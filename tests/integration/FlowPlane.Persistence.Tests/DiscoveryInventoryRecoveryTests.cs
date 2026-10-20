using System.Data.Common;
using System.Text.Json;

using FlowPlane.Application.Providers;
using FlowPlane.Application.Providers.DiscoveryInventory;
using FlowPlane.Domain.Providers;
using FlowPlane.Infrastructure.Persistence;
using FlowPlane.Infrastructure.Persistence.Discovery;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace FlowPlane.Persistence.Tests;

public sealed class DiscoveryInventoryRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_at_actual_commit_returns_the_refresh_id_for_durable_reconciliation(bool committed)
    {
        ProviderDiscoveryInventoryTests.RequireMigrationApproval();
        var token = TestContext.Current.CancellationToken;
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        var fault = new CommitFault(committed, caller.Cancel);
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        Assert.SkipWhen(database is null, "Configure isolated PostgreSQL 18.");
        await using var services = database!.CreateServices(null, registrations =>
            registrations.ConfigureDbContext<FlowPlaneDbContext>(options => options.AddInterceptors(fault)));
        await database.MigrateAsync(services, token);
        var id = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, token);
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderDiscoveryInventoryStore>();
        var coordinator = new ProviderDiscoveryRefreshService(store, [new CommitCancellationCapability(async cancellation =>
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
            var refresh = (await db.DiscoveryStates.AsNoTracking().SingleAsync(cancellation)).InFlightGenerationId!.Value;
            fault.Arm(refresh);
            return ProviderDiscoveryInventoryTests.Summary(0);
        })], new());
        var exception = await Assert.ThrowsAsync<ProviderDiscoveryRefreshCanceledException>(() => coordinator.RefreshAsync(id, caller.Token));
        Assert.NotNull(exception.RefreshId);
        Assert.Equal(caller.Token, exception.CancellationToken);
        var receipt = await store.ReconcileAsync(exception.RefreshId.Value, token);
        Assert.Equal(committed ? DiscoveryPublicationStatus.Published : DiscoveryPublicationStatus.Uncertain, receipt.Status);
        var context = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        var state = await context.DiscoveryStates.AsNoTracking().SingleAsync(token);
        Assert.Equal(committed ? exception.RefreshId : null, state.CurrentGenerationId);
    }

    [Fact]
    public async Task Competing_connections_claim_once_and_failed_staging_cannot_replace_current_inventory()
    {
        ProviderDiscoveryInventoryTests.RequireMigrationApproval();
        var token = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        Assert.SkipWhen(database is null, "Configure PostgreSQL 18.");
        await using var services = database!.CreateServices(null);
        await database.MigrateAsync(services, token);
        var id = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, token);
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderDiscoveryInventoryStore>();
        await using var current = await store.BeginRefreshAsync(id, token);
        await current.PublishAsync(ProviderDiscoveryInventoryTests.Summary(0), token);
        var claims = await Task.WhenAll(ClaimAsync(), ClaimAsync());
        var winner = Assert.Single(claims, item => item.Session is not null).Session!;
        Assert.Equal(DiscoveryInventoryError.RefreshInProgress, Assert.Single(claims, item => item.Error is not null).Error);
        await using (winner)
        {
            // More than one write buffer is durably staged before the duplicate fails.
            for (var index = 0; index < 130; index++)
                await winner.Sink.AppendAsync(ProviderDiscoveryInventoryTests.Observation(id, $"row-{index}"), token);
            await winner.Sink.AppendAsync(ProviderDiscoveryInventoryTests.Observation(id, "row-0"), token);
            var duplicate = await Assert.ThrowsAsync<DiscoveryInventoryException>(() =>
                winner.PublishAsync(ProviderDiscoveryInventoryTests.Summary(131), token));
            Assert.Equal(DiscoveryInventoryError.DuplicateNativeKey, duplicate.Code);
            await winner.AbortAsync(duplicate.Code, token);
        }
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        Assert.Equal(current.RefreshId, (await db.DiscoveryStates.AsNoTracking().SingleAsync(token)).CurrentGenerationId);
        await Assert.ThrowsAsync<DiscoveryInventoryException>(() => store.OpenReaderAsync(id, current.RefreshId, token));

        async Task<(IProviderDiscoveryRefreshSession? Session, DiscoveryInventoryError? Error)> ClaimAsync()
        {
            try { return (await store.BeginRefreshAsync(id, token), null); }
            catch (DiscoveryInventoryException exception) { return (null, exception.Code); }
        }
    }

    [Fact]
    public async Task Cleanup_is_bounded_and_preserves_the_current_payload_and_receipt()
    {
        ProviderDiscoveryInventoryTests.RequireMigrationApproval();
        var token = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        Assert.SkipWhen(database is null, "Configure PostgreSQL 18.");
        await using var services = database!.CreateServices(null, registrations =>
            registrations.AddSingleton(new DiscoveryInventoryOptions { CleanupBatchSize = 1 }));
        await database.MigrateAsync(services, token);
        var id = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, token);
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderDiscoveryInventoryStore>();
        await using var old = await store.BeginRefreshAsync(id, token);
        for (var index = 0; index < 130; index++)
            await old.Sink.AppendAsync(ProviderDiscoveryInventoryTests.Observation(id, $"row-{index}"), token);
        await old.PublishAsync(ProviderDiscoveryInventoryTests.Summary(130), token);
        await using var current = await store.BeginRefreshAsync(id, token);
        await current.PublishAsync(ProviderDiscoveryInventoryTests.Summary(0), token);
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        await store.CleanupAsync(id, token);
        Assert.Equal(120, await db.DiscoveryObservations.CountAsync(row => row.RefreshId == old.RefreshId, token));
        Assert.Equal(130, await db.DiscoveryObservations.CountAsync(row => row.RefreshId == current.RefreshId, token));
        Assert.Equal(DiscoveryPublicationStatus.Published, (await store.ReconcileAsync(current.RefreshId, token)).Status);
        await using var restarted = database.CreateServices(null, registrations =>
            registrations.AddSingleton(new DiscoveryInventoryOptions { CleanupBatchSize = 1 }));
        await using var restartedScope = restarted.CreateAsyncScope();
        var resumed = restartedScope.ServiceProvider.GetRequiredService<IProviderDiscoveryInventoryStore>();
        for (var batch = 0; batch < 12; batch++) await resumed.CleanupAsync(id, token);
        Assert.Equal(0, await db.DiscoveryObservations.CountAsync(row => row.RefreshId == old.RefreshId, token));
        Assert.Equal(130, await db.DiscoveryObservations.CountAsync(row => row.RefreshId == current.RefreshId, token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_commit_faults_reconcile_by_durable_id_without_publishing_partial_rows(bool loseAcknowledgement)
    {
        ProviderDiscoveryInventoryTests.RequireMigrationApproval();
        var token = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        Assert.SkipWhen(database is null, "Configure PostgreSQL 18.");
        var fault = new CommitFault(loseAcknowledgement);
        await using var services = database!.CreateServices(null, registrations =>
            registrations.ConfigureDbContext<FlowPlaneDbContext>(options => options.AddInterceptors(fault)));
        await database.MigrateAsync(services, token);
        var id = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, token);
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderDiscoveryInventoryStore>();
        await using var previous = await store.BeginRefreshAsync(id, token);
        await previous.PublishAsync(ProviderDiscoveryInventoryTests.Summary(0), token);
        await using var refresh = await store.BeginRefreshAsync(id, token);
        fault.Arm(refresh.RefreshId);
        await refresh.Sink.AppendAsync(ProviderDiscoveryInventoryTests.Observation(id, "current"), token);
        var result = await refresh.PublishAsync(ProviderDiscoveryInventoryTests.Summary(1), token);
        Assert.Equal(loseAcknowledgement ? DiscoveryPublicationStatus.Published : DiscoveryPublicationStatus.Uncertain, result.Status);
        // A fresh service provider models backend restart; no in-process session is reused.
        await using var restarted = database.CreateServices(null);
        await using var verification = restarted.CreateAsyncScope();
        var db = verification.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        Assert.Equal(loseAcknowledgement ? refresh.RefreshId : previous.RefreshId,
            (await db.DiscoveryStates.AsNoTracking().SingleAsync(token)).CurrentGenerationId);
        var receipt = await verification.ServiceProvider.GetRequiredService<IProviderDiscoveryInventoryStore>().ReconcileAsync(refresh.RefreshId, token);
        Assert.Equal(loseAcknowledgement ? DiscoveryPublicationStatus.Published : DiscoveryPublicationStatus.Uncertain, receipt.Status);
    }

    [Fact]
    public async Task Complete_partial_and_changed_metadata_preserve_only_current_approved_documents()
    {
        ProviderDiscoveryInventoryTests.RequireMigrationApproval();
        var token = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        Assert.SkipWhen(database is null, "Configure PostgreSQL 18.");
        await using var services = database!.CreateServices(null);
        await database.MigrateAsync(services, token);
        var id = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, token);
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderDiscoveryInventoryStore>();
        await using (var first = await store.BeginRefreshAsync(id, token))
        {
            await first.Sink.AppendAsync(ProviderDiscoveryInventoryTests.Observation(id, "same"), token);
            await first.PublishAsync(ProviderDiscoveryInventoryTests.Summary(1), token);
        }
        await using var partial = await store.BeginRefreshAsync(id, token);
        await partial.Sink.AppendAsync(ProviderDiscoveryInventoryTests.Observation(id, "same"), token);
        await partial.Sink.AppendAsync(new(id, "unknown", new Codec().Encode("{\"format\":999,\"eligible\":false}"), null), token);
        await partial.PublishAsync(ProviderDiscoveryInventoryTests.Summary(2) with { Outcome = ProviderDiscoveryOutcome.Partial, IncompatibleDefinitions = 1 }, token);
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        var generation = await db.DiscoveryGenerations.AsNoTracking().SingleAsync(row => row.RefreshId == partial.RefreshId, token);
        Assert.Equal(1, generation.UnchangedCount);
        Assert.Equal(1, generation.AddedCount);
        await using var changed = await store.BeginRefreshAsync(id, token);
        await changed.Sink.AppendAsync(new(id, "same", new Codec().Encode("{\"revision\":2}"), null), token);
        await changed.PublishAsync(ProviderDiscoveryInventoryTests.Summary(1), token);
        var change = await db.DiscoveryGenerations.AsNoTracking().SingleAsync(row => row.RefreshId == changed.RefreshId, token);
        Assert.Equal(1, change.ChangedCount);
        var stored = await db.DiscoveryObservations.AsNoTracking().Where(row => row.RefreshId == changed.RefreshId).ToListAsync(token);
        Assert.DoesNotContain("INVENTORY_FIXTURE_PASSWORD", JsonSerializer.Serialize(stored), StringComparison.Ordinal);
        Assert.DoesNotContain("password", JsonSerializer.Serialize(stored), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("default_value", JsonSerializer.Serialize(stored), StringComparison.OrdinalIgnoreCase);
        Assert.All(stored.Where(row => row.Visibility == DiscoveryObservationVisibility.NotObserved), row => Assert.Null(row.MetadataJson));
    }

    [Fact]
    public async Task Interrupted_retirement_is_resumable_and_stale_scanners_remain_fenced()
    {
        ProviderDiscoveryInventoryTests.RequireMigrationApproval();
        var token = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        Assert.SkipWhen(database is null, "Configure PostgreSQL 18.");
        await using var services = database!.CreateServices(null);
        await database.MigrateAsync(services, token);
        var id = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, token);
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderDiscoveryInventoryStore>();
        await using var stale = await store.BeginRefreshAsync(id, token);
        await using (var retiring = database.CreateServices(null))
        await using (var retirementScope = retiring.CreateAsyncScope())
        {
            var context = retirementScope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
            await using var transaction = await context.Database.BeginTransactionAsync(token);
            await DiscoveryDatabase.LockProviderAsync(context, id, token);
            await ProviderDiscoveryInventoryStore.RetireAsync(context, id, token);
            await transaction.CommitAsync(token);
            // Simulated interruption after durable retirement, before purge/final removal.
        }
        await Assert.ThrowsAsync<DiscoveryInventoryException>(() => stale.PublishAsync(ProviderDiscoveryInventoryTests.Summary(0), token));
        await using var restarted = database.CreateServices(null);
        await using var restartedScope = restarted.CreateAsyncScope();
        await restartedScope.ServiceProvider.GetRequiredService<IProviderInstanceStore>().DeleteAsync(id, 1, token);
        var db = restartedScope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        Assert.False(await db.ProviderInstances.AnyAsync(token));
        Assert.False(await db.DiscoveryGenerations.AnyAsync(token));
    }

    // Test-only failure injection at the actual EF transaction boundary, not a fake PublishAsync.
    private sealed class CommitFault(bool afterCommit, Action? cancel = null) : DbTransactionInterceptor
    {
        private Guid? refreshId, transactionId;
        public void Arm(Guid id) => refreshId = id;
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (refreshId is null) return result;
            await using var command = transaction.Connection!.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT count(*) FROM \"ProviderDiscoveryGenerations\" WHERE \"RefreshId\" = @id AND \"Status\" = 'Published'";
            var parameter = command.CreateParameter(); parameter.ParameterName = "id"; parameter.Value = refreshId.Value; command.Parameters.Add(parameter);
            if ((long)(await command.ExecuteScalarAsync(cancellationToken))! != 1) return result;
            transactionId = eventData.TransactionId;
            if (!afterCommit) { refreshId = null; cancel?.Invoke(); throw new IOException("injected-before-commit"); }
            return result;
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (transactionId == eventData.TransactionId)
            {
                transactionId = null; refreshId = null;
                cancel?.Invoke();
                throw new IOException("injected-lost-commit-acknowledgement");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class CommitCancellationCapability(Func<CancellationToken, Task<ProviderDiscoveryScanResult>> scan) : IProviderDiscoveryCapability
    {
        public ProviderType ProviderType => ProviderType.Parse("ssis");
        public Task<ProviderDiscoveryScanResult> ScanAsync(ProviderInstance instance, IProviderDiscoverySink sink, CancellationToken cancellationToken = default) => scan(cancellationToken);
    }

    private sealed class Codec : ProviderDiscoveryMetadataCodec
    {
        public ProviderDiscoveryMetadataDocument Encode(string json) => CreateValidatedDocument("fixture/observation.v1", json);
    }
}
