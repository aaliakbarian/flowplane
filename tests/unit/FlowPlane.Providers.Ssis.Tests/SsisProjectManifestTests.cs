using System.Diagnostics;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

using FlowPlane.Providers.Ssis.Discovery;

namespace FlowPlane.Providers.Ssis.Tests;

public sealed class SsisProjectManifestTests
{
    [Fact]
    public void Unsupported_temporary_filesystem_fails_without_creating_a_named_fallback()
    {
        if (!OperatingSystem.IsLinux()) return;
        Assert.Throws<IOException>(() => SsisManifestFile.Open("/proc"));
        var directory = Directory.CreateTempSubdirectory("flowplane-spool-init-");
        try
        {
            Assert.Throws<IOException>(() => SsisManifestFile.Open(Path.Combine(directory.FullName, "missing")));
            Assert.Empty(directory.EnumerateFileSystemInfos());
        }
        finally { directory.Delete(); }
    }

    [Fact]
    public async Task Windows_file_has_an_explicit_owner_only_acl_and_is_deleted_on_close()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent();
        var file = SsisManifestFile.Open();
        var path = file.Name;
        await using (var manifest = new SsisProjectManifest(file))
        {
            var acl = file.GetAccessControl();
            Assert.True(acl.AreAccessRulesProtected);
            Assert.Equal(identity.User, acl.GetOwner(typeof(SecurityIdentifier)));
            var rule = Assert.Single(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>());
            Assert.Equal(identity.User, rule.IdentityReference);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            await manifest.AppendAsync(DiscoveryTestData.Project(), TestContext.Current.CancellationToken);
        }
        Assert.False(File.Exists(path));
    }
    [Theory]
    [InlineData("complete")]
    [InlineData("exception")]
    [InlineData("cancel")]
    public async Task Owned_storage_is_closed_on_every_normal_unwind(string mode)
    {
        var storage = new MemoryStream();
        try
        {
            await using var manifest = new SsisProjectManifest(storage);
            await manifest.AppendAsync(DiscoveryTestData.Project(), TestContext.Current.CancellationToken);
            if (mode == "exception") throw new IOException("controlled failure");
            if (mode == "cancel") throw new OperationCanceledException();
            await foreach (var batch in manifest.ReadBatchesAsync(1, TestContext.Current.CancellationToken))
                Assert.Single(batch);
        }
        catch (IOException) when (mode == "exception") { }
        catch (OperationCanceledException) when (mode == "cancel") { }
        Assert.False(storage.CanRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Constructor_failure_closes_owned_storage_and_preserves_the_primary_failure(bool cleanupFails)
    {
        var storage = new FaultingStream { ReadOnly = true, FailDispose = cleanupFails };
        Assert.Throws<ArgumentException>(() => new SsisProjectManifest(storage));
        Assert.False(storage.CanRead);
    }

    [Theory]
    [InlineData("write")]
    [InlineData("flush")]
    [InlineData("close")]
    public async Task Storage_failures_are_observable_and_release_owned_resources(string operation)
    {
        var storage = new FaultingStream();
        var manifest = new SsisProjectManifest(storage);
        try
        {
            if (operation == "write")
            {
                storage.FailWrite = true;
                await Assert.ThrowsAsync<IOException>(() =>
                    manifest.AppendAsync(DiscoveryTestData.Project(), TestContext.Current.CancellationToken));
            }
            else
            {
                await manifest.AppendAsync(DiscoveryTestData.Project(), TestContext.Current.CancellationToken);
                storage.FailFlush = operation == "flush";
                if (operation == "flush")
                {
                    await Assert.ThrowsAsync<IOException>(async () =>
                    {
                        await foreach (var row in manifest.ReadBatchesAsync(1, TestContext.Current.CancellationToken)) { }
                    });
                }
            }
        }
        finally
        {
            storage.FailDispose = operation == "close";
            if (operation == "close")
                await Assert.ThrowsAsync<IOException>(async () => await manifest.DisposeAsync());
            else
                await manifest.DisposeAsync();
        }
        Assert.False(storage.CanRead);
    }

    [Fact]
    public async Task Spool_contains_only_the_approved_manifest_fields_not_provider_or_secret_material()
    {
        var storage = new MemoryStream();
        await using var manifest = new SsisProjectManifest(storage);
        await manifest.AppendAsync(DiscoveryTestData.Project(), TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(storage.ToArray());
        Assert.Equal(new[] { "FolderId", "FolderName", "ProjectId", "ProjectName", "ProjectFormatVersion",
            "ObjectVersionLsn", "LastDeployedAt" }, json.RootElement.EnumerateObject().Select(item => item.Name));
    }

    [Fact]
    public async Task Linux_manifest_has_no_named_entry_while_open()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var manifest = new SsisProjectManifest();
        var stream = Assert.IsType<FileStream>(typeof(SsisProjectManifest)
            .GetField("stream", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manifest));
        var descriptor = $"/proc/self/fd/{stream.SafeFileHandle.DangerousGetHandle()}";
        Assert.EndsWith(" (deleted)", new FileInfo(descriptor).LinkTarget!, StringComparison.Ordinal);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(descriptor));
        await manifest.AppendAsync(DiscoveryTestData.Project(), TestContext.Current.CancellationToken);
        await foreach (var batch in manifest.ReadBatchesAsync(1, TestContext.Current.CancellationToken))
            Assert.Single(batch);
    }

    [Fact]
    public async Task Linux_disk_full_write_is_not_retried_during_cleanup()
    {
        if (!OperatingSystem.IsLinux()) return;
        var file = new FileStream("/dev/full", FileMode.Open, FileAccess.ReadWrite,
            FileShare.ReadWrite, bufferSize: 1);
        var manifest = new SsisProjectManifest(file);
        try
        {
            await Assert.ThrowsAsync<IOException>(() =>
                manifest.AppendAsync(DiscoveryTestData.Project(), TestContext.Current.CancellationToken));
        }
        finally { await manifest.DisposeAsync(); }
        Assert.True(file.SafeFileHandle.IsClosed);
    }

    [Fact]
    public async Task Abrupt_process_termination_leaves_no_manifest_path()
    {
        if (!OperatingSystem.IsLinux()) return;
        const string markerKey = "FLOWPLANE_MANIFEST_CHILD_MARKER";
        if (Environment.GetEnvironmentVariable(markerKey) is { } marker)
        {
            await using var manifest = new SsisProjectManifest();
            await manifest.AppendAsync(DiscoveryTestData.Project(), CancellationToken.None);
            await foreach (var batch in manifest.ReadBatchesAsync(1, CancellationToken.None)) { }
            await File.WriteAllTextAsync(marker, "ready", CancellationToken.None);
            await Task.Delay(Timeout.InfiniteTimeSpan, TestContext.Current.CancellationToken);
            return;
        }

        var directory = Directory.CreateTempSubdirectory("flowplane-spool-test-");
        var spoolDirectory = directory.CreateSubdirectory("spool");
        var ready = Path.Combine(directory.FullName, "ready");
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var start = new ProcessStartInfo(host) { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(SsisProjectManifestTests).Assembly.Location);
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add($"{typeof(SsisProjectManifestTests).FullName}.{nameof(Abrupt_process_termination_leaves_no_manifest_path)}");
        start.Environment[markerKey] = ready;
        start.Environment["TMPDIR"] = spoolDirectory.FullName;
        start.Environment["DOTNET_EnableDiagnostics"] = "0";
        using var child = Process.Start(start)!;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!File.Exists(ready))
            {
                Assert.False(child.HasExited, "Manifest subprocess exited before initialization.");
                await Task.Delay(20, deadline.Token);
            }
            child.Kill(); // SIGKILL on Linux, not graceful managed disposal
            await child.WaitForExitAsync(deadline.Token);
            Assert.Empty(Directory.EnumerateFileSystemEntries(spoolDirectory.FullName));
        }
        finally
        {
            if (!child.HasExited) { child.Kill(); child.WaitForExit(); }
            directory.Delete(recursive: true);
        }
    }

    private sealed class FaultingStream : MemoryStream
    {
        public bool ReadOnly { get; init; }
        public bool FailWrite { get; set; }
        public bool FailFlush { get; set; }
        public bool FailDispose { get; set; }
        public override bool CanWrite => !ReadOnly && base.CanWrite;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) =>
            FailWrite ? ValueTask.FromException(new IOException("controlled storage write failure")) : base.WriteAsync(buffer, token);
        public override Task FlushAsync(CancellationToken token) =>
            FailFlush ? Task.FromException(new IOException("controlled storage flush failure")) : base.FlushAsync(token);
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (FailDispose) throw new IOException("controlled storage cleanup failure");
        }
    }
}
