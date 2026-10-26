using System.Diagnostics;

using FlowPlane.Application.Providers.DiscoveryHosting;
using FlowPlane.Application.Providers.DiscoveryInventory;
using FlowPlane.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace FlowPlane.Persistence.Tests;

public sealed class HostedDiscoveryProcessRecoveryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SIGKILL_recovery_preserves_accepted_intent_fences_started_scans_and_reconciles_publication()
    {
        if (Environment.GetEnvironmentVariable("FLOWPLANE_TEST_HOSTED_CRASH_PHASE") is { } childPhase)
        {
            var connection = Environment.GetEnvironmentVariable("FLOWPLANE_TEST_POSTGRES_CONNECTION_STRING")!;
            Assert.StartsWith("flowplane_a09_", new NpgsqlConnectionStringBuilder(connection).Database);
            var id = Guid.Parse(Environment.GetEnvironmentVariable("FLOWPLANE_TEST_HOSTED_CRASH_PROVIDER")!);
            var refresh = Guid.Parse(Environment.GetEnvironmentVariable("FLOWPLANE_TEST_HOSTED_CRASH_REFRESH")!);
            await using var services = PostgreSqlTestDatabase.CreateServicesForConnection(connection, null, Configure);
            await using var scope = services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IHostedDiscoveryStore>();
            await store.AdmitAsync(new(refresh, id, 1, new("https://fixture.test", "operator")), Token);
            if (childPhase != "Accepted")
            {
                var work = (await store.TryAcquireAsync(Guid.NewGuid(), Token))!;
                if (childPhase == "Published") await work.Session.PublishAsync(ProviderDiscoveryInventoryTests.Summary(0), Token);
                else
                {
                    // Flush real provisional observations before killing the process, not just an in-memory buffer.
                    for (var index = 0; index < 128; index++)
                        await work.Session.Sink.AppendAsync(ProviderDiscoveryInventoryTests.Observation(id, "provisional-" + index), Token);
                    if (childPhase == "Ready")
                        await scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>().Database.ExecuteSqlInterpolatedAsync($"""
                            UPDATE "ProviderDiscoveryGenerations" SET "Status"='Ready', "PublicationAttemptedAtUtc"=clock_timestamp()
                            WHERE "RefreshId"={refresh}
                            """, Token);
                }
            }
            // Deliberately no session disposal, CompleteAsync or graceful BackgroundService shutdown.
            await Task.Delay(Timeout.InfiniteTimeSpan, Token);
            return;
        }
        foreach (var phase in new[] { "Accepted", "Staging", "Ready", "Published" })
        {
            await using var database = await PostgreSqlTestDatabase.CreateAsync(Token);
            Assert.SkipWhen(database is null, "Configure isolated PostgreSQL 18.");
            await using var services = database!.CreateServices(null, Configure);
            await database.MigrateAsync(services, Token);
            var id = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, Token);
            var refresh = Guid.CreateVersion7();
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("vstest");
            start.ArgumentList.Add(typeof(HostedDiscoveryProcessRecoveryTests).Assembly.Location);
            start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName~SIGKILL_recovery_preserves_accepted_intent");
            start.Environment["FLOWPLANE_TEST_POSTGRES_CONNECTION_STRING"] = database.ConnectionString;
            start.Environment["FLOWPLANE_TEST_HOSTED_CRASH_PHASE"] = phase;
            start.Environment["FLOWPLANE_TEST_HOSTED_CRASH_PROVIDER"] = id.ToString();
            start.Environment["FLOWPLANE_TEST_HOSTED_CRASH_REFRESH"] = refresh.ToString();
            using var child = Process.Start(start)!;
            var stdout = child.StandardOutput.ReadToEndAsync(Token);
            var stderr = child.StandardError.ReadToEndAsync(Token);
            try
            {
                await using var scope = services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
                var elapsed = Stopwatch.StartNew();
                var reached = false;
                while (elapsed.Elapsed < TimeSpan.FromSeconds(30))
                {
                    Assert.False(child.HasExited, "Controlled hosted subprocess exited before its kill point.");
                    var row = await db.DiscoveryGenerations.AsNoTracking().SingleOrDefaultAsync(item => item.RefreshId == refresh, Token);
                    reached = row?.Status.ToString() == phase && (phase is "Accepted" or "Published" ||
                        await db.DiscoveryObservations.CountAsync(item => item.RefreshId == refresh, Token) == 128);
                    if (reached) break;
                    await Task.Delay(50, Token);
                }
                Assert.True(reached, "Subprocess did not reach its durable crash boundary.");
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync(Token);
                await Task.WhenAll(stdout, stderr); // Never print private child diagnostics/environment.
                if (phase != "Accepted")
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"ProviderDiscoveryGenerations\" SET \"LeaseExpiresAtUtc\"=clock_timestamp()-interval '1 second' WHERE \"RefreshId\"={refresh}", Token);
                await using var restarted = database.CreateServices(null, Configure);
                await using var restartScope = restarted.CreateAsyncScope();
                var store = restartScope.ServiceProvider.GetRequiredService<IHostedDiscoveryStore>();
                var work = await store.TryAcquireAsync(Guid.NewGuid(), Token);
                if (phase == "Accepted")
                {
                    Assert.NotNull(work);
                    Assert.Equal(refresh, work.Session.RefreshId);
                    await work.Session.AbortAsync(DiscoveryInventoryError.Cancelled, Token);
                    await work.Session.DisposeAsync();
                }
                else
                {
                    Assert.Null(work); // Never resume a scan or issue a second claim after interruption.
                    Assert.Equal(phase == "Published" ? HostedDiscoveryPhase.Published : HostedDiscoveryPhase.Abandoned,
                        (await store.FindAsync(id, refresh, Token))!.Phase);
                    Assert.Equal(phase == "Published" ? refresh : (Guid?)null,
                        (await db.DiscoveryStates.AsNoTracking().SingleAsync(Token)).CurrentGenerationId);
                }
                Assert.Equal(1, await db.DiscoveryGenerations.CountAsync(Token));
            }
            finally
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private static void Configure(IServiceCollection services) => services.AddSingleton(new DiscoveryHostingOptions { Enabled = true });
}
