using System.Diagnostics;
using FlowPlane.Application.Providers.DiscoveryInventory;
using FlowPlane.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FlowPlane.Persistence.Tests;

public sealed class DiscoveryInventoryProcessRecoveryTests
{
    [Fact]
    public async Task Killed_staging_process_preserves_previous_inventory_and_is_fenced_after_restart()
    {
        ProviderDiscoveryInventoryTests.RequireMigrationApproval();
        var token = TestContext.Current.CancellationToken;
        if (Environment.GetEnvironmentVariable("FLOWPLANE_TEST_INVENTORY_CRASH_PROVIDER") is { } childId)
        {
            var connection = Environment.GetEnvironmentVariable("FLOWPLANE_TEST_POSTGRES_CONNECTION_STRING")!;
            var builder = new NpgsqlConnectionStringBuilder(connection);
            Assert.StartsWith("flowplane_a09_", builder.Database);
            await using var childServices = PostgreSqlTestDatabase.CreateServicesForConnection(connection, null);
            await using var childScope = childServices.CreateAsyncScope();
            await using var staging = await childScope.ServiceProvider.GetRequiredService<IProviderDiscoveryInventoryStore>()
                .BeginRefreshAsync(Guid.Parse(childId), token);
            for (var index = 0; index < 128; index++)
                await staging.Sink.AppendAsync(ProviderDiscoveryInventoryTests.Observation(staging.Instance.Id, $"provisional-{index}"), token);
            await Task.Delay(Timeout.Infinite, token); // Parent kills the actual process tree after observing committed staging.
            return;
        }
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        Assert.SkipWhen(database is null, "Configure isolated PostgreSQL 18.");
        await using var services = database!.CreateServices(null);
        await database.MigrateAsync(services, token);
        var id = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, token);
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderDiscoveryInventoryStore>();
        await using var previous = await store.BeginRefreshAsync(id, token);
        await previous.PublishAsync(ProviderDiscoveryInventoryTests.Summary(0), token);
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(DiscoveryInventoryProcessRecoveryTests).Assembly.Location);
        start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName~Killed_staging_process_preserves_previous_inventory_and_is_fenced_after_restart");
        start.Environment["FLOWPLANE_TEST_POSTGRES_CONNECTION_STRING"] = database.ConnectionString;
        start.Environment["FLOWPLANE_TEST_INVENTORY_CRASH_PROVIDER"] = id.ToString();
        using var child = Process.Start(start)!;
        // Drain diagnostics without printing child environment, credentials or SQL.
        var output = child.StandardOutput.ReadToEndAsync(token);
        var errors = child.StandardError.ReadToEndAsync(token);
        try
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
            Guid? abandoned = null;
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(30))
            {
                Assert.False(child.HasExited, "The controlled staging subprocess stopped before the kill point.");
                var state = await db.DiscoveryStates.AsNoTracking().SingleAsync(token);
                if (state.InFlightGenerationId is { } refresh && await db.DiscoveryObservations.CountAsync(row => row.RefreshId == refresh, token) == 128)
                {
                    abandoned = refresh;
                    break;
                }
                await Task.Delay(50, token);
            }
            Assert.NotNull(abandoned);
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(token);
            await Task.WhenAll(output, errors);
            Assert.Equal(previous.RefreshId, (await db.DiscoveryStates.AsNoTracking().SingleAsync(token)).CurrentGenerationId);
            // Expire only the abandoned claim in the owned test DB; do not wait an hour or alter PostgreSQL time.
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"ProviderDiscoveryGenerations\" SET \"StartedAtUtc\" = clock_timestamp() - interval '2 hours', \"ExpiresAtUtc\" = clock_timestamp() - interval '1 hour' WHERE \"RefreshId\" = {abandoned}", token);
            await using var restarted = database.CreateServices(null);
            await using var restartedScope = restarted.CreateAsyncScope();
            var recovered = restartedScope.ServiceProvider.GetRequiredService<IProviderDiscoveryInventoryStore>();
            await using var next = await recovered.BeginRefreshAsync(id, token);
            Assert.Equal(DiscoveryPublicationStatus.NotPublished, (await recovered.ReconcileAsync(abandoned.Value, token)).Status);
            Assert.Equal(DiscoveryPublicationStatus.Published, (await next.PublishAsync(ProviderDiscoveryInventoryTests.Summary(0), token)).Status);
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(CancellationToken.None);
        }
    }
}
