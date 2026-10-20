using System.Collections.Concurrent;
using System.Text.Json;
using FlowPlane.Application.Providers;
using FlowPlane.Application.Providers.DiscoveryInventory;
using FlowPlane.Application.Secrets;
using FlowPlane.Domain.Providers;
using FlowPlane.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FlowPlane.Persistence.Tests;

public sealed class DiscoveryInventoryFailureTests
{
    [Theory]
    [InlineData(ProviderDiscoveryOutcome.Failed)]
    [InlineData(ProviderDiscoveryOutcome.Unavailable)]
    [InlineData(ProviderDiscoveryOutcome.Partial)]
    public async Task Noncomplete_scan_or_count_disagreement_cannot_replace_published_inventory(ProviderDiscoveryOutcome outcome)
    {
        ProviderDiscoveryInventoryTests.RequireMigrationApproval();
        var token = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        Assert.SkipWhen(database is null, "Configure isolated PostgreSQL 18.");
        await using var services = database!.CreateServices(null);
        await database.MigrateAsync(services, token);
        var id = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, token);
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderDiscoveryInventoryStore>();
        await using var prior = await store.BeginRefreshAsync(id, token);
        await prior.PublishAsync(ProviderDiscoveryInventoryTests.Summary(0), token);
        await using var failed = await store.BeginRefreshAsync(id, token);
        await failed.Sink.AppendAsync(ProviderDiscoveryInventoryTests.Observation(id, "provisional"), token);
        var error = await Assert.ThrowsAsync<DiscoveryInventoryException>(() => failed.PublishAsync(
            ProviderDiscoveryInventoryTests.Summary(2) with { Outcome = outcome, ScanCompleted = false }, token));
        Assert.Equal(DiscoveryInventoryError.IncompleteScan, error.Code);
        await failed.AbortAsync(error.Code, token);
        await using var wrongCount = await store.BeginRefreshAsync(id, token);
        await wrongCount.Sink.AppendAsync(ProviderDiscoveryInventoryTests.Observation(id, "one"), token);
        await Assert.ThrowsAsync<DiscoveryInventoryException>(() => wrongCount.PublishAsync(ProviderDiscoveryInventoryTests.Summary(2), token));
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        Assert.Equal(prior.RefreshId, (await db.DiscoveryStates.AsNoTracking().SingleAsync(token)).CurrentGenerationId);
    }

    [Theory]
    [InlineData(4, 262144, 3, 0)]
    [InlineData(4, 262144, 4, 4)]
    [InlineData(128, 1, 1, 1)]
    public async Task Awaited_staging_flushes_at_row_or_byte_bound_without_buffering_the_catalog(int rows, int bytes, int append, int durable)
    {
        ProviderDiscoveryInventoryTests.RequireMigrationApproval();
        var token = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        Assert.SkipWhen(database is null, "Configure isolated PostgreSQL 18.");
        await using var services = database!.CreateServices(null, registrations =>
            registrations.AddSingleton(new DiscoveryInventoryOptions { WriteBatchSize = rows, WriteBufferBytes = bytes }));
        await database.MigrateAsync(services, token);
        var id = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, token);
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderDiscoveryInventoryStore>();
        await using var staging = await store.BeginRefreshAsync(id, token);
        for (var index = 0; index < append; index++) await staging.Sink.AppendAsync(ProviderDiscoveryInventoryTests.Observation(id, $"row-{index}"), token);
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        Assert.Equal(durable, await db.DiscoveryObservations.CountAsync(token));
        Assert.Null((await db.DiscoveryStates.AsNoTracking().SingleAsync(token)).CurrentGenerationId);
        await staging.PublishAsync(ProviderDiscoveryInventoryTests.Summary(append), token);
        Assert.Equal(append, await db.DiscoveryObservations.CountAsync(token));
    }

    [Fact]
    public async Task Provider_timeout_and_caller_cancellation_leave_safe_durable_receipts()
    {
        ProviderDiscoveryInventoryTests.RequireMigrationApproval();
        var token = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        Assert.SkipWhen(database is null, "Configure isolated PostgreSQL 18.");
        await using var services = database!.CreateServices(null);
        await database.MigrateAsync(services, token);
        var id = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, token);
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderDiscoveryInventoryStore>();
        var capability = new Capability(async (instance, sink, cancellation) =>
        {
            await sink.AppendAsync(ProviderDiscoveryInventoryTests.Observation(instance.Id, "provisional"), cancellation);
            await Task.Delay(Timeout.Infinite, cancellation);
            throw new InvalidOperationException("Unreachable.");
        });
        var coordinator = new ProviderDiscoveryRefreshService(store, [capability], new() { ScanTimeoutSeconds = 1 });
        var timeout = await coordinator.RefreshAsync(id, token);
        Assert.Equal(DiscoveryInventoryError.TimedOut, timeout.Error);
        Assert.Equal(DiscoveryPublicationStatus.NotPublished, (await store.ReconcileAsync(timeout.RefreshId!.Value, token)).Status);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        caller.CancelAfter(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.RefreshAsync(id, caller.Token));
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        Assert.Null((await db.DiscoveryStates.AsNoTracking().SingleAsync(token)).CurrentGenerationId);
    }

    [Fact]
    public async Task Raw_inventory_rows_results_logs_and_errors_do_not_disclose_secret_material()
    {
        ProviderDiscoveryInventoryTests.RequireMigrationApproval();
        var token = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        Assert.SkipWhen(database is null, "Configure isolated PostgreSQL 18.");
        var capture = new Diagnostics();
        await using var services = database!.CreateServices(null, registrations =>
            registrations.AddLogging(logging => logging.AddProvider(capture).SetMinimumLevel(LogLevel.Trace)));
        await database.MigrateAsync(services, token);
        var id = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, token);
        var variable = $"FLOWPLANE_INVENTORY_CANARY_{Guid.NewGuid():N}";
        var canary = $"secret-{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variable, canary);
        try
        {
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
            // Replace only the controlled test ProviderInstance so resolution genuinely uses this canary.
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"ProviderInstances\" SET \"SecretReference\" = {"env:" + variable} WHERE \"Id\" = {id}", token);
            var resolver = scope.ServiceProvider.GetRequiredService<ISecretResolver>();
            var store = scope.ServiceProvider.GetRequiredService<IProviderDiscoveryInventoryStore>();
            var capability = new Capability(async (instance, sink, cancellation) =>
            {
                Assert.True(await resolver.UseAsync(instance.SecretReference, (value, _) => ValueTask.FromResult(value.Span.SequenceEqual(canary.AsSpan())), cancellation));
                await sink.AppendAsync(ProviderDiscoveryInventoryTests.Observation(instance.Id, "approved-native-key"), cancellation);
                return ProviderDiscoveryInventoryTests.Summary(1);
            });
            var result = await new ProviderDiscoveryRefreshService(store, [capability], new()).RefreshAsync(id, token);
            Assert.Equal(DiscoveryPublicationStatus.Published, result.Status);
            foreach (var sql in new[]
            {
                "SELECT row_to_json(t)::text AS \"Value\" FROM \"ProviderDiscoveryStates\" t",
                "SELECT row_to_json(t)::text AS \"Value\" FROM \"ProviderDiscoveryGenerations\" t",
                "SELECT row_to_json(t)::text AS \"Value\" FROM \"ProviderDiscoveryObservations\" t",
            })
            {
                var rows = await db.Database.SqlQueryRaw<string>(sql).ToListAsync(token);
                foreach (var row in rows) Safe(row);
            }
            await using var reader = await store.OpenReaderAsync(id, result.RefreshId!.Value, token);
            await foreach (var row in reader.ReadAsync(null, token)) Safe(JsonSerializer.Serialize(row));
            Safe(JsonSerializer.Serialize(result));
            foreach (var message in capture.Messages) Safe(message);

            void Safe(string value)
            {
                Assert.False(value.Contains(canary, StringComparison.Ordinal), "Secret canary appeared in diagnostics or inventory.");
                Assert.False(value.Contains(variable, StringComparison.Ordinal), "Secret locator appeared in diagnostics or inventory.");
                Assert.False(value.Contains("Password=", StringComparison.OrdinalIgnoreCase), "Connection string appeared in diagnostics or inventory.");
                Assert.False(value.Contains("PRIVATE KEY", StringComparison.Ordinal), "Private key appeared in inventory.");
                Assert.False(value.Contains("default_value", StringComparison.OrdinalIgnoreCase), "Unapproved value field appeared in inventory.");
            }
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    private sealed class Capability(Func<ProviderInstance, IProviderDiscoverySink, CancellationToken, Task<ProviderDiscoveryScanResult>> scan) : IProviderDiscoveryCapability
    {
        public ProviderType ProviderType => ProviderType.Parse("ssis");
        public Task<ProviderDiscoveryScanResult> ScanAsync(ProviderInstance instance, IProviderDiscoverySink sink, CancellationToken cancellationToken = default) => scan(instance, sink, cancellationToken);
    }
    private sealed class Diagnostics : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
        public void Dispose() { }
    }
}
