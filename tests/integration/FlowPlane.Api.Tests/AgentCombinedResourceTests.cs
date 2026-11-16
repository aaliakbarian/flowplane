using System.Diagnostics;
using System.Runtime.ExceptionServices;

using FlowPlane.Persistence.Tests;

using Npgsql;

namespace FlowPlane.Api.Tests;

public sealed class AgentCombinedResourceTests
{
    [Fact]
    public async Task Child_host_runs_the_current_test_build_configuration()
    {
        await using var fixture = await AgentCombinedFixture.CreateAsync();
        Assert.True(fixture.ProcessId > 0); // Startup verifies the actual child assembly configuration, not a source/path string.
    }

    [Fact]
    public async Task Failed_preparation_after_private_PFX_creation_removes_its_owned_database_and_certificate()
    {
        PostgreSqlTestDatabase? owned = null;
        DirectoryInfo? directory = null;
        try
        {
            await Assert.ThrowsAsync<FixturePreparationException>(() => AgentCombinedFixture.CreateAsync((database, path) =>
            {
                owned = database; directory = path;
                throw new FixturePreparationException();
            }));
            Assert.NotNull(owned);
            Assert.False(await ExistsAsync(owned.DatabaseName));
            Assert.False(Directory.Exists(directory!.FullName));
        }
        finally
        {
            if (directory is not null && Directory.Exists(directory.FullName))
            {
                foreach (var file in directory.GetFiles("*.pfx"))
                {
                    using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Write, FileShare.None))
                    { stream.Write(new byte[stream.Length]); stream.Flush(true); }
                    file.Delete();
                }
                directory.Delete();
            }
            if (owned is not null && await ExistsAsync(owned.DatabaseName)) await owned.DisposeAsync();
        }
    }

    [Fact]
    public async Task Unexpected_private_directory_file_is_preserved_without_preventing_owned_database_cleanup()
        => await ExerciseSentinelAsync(_ => { },
            (path, token) => File.WriteAllTextAsync(path, "non-secret-sentinel", token),
            File.ReadAllTextAsync, AgentCombinedFixture.Token);

    [Theory]
    [InlineData("cancel-before-write")]
    [InlineData("write")]
    [InlineData("cancel-cleanup")]
    [InlineData("read")]
    [InlineData("write-and-read")]
    public async Task Sentinel_failure_paths_release_owned_resources_and_preserve_primary_failure(string phase)
    {
        AgentCombinedFixture? fixture = null;
        var processId = 0;
        using var cancellation = new CancellationTokenSource();
        var original = phase == "cancel-before-write"
            ? (Exception)new OperationCanceledException(cancellation.Token) : new IOException("Synthetic primary failure.");
        var readFailure = new IOException("Synthetic cleanup read failure.");
        ExceptionDispatchInfo? failureEvidence = null;
        try
        {
            var failure = await Record.ExceptionAsync(() => ExerciseSentinelAsync(value =>
            { fixture = value; processId = value.ProcessId; }, async (path, token) =>
            {
                if (phase == "cancel-before-write") { cancellation.Cancel(); throw original; }
                if (phase == "write") throw original;
                await File.WriteAllTextAsync(path, "non-secret-sentinel", token);
                if (phase == "write-and-read") throw original;
                if (phase == "cancel-cleanup") cancellation.Cancel();
            }, (path, token) => phase is "read" or "write-and-read"
                ? Task.FromException<string>(readFailure) : File.ReadAllTextAsync(path, token), cancellation.Token));

            Assert.NotNull(fixture);
            Assert.False(await ExistsAsync(fixture.OwnedDatabase.DatabaseName));
            Assert.False(ProcessExists(processId));
            Assert.False(File.Exists(Path.Combine(fixture.TemporaryDirectory, "disposable-data-protection.pfx")));
            if (phase is "read" or "write-and-read")
            {
                Assert.True(File.Exists(Path.Combine(fixture.TemporaryDirectory, "owned-test-sentinel.txt")));
                var cleanup = phase == "read" ? Assert.IsType<AggregateException>(failure)
                    : Assert.IsType<AggregateException>(failure!.Data["CleanupFailures"]);
                Assert.Contains(readFailure, cleanup.InnerExceptions);
            }
            else Assert.False(Directory.Exists(fixture.TemporaryDirectory));

            if (phase is "cancel-before-write" or "write" or "write-and-read") Assert.Same(original, failure);
            if (phase == "cancel-cleanup") Assert.Null(failure);
        }
        catch (Exception exception) { failureEvidence = ExceptionDispatchInfo.Capture(exception); }
        finally
        {
            // Independent fallback also cleans deliberate RED failures; never delete unread or unknown contents.
            if (fixture is not null)
            {
                var cleanupFailures = new List<Exception>();
                await CleanupAsync("fallback-sentinel", async () =>
                {
                    var path = Path.Combine(fixture.TemporaryDirectory, "owned-test-sentinel.txt");
                    if (!File.Exists(path)) return;
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length != "non-secret-sentinel".Length)
                        throw new IOException("Unexpected fallback sentinel identity; resource preserved.");
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    if (await File.ReadAllTextAsync(path, deadline.Token).WaitAsync(deadline.Token) != "non-secret-sentinel")
                        throw new IOException("Unexpected fallback sentinel contents; resource preserved.");
                    File.Delete(path);
                }, cleanupFailures);
                await CleanupAsync("fallback-fixture", async () =>
                {
                    if (fixture.OwnedProcess is not null || File.Exists(Path.Combine(fixture.TemporaryDirectory, "disposable-data-protection.pfx")) ||
                        await ExistsAsync(fixture.OwnedDatabase.DatabaseName))
#pragma warning disable xUnit1051 // The independent cleanup deadline must not inherit test cancellation.
                        await fixture.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60), CancellationToken.None);
#pragma warning restore xUnit1051
                }, cleanupFailures);
                await CleanupAsync("fallback-directory", () =>
                {
                    if (Directory.Exists(fixture.TemporaryDirectory)) Directory.Delete(fixture.TemporaryDirectory);
                    return Task.CompletedTask;
                }, cleanupFailures);
                ReportCleanupFailures(failureEvidence, cleanupFailures);
            }
        }
        failureEvidence?.Throw();
    }

    private static bool ProcessExists(int id)
    {
        try { using var process = Process.GetProcessById(id); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static async Task ExerciseSentinelAsync(Action<AgentCombinedFixture> acquired,
        Func<string, CancellationToken, Task> write, Func<string, CancellationToken, Task<string>> read, CancellationToken token)
    {
        ExceptionDispatchInfo? failure = null;
        var disposalAttempted = false;
        var cleanupFailures = new List<Exception>();
        var fixture = await AgentCombinedFixture.CreateAsync();
        try
        {
            acquired(fixture);
            var sentinel = Path.Combine(fixture.TemporaryDirectory, "owned-test-sentinel.txt");
            await write(sentinel, token);
            disposalAttempted = true;
            await Assert.ThrowsAsync<IOException>(() => fixture.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60), CancellationToken.None));
            Assert.True(File.Exists(sentinel));
            Assert.False(await ExistsAsync(fixture.OwnedDatabase.DatabaseName));
        }
        catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
        finally
        {
            // Separate deadlines: a failed/cancelled read must not prevent child/PFX/guarded database disposal.
            await CleanupAsync("sentinel", async () =>
            {
                var sentinel = Path.Combine(fixture.TemporaryDirectory, "owned-test-sentinel.txt");
                if (!File.Exists(sentinel)) return;
                if ((File.GetAttributes(sentinel) & FileAttributes.ReparsePoint) != 0 || new FileInfo(sentinel).Length != "non-secret-sentinel".Length)
                    throw new IOException("Unexpected sentinel identity; resource preserved.");
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                if (await read(sentinel, deadline.Token).WaitAsync(deadline.Token) != "non-secret-sentinel")
                    throw new IOException("Unexpected sentinel contents; resource preserved.");
                File.Delete(sentinel);
            }, cleanupFailures);
            if (!disposalAttempted)
                await CleanupAsync("fixture", () => fixture.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60), CancellationToken.None), cleanupFailures);
            await CleanupAsync("directory", () =>
            {
                if (Directory.Exists(fixture.TemporaryDirectory)) Directory.Delete(fixture.TemporaryDirectory);
                return Task.CompletedTask; // Nonrecursive: unread or unexpected contents are preserved.
            }, cleanupFailures);
            ReportCleanupFailures(failure, cleanupFailures);
        }
        failure?.Throw(); // Keep the original failure and stack, not the last cleanup failure.
    }

    private static async Task CleanupAsync(string phase, Func<Task> cleanup, List<Exception> failures)
    {
        try { await cleanup(); }
        catch (Exception exception)
        {
            failures.Add(exception);
            TestContext.Current.SendDiagnosticMessage("Isolated cleanup failure ({0}: {1}).", phase, exception.GetType().Name);
        }
    }

    private static void ReportCleanupFailures(ExceptionDispatchInfo? original, List<Exception> failures)
    {
        if (failures.Count == 0) return;
        var cleanup = new AggregateException("Isolated resource cleanup failed; inspect separate cleanup evidence.", failures);
        if (original is null) throw cleanup;
        original.SourceException.Data["CleanupFailures"] = cleanup;
    }

    [Fact]
    public async Task Closed_control_pipe_still_stops_the_owned_child_and_cleans_its_database()
    {
        var fixture = await AgentCombinedFixture.CreateAsync();
        var child = fixture.OwnedProcess;
        var database = fixture.OwnedDatabase;
        child.StandardInput.Dispose();
        try
        {
            await fixture.DisposeAsync();
            Assert.False(await ExistsAsync(database.DatabaseName));
        }
        finally
        {
            // A failing RED run still cleans only this test's owned process/database.
            try
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
#pragma warning disable xUnit1051 // Owned-process cleanup must still finish after the test's cancellation token is cancelled.
                await child.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
#pragma warning restore xUnit1051
            }
            catch (InvalidOperationException) { } // The successful cleanup already disposed this Process object.
            child.Dispose();
            if (await ExistsAsync(database.DatabaseName)) await database.DisposeAsync();
        }
    }

    private static async Task<bool> ExistsAsync(string name)
    {
        PostgreSqlTestDatabase.RequireCurrentRunDatabase(name);
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FLOWPLANE_TEST_POSTGRES_CONNECTION_STRING"))
        { Database = "postgres", Pooling = false };
        await using var admin = new NpgsqlConnection(connection.ConnectionString);
        await admin.OpenAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_database WHERE datname=@name)", admin);
        command.Parameters.AddWithValue("name", name);
        return (bool)(await command.ExecuteScalarAsync(CancellationToken.None))!;
    }

    private sealed class FixturePreparationException : Exception;
}
