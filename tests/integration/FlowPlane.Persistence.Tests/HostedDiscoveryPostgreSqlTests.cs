using System.Data.Common;

using FlowPlane.Application.Providers.DiscoveryHosting;
using FlowPlane.Application.Providers.DiscoveryInventory;
using FlowPlane.Infrastructure.Persistence;
using FlowPlane.Infrastructure.Persistence.Discovery;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace FlowPlane.Persistence.Tests;

/// <summary>Gate B acceptance: all databases are uniquely named and disposable; never run before migration approval.</summary>
public sealed class HostedDiscoveryPostgreSqlTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly DiscoveryActor Actor = new("https://identity.example.test/realms/flowplane", "synthetic-operator");

    [Theory]
    [InlineData(-240, true)]
    [InlineData(240, true)]
    [InlineData(-360, false)]
    [InlineData(360, false)]
    public async Task UUIDv7_admission_uses_PostgreSQL_time_and_replay_is_bound_to_the_original_actor(int skewSeconds, bool allowed)
    {
        await using var database = await Database();
        await using var services = Services(database);
        await database.MigrateAsync(services, Token);
        var id = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, Token);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        var now = await DiscoveryDatabase.NowAsync(db, Token);
        var key = Guid.CreateVersion7(now.AddSeconds(skewSeconds));
        var request = new HostedDiscoveryAdmission(key, id, 1, Actor);
        var store = scope.ServiceProvider.GetRequiredService<IHostedDiscoveryStore>();
        if (!allowed)
        {
            Assert.Equal(HostedDiscoveryError.InvalidKey, (await Assert.ThrowsAsync<HostedDiscoveryException>(() => store.AdmitAsync(request, Token))).Code);
            Assert.Empty(await db.DiscoveryGenerations.ToListAsync(Token));
            return;
        }
        var receipt = await store.AdmitAsync(request, Token);
        Assert.InRange(receipt.AcceptedAtUtc, now, await DiscoveryDatabase.NowAsync(db, Token));
        Assert.Equal(HostedDiscoveryPhase.Accepted, receipt.Phase);
        Assert.Equal(receipt.RefreshId, (await store.AdmitAsync(request, Token)).RefreshId);
        Assert.Equal(HostedDiscoveryError.KeyConflict, (await Assert.ThrowsAsync<HostedDiscoveryException>(
            () => store.AdmitAsync(request with { Actor = Actor with { Issuer = "https://different.example.test" } }, Token))).Code);
    }

    [Fact]
    public async Task Direct_managed_secret_replacement_fences_accepted_work_without_changing_provider_version()
    {
        await using var database = await Database();
        var directory = Directory.CreateTempSubdirectory("flowplane-hosted-secret-").FullName;
        try
        {
            var certificate = PostgreSqlTestDatabase.CreateRootCertificate(directory, "disposable");
            await using var services = database.CreateServices(certificate,
                registrations => registrations.AddSingleton(new DiscoveryHostingOptions { Enabled = true }));
            await database.MigrateAsync(services, Token);
            await using var scope = services.CreateAsyncScope();
            var providers = scope.ServiceProvider.GetRequiredService<FlowPlane.Application.Providers.IProviderInstanceStore>();
            var provider = await providers.CreateWithManagedSecretAsync(Guid.CreateVersion7(),
                FlowPlane.Providers.Ssis.Configuration.SsisProviderConfigurationCodec.SsisProviderType, "Synthetic hosted credential", true,
                new FlowPlane.Providers.Ssis.Configuration.SsisProviderConfigurationCodec().Encode(new("fixture", 1433, "fixture-user", false)),
                Guid.NewGuid().ToString("N").AsMemory(), DateTimeOffset.UtcNow, Token);
            var store = scope.ServiceProvider.GetRequiredService<IHostedDiscoveryStore>();
            var receipt = await store.AdmitAsync(new(Guid.CreateVersion7(), provider.Id, provider.Version, Actor), Token);
            await scope.ServiceProvider.GetRequiredService<FlowPlane.Application.Secrets.IManagedSecretStore>()
                .ReplaceAsync(provider.SecretReference, Guid.NewGuid().ToString("N").AsMemory(), Token);
            Assert.Equal(provider.Version, (await providers.FindAsync(provider.Id, Token))!.Version);
            Assert.Null(await store.TryAcquireAsync(Guid.NewGuid(), Token));
            Assert.Equal(HostedDiscoveryPhase.Abandoned, (await store.FindAsync(provider.Id, receipt.RefreshId, Token))!.Phase);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Recovery_waits_for_and_preserves_a_concurrently_committed_lease_renewal()
    {
        await using var database = await Database();
        var wait = new StateLockSignal();
        await using var services = database.CreateServices(null, registrations =>
        {
            registrations.AddSingleton(new DiscoveryHostingOptions { Enabled = true });
            registrations.ConfigureDbContext<FlowPlaneDbContext>(options => options.AddInterceptors(wait));
        });
        await database.MigrateAsync(services, Token);
        var id = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, Token);
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IHostedDiscoveryStore>();
        var receipt = await store.AdmitAsync(new(Guid.CreateVersion7(), id, 1, Actor), Token);
        var owner = Guid.NewGuid();
        var work = (await store.TryAcquireAsync(owner, Token))!;
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"ProviderDiscoveryGenerations\" SET \"LeaseExpiresAtUtc\" = clock_timestamp() - interval '1 second' WHERE \"RefreshId\" = {receipt.RefreshId}", Token);
        await using (var renewingScope = services.CreateAsyncScope())
        {
            var renewing = renewingScope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
            await using var transaction = await renewing.Database.BeginTransactionAsync(Token);
            await DiscoveryDatabase.LockStateAsync(renewing, id, Token);
            await renewing.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"ProviderDiscoveryGenerations\" SET \"LeaseExpiresAtUtc\" = clock_timestamp() + interval '60 seconds', \"Version\" = \"Version\" + 1 WHERE \"RefreshId\" = {receipt.RefreshId}", Token);
            wait.Arm();
            var admission = store.AdmitAsync(new(Guid.CreateVersion7(), id, 1, Actor), Token);
            await wait.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await transaction.CommitAsync(Token);
            Assert.Equal(HostedDiscoveryError.SlotOccupied,
                (await Assert.ThrowsAsync<HostedDiscoveryException>(() => admission)).Code);
        }
        Assert.Equal(HostedDiscoveryPhase.Scanning, (await store.FindAsync(id, receipt.RefreshId, Token))!.Phase);
        Assert.True(await store.RenewAsync(receipt.RefreshId, owner, Token));
        await work.Session.AbortAsync(DiscoveryInventoryError.Cancelled, Token);
        await work.Session.DisposeAsync();
    }

    [Fact]
    public async Task Idle_hosted_poll_cleans_obsolete_payloads_but_preserves_current_generation_and_live_receipts()
    {
        await using var database = await Database();
        await using var services = Services(database);
        await database.MigrateAsync(services, Token);
        var id = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, Token);
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IHostedDiscoveryStore>();
        var published = new List<Guid>();
        for (var index = 0; index < 2; index++)
        {
            var receipt = await store.AdmitAsync(new(Guid.CreateVersion7(), id, 1, Actor), Token);
            var owner = Guid.NewGuid();
            var work = (await store.TryAcquireAsync(owner, Token))!;
            await work.Session.Sink.AppendAsync(ProviderDiscoveryInventoryTests.Observation(id, "observed"), Token);
            var result = await work.Session.PublishAsync(ProviderDiscoveryInventoryTests.Summary(1), Token);
            await store.CompleteAsync(receipt.RefreshId, owner, result, Token);
            await work.Session.DisposeAsync();
            published.Add(receipt.RefreshId);
        }
        Assert.Null(await store.TryAcquireAsync(Guid.NewGuid(), Token));
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        Assert.False(await db.DiscoveryObservations.AnyAsync(row => row.RefreshId == published[0], Token));
        Assert.Equal(1, await db.DiscoveryObservations.CountAsync(row => row.RefreshId == published[1], Token));
        Assert.Equal(published[1], (await db.DiscoveryStates.AsNoTracking().SingleAsync(Token)).CurrentGenerationId);
        Assert.NotNull(await store.FindAsync(id, published[0], Token));
    }

    [Fact]
    public async Task Lost_HTTP_acknowledgement_replays_one_durable_receipt_and_rejects_different_context()
    {
        await using var database = await Database();
        await using var services = Services(database);
        await database.MigrateAsync(services, Token);
        var id = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, Token);
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IHostedDiscoveryStore>();
        var request = new HostedDiscoveryAdmission(Guid.CreateVersion7(), id, 1, Actor);
        // Treat the first committed return as lost before HTTP 202; retry from an independent transaction.
        var receipts = await Task.WhenAll(store.AdmitAsync(request, Token), store.AdmitAsync(request, Token));
        Assert.All(receipts, receipt => Assert.Equal(request.Key, receipt.RefreshId));
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        Assert.Equal(1, await db.DiscoveryGenerations.CountAsync(Token));
        Assert.Equal(1, await db.DiscoveryGenerations.CountAsync(row => row.ExecutionSlot == 1, Token));
        foreach (var altered in new[] { request with { ProviderVersion = 2 }, request with { ProviderId = Guid.NewGuid() },
            request with { Actor = Actor with { Subject = "another-operator" } } })
            Assert.Equal(HostedDiscoveryError.KeyConflict, (await Assert.ThrowsAsync<HostedDiscoveryException>(() => store.AdmitAsync(altered, Token))).Code);
    }

    [Fact]
    public async Task Simultaneous_requests_across_providers_reserve_exactly_one_hosted_slot()
    {
        await using var database = await Database();
        await using var services = Services(database);
        await database.MigrateAsync(services, Token);
        var first = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, Token);
        var second = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, Token);
        var results = await Task.WhenAll(Admit(first), Admit(second));
        Assert.Single(results, result => result is null);
        Assert.Single(results, result => result == HostedDiscoveryError.SlotOccupied);
        async Task<HostedDiscoveryError?> Admit(Guid id)
        {
            await using var scope = services.CreateAsyncScope();
            try { await scope.ServiceProvider.GetRequiredService<IHostedDiscoveryStore>().AdmitAsync(new(Guid.CreateVersion7(), id, 1, Actor), Token); return null; }
            catch (HostedDiscoveryException error) { return error.Code; }
        }
    }

    [Fact]
    public async Task Accepted_receipt_survives_restart_and_is_acquired_once_without_another_generation()
    {
        await using var database = await Database();
        await using var services = Services(database);
        await database.MigrateAsync(services, Token);
        var id = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, Token);
        await using var scope = services.CreateAsyncScope();
        var receipt = await scope.ServiceProvider.GetRequiredService<IHostedDiscoveryStore>().AdmitAsync(new(Guid.CreateVersion7(), id, 1, Actor), Token);
        var accepted = await scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>().DiscoveryGenerations.AsNoTracking().SingleAsync(Token);
        Assert.Null(accepted.ExecutorId);
        Assert.Equal(receipt.ExpiresAtUtc, accepted.LeaseExpiresAtUtc);
        await using var restarted = Services(database);
        await using var restartedScope = restarted.CreateAsyncScope();
        var store = restartedScope.ServiceProvider.GetRequiredService<IHostedDiscoveryStore>();
        var owner = Guid.NewGuid();
        var claims = await Task.WhenAll(store.TryAcquireAsync(owner, Token), store.TryAcquireAsync(Guid.NewGuid(), Token));
        var work = Assert.Single(claims, claim => claim is not null)!;
        Assert.Equal(receipt.RefreshId, work.Session.RefreshId);
        Assert.False(await store.RenewAsync(receipt.RefreshId, Guid.NewGuid(), Token));
        var db = restartedScope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        Assert.Equal(1, await db.DiscoveryGenerations.CountAsync(Token));
        await work.Session.AbortAsync(DiscoveryInventoryError.Cancelled, Token);
        await work.Session.DisposeAsync();
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Preparing")]
    [InlineData("Ready")]
    public async Task Crashed_started_scan_is_fenced_and_never_resumed(string phase)
    {
        await using var database = await Database();
        await using var services = Services(database);
        await database.MigrateAsync(services, Token);
        var id = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, Token);
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IHostedDiscoveryStore>();
        var receipt = await store.AdmitAsync(new(Guid.CreateVersion7(), id, 1, Actor), Token);
        var executor = Guid.NewGuid();
        var work = (await store.TryAcquireAsync(executor, Token))!;
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"ProviderDiscoveryGenerations\" SET \"Status\" = {phase}, \"PublicationAttemptedAtUtc\" = clock_timestamp(), \"LeaseExpiresAtUtc\" = clock_timestamp() - interval '1 second' WHERE \"RefreshId\" = {receipt.RefreshId}", Token);
        Assert.False(await store.RenewAsync(receipt.RefreshId, executor, Token));
        Assert.Null(await store.TryAcquireAsync(Guid.NewGuid(), Token));
        var recovered = await store.FindAsync(id, receipt.RefreshId, Token);
        Assert.Equal(HostedDiscoveryPhase.Abandoned, recovered!.Phase);
        await store.CompleteAsync(receipt.RefreshId, executor,
            new(receipt.RefreshId, DiscoveryPublicationStatus.Uncertain, DiscoveryInventoryError.StorageUnavailable), Token);
        Assert.Equal(HostedDiscoveryPhase.Abandoned, (await store.FindAsync(id, receipt.RefreshId, Token))!.Phase);
        Assert.False((await db.DiscoveryGenerations.AsNoTracking().SingleAsync(Token)).PublicationUncertain);
        await Assert.ThrowsAsync<DiscoveryInventoryException>(() => work.Session.PublishAsync(ProviderDiscoveryInventoryTests.Summary(0), Token));
        Assert.Null((await db.DiscoveryStates.AsNoTracking().SingleAsync(Token)).CurrentGenerationId);
        await work.Session.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Complete_empty_and_compatible_partial_generations_publish_through_the_accepted_session(bool incompatible)
    {
        await using var database = await Database();
        await using var services = Services(database);
        await database.MigrateAsync(services, Token);
        var id = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, Token);
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IHostedDiscoveryStore>();
        var receipt = await store.AdmitAsync(new(Guid.CreateVersion7(), id, 1, Actor), Token);
        var owner = Guid.NewGuid();
        var work = (await store.TryAcquireAsync(owner, Token))!;
        if (incompatible) await work.Session.Sink.AppendAsync(ProviderDiscoveryInventoryTests.Observation(id, "unknown-format-observation"), Token);
        var summary = ProviderDiscoveryInventoryTests.Summary(incompatible ? 1 : 0);
        if (incompatible) summary = summary with { Outcome = FlowPlane.Application.Providers.ProviderDiscoveryOutcome.Partial, IncompatibleDefinitions = 1 };
        var result = await work.Session.PublishAsync(summary, Token);
        await store.CompleteAsync(receipt.RefreshId, owner, result, Token);
        await work.Session.DisposeAsync();
        var current = await store.FindAsync(id, receipt.RefreshId, Token);
        Assert.Equal(HostedDiscoveryPhase.Published, current!.Phase);
        Assert.True(current.ScanCompleted);
        Assert.Equal(incompatible ? 1 : 0, current.Observations);
    }

    [Fact]
    public async Task Changed_provider_configuration_fences_an_accepted_but_unstarted_generation()
    {
        await using var database = await Database();
        await using var services = Services(database);
        await database.MigrateAsync(services, Token);
        var id = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, Token);
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IHostedDiscoveryStore>();
        var receipt = await store.AdmitAsync(new(Guid.CreateVersion7(), id, 1, Actor), Token);
        var providers = scope.ServiceProvider.GetRequiredService<FlowPlane.Application.Providers.IProviderInstanceStore>();
        var original = (await providers.FindAsync(id, Token))!;
        await providers.UpdateAsync(id, 1, "Changed", true, original.Configuration, DateTimeOffset.UtcNow, Token);
        Assert.Null(await store.TryAcquireAsync(Guid.NewGuid(), Token));
        Assert.Equal(HostedDiscoveryPhase.Abandoned, (await store.FindAsync(id, receipt.RefreshId, Token))!.Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_publication_acknowledgement_reconciles_durable_commit_not_assumed_failure(bool committed)
    {
        await using var database = await Database();
        var fault = new PublicationFault(committed);
        await using var services = database.CreateServices(null, registrations =>
        {
            registrations.AddSingleton(new DiscoveryHostingOptions { Enabled = true });
            registrations.ConfigureDbContext<FlowPlaneDbContext>(options => options.AddInterceptors(fault));
        });
        await database.MigrateAsync(services, Token);
        var id = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, Token);
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IHostedDiscoveryStore>();
        var receipt = await store.AdmitAsync(new(Guid.CreateVersion7(), id, 1, Actor), Token);
        var owner = Guid.NewGuid();
        var work = (await store.TryAcquireAsync(owner, Token))!;
        fault.Arm(receipt.RefreshId);
        var result = await work.Session.PublishAsync(ProviderDiscoveryInventoryTests.Summary(0), Token);
        Assert.Equal(committed ? DiscoveryPublicationStatus.Published : DiscoveryPublicationStatus.Uncertain, result.Status);
        await store.CompleteAsync(receipt.RefreshId, owner, result, Token);
        await work.Session.DisposeAsync();
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        if (!committed)
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"ProviderDiscoveryGenerations\" SET \"LeaseExpiresAtUtc\" = clock_timestamp() - interval '1 second' WHERE \"RefreshId\" = {receipt.RefreshId}", Token);
        // Recreated worker consults durable publication ID rather than executing the scan a second time.
        Assert.Null(await store.TryAcquireAsync(Guid.NewGuid(), Token));
        var current = await store.FindAsync(id, receipt.RefreshId, Token);
        Assert.Equal(committed ? HostedDiscoveryPhase.Published : HostedDiscoveryPhase.Abandoned, current!.Phase);
        Assert.Equal(committed ? receipt.RefreshId : (Guid?)null, (await db.DiscoveryStates.AsNoTracking().SingleAsync(Token)).CurrentGenerationId);
    }

    private static async Task<PostgreSqlTestDatabase> Database()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("FLOWPLANE_TEST_DISCOVERY_HOSTING_MIGRATION_APPROVED") == "true",
            "Gate B migration review/application approval is required; no test database created.");
        var database = await PostgreSqlTestDatabase.CreateAsync(Token);
        Assert.SkipWhen(database is null, "Configure isolated PostgreSQL 18.");
        return database!;
    }

    private static ServiceProvider Services(PostgreSqlTestDatabase database) => database.CreateServices(null,
        registrations => registrations.AddSingleton(new DiscoveryHostingOptions { Enabled = true }));

    private sealed class PublicationFault(bool committed) : DbTransactionInterceptor
    {
        private Guid? refresh, transactionId;
        public void Arm(Guid id) => refresh = id;
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (refresh is null) return result;
            await using var command = transaction.Connection!.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT count(*) FROM \"ProviderDiscoveryGenerations\" WHERE \"RefreshId\" = @id AND \"Status\" = 'Published'";
            var parameter = command.CreateParameter(); parameter.ParameterName = "id"; parameter.Value = refresh.Value; command.Parameters.Add(parameter);
            if ((long)(await command.ExecuteScalarAsync(cancellationToken))! != 1) return result;
            transactionId = eventData.TransactionId;
            if (!committed) { refresh = null; throw new IOException("synthetic-before-commit"); }
            return result;
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (transactionId == eventData.TransactionId)
            { refresh = transactionId = null; throw new IOException("synthetic-lost-acknowledgement"); }
            return Task.CompletedTask;
        }
    }

    private sealed class StateLockSignal : DbCommandInterceptor
    {
        private int armed;
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Arm() => Interlocked.Exchange(ref armed, 1);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("ProviderDiscoveryStates", StringComparison.Ordinal) &&
                command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal) && Interlocked.Exchange(ref armed, 0) == 1)
                Reached.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }
}
