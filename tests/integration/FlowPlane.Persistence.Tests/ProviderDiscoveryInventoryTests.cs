using FlowPlane.Application.Providers;
using FlowPlane.Application.Providers.DiscoveryInventory;
using FlowPlane.Domain.Providers;
using FlowPlane.Infrastructure.Persistence;
using FlowPlane.Infrastructure.Persistence.Discovery;
using FlowPlane.Providers.Ssis.Configuration;
using FlowPlane.Application.Secrets;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FlowPlane.Persistence.Tests;

public sealed class ProviderDiscoveryInventoryTests
{
    internal static void RequireMigrationApproval() => Assert.SkipUnless(
        Environment.GetEnvironmentVariable("FLOWPLANE_TEST_DISCOVERY_MIGRATION_APPROVED") == "true",
        "Pending explicit A-11b migration review/application approval. No database is created or migrated before this gate.");

    [Fact]
    public async Task Direct_owned_secret_replacement_fences_publication_even_when_provider_version_does_not_change()
    {
        RequireMigrationApproval();
        var token = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        Assert.SkipWhen(database is null, "Configure PostgreSQL 18.");
        var directory = Path.Combine(Path.GetTempPath(), $"flowplane-inventory-{Guid.NewGuid():N}");
        var certificate = PostgreSqlTestDatabase.CreateRootCertificate(directory, "inventory-root");
        try
        {
            await using var services = database!.CreateServices(certificate);
            await database.MigrateAsync(services, token);
            await using var scope = services.CreateAsyncScope();
            var providers = scope.ServiceProvider.GetRequiredService<IProviderInstanceStore>();
            var instance = await providers.CreateWithManagedSecretAsync(Guid.CreateVersion7(), ProviderType.Parse("fixture"), "Managed fixture", true,
                new SsisProviderConfigurationCodec().Encode(new("fixture", 1433, "user", false)),
                "first-unique-canary".AsMemory(), DateTimeOffset.UtcNow, token);
            var store = scope.ServiceProvider.GetRequiredService<IProviderDiscoveryInventoryStore>();
            await using var refresh = await store.BeginRefreshAsync(instance.Id, token);
            await scope.ServiceProvider.GetRequiredService<IManagedSecretStore>().ReplaceAsync(instance.SecretReference, "second-unique-canary".AsMemory(), token);
            var failure = await Assert.ThrowsAsync<DiscoveryInventoryException>(() => refresh.PublishAsync(Summary(0), token));
            Assert.Equal(DiscoveryInventoryError.ContextChanged, failure.Code);
            Assert.Equal(1, instance.Version);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Expired_claim_cannot_write_or_publish_and_is_reconciled_by_refresh_id()
    {
        RequireMigrationApproval();
        var token = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        Assert.SkipWhen(database is null, "Configure PostgreSQL 18.");
        await using var services = database!.CreateServices(null);
        await database.MigrateAsync(services, token);
        var id = await CreateInstanceAsync(services, token);
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderDiscoveryInventoryStore>();
        await using var expired = await store.BeginRefreshAsync(id, token);
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        // Controlled PostgreSQL state only; this never mutates SSISDB.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"ProviderDiscoveryGenerations\" SET \"StartedAtUtc\" = clock_timestamp() - interval '2 hours', \"ExpiresAtUtc\" = clock_timestamp() - interval '1 hour' WHERE \"RefreshId\" = {expired.RefreshId}", token);
        await using var replacement = await store.BeginRefreshAsync(id, token);
        var failure = await Assert.ThrowsAsync<DiscoveryInventoryException>(() => expired.PublishAsync(Summary(0), token));
        Assert.Contains(failure.Code, new[] { DiscoveryInventoryError.Expired, DiscoveryInventoryError.Superseded });
        Assert.Equal(DiscoveryPublicationStatus.NotPublished, (await store.ReconcileAsync(expired.RefreshId, token)).Status);
        await replacement.PublishAsync(Summary(0), token);
        Assert.Equal(DiscoveryPublicationStatus.Published, (await store.ReconcileAsync(replacement.RefreshId, token)).Status);
    }

    [Fact]
    public async Task Complete_inventory_and_empty_refresh_publish_but_missing_records_have_no_stale_payload()
    {
        RequireMigrationApproval();
        var token = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        Assert.SkipWhen(database is null, "Configure the controlled PostgreSQL 18 test instance.");
        await using var services = database!.CreateServices(null);
        await database.MigrateAsync(services, token);
        var id = await CreateInstanceAsync(services, token);
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderDiscoveryInventoryStore>();
        Guid first;
        await using (var refresh = await store.BeginRefreshAsync(id, token))
        {
            first = refresh.RefreshId;
            for (var index = 0; index < 301; index++)
                await refresh.Sink.AppendAsync(Observation(id, $"[\"项目\",\"{index}\"]"), token);
            Assert.Equal(DiscoveryPublicationStatus.Published, (await refresh.PublishAsync(Summary(301), token)).Status);
        }
        await using (var reader = await store.OpenReaderAsync(id, first, token))
        {
            var count = 0;
            await foreach (var row in reader.ReadAsync(null, token))
            {
                Assert.NotNull(row.Metadata);
                count++;
            }
            Assert.Equal(301, count);
        }
        Guid second;
        await using (var empty = await store.BeginRefreshAsync(id, token))
        {
            second = empty.RefreshId;
            Assert.Equal(DiscoveryPublicationStatus.Published, (await empty.PublishAsync(Summary(0), token)).Status);
        }
        await using (var reader = await store.OpenReaderAsync(id, second, token))
        {
            var count = 0;
            await foreach (var row in reader.ReadAsync(null, token))
            {
                Assert.Equal(DiscoveryObservationVisibility.NotObserved, row.Visibility);
                Assert.Null(row.Metadata);
                Assert.Null(row.ExecutableCandidate);
                count++;
            }
            Assert.Equal(301, count);
        }
        await using var third = await store.BeginRefreshAsync(id, token);
        await third.PublishAsync(Summary(0), token);
        await using var finalReader = await store.OpenReaderAsync(id, third.RefreshId, token);
        await foreach (var _ in finalReader.ReadAsync(null, token)) Assert.Fail("A still-missing identity marker must be omitted.");
    }

    [Fact]
    public async Task Incomplete_scan_and_concurrent_claim_preserve_the_prior_generation_without_cached_reader_fallback()
    {
        RequireMigrationApproval();
        var token = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        Assert.SkipWhen(database is null, "Configure PostgreSQL 18.");
        await using var services = database!.CreateServices(null);
        await database.MigrateAsync(services, token);
        var id = await CreateInstanceAsync(services, token);
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderDiscoveryInventoryStore>();
        await using var original = await store.BeginRefreshAsync(id, token);
        await original.PublishAsync(Summary(0), token);
        await using var incomplete = await store.BeginRefreshAsync(id, token);
        var busy = await Assert.ThrowsAsync<DiscoveryInventoryException>(() => store.BeginRefreshAsync(id, token));
        Assert.Equal(DiscoveryInventoryError.RefreshInProgress, busy.Code);
        await incomplete.Sink.AppendAsync(Observation(id, "provisional"), token);
        var failure = await Assert.ThrowsAsync<DiscoveryInventoryException>(() => incomplete.PublishAsync(Summary(1) with { ScanCompleted = false }, token));
        Assert.Equal(DiscoveryInventoryError.IncompleteScan, failure.Code);
        await incomplete.AbortAsync(failure.Code, token);
        await using var verification = services.CreateAsyncScope();
        var db = verification.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        Assert.Equal(original.RefreshId, (await db.DiscoveryStates.SingleAsync(token)).CurrentGenerationId);
        await Assert.ThrowsAsync<DiscoveryInventoryException>(() => store.OpenReaderAsync(id, original.RefreshId, token));
    }

    [Fact]
    public async Task Credential_and_configuration_changes_fence_publication_and_deletion_purges_inventory()
    {
        RequireMigrationApproval();
        var token = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        Assert.SkipWhen(database is null, "Configure PostgreSQL 18.");
        await using var services = database!.CreateServices(null);
        await database.MigrateAsync(services, token);
        var id = await CreateInstanceAsync(services, token);
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderDiscoveryInventoryStore>();
        await using var refresh = await store.BeginRefreshAsync(id, token);
        await using (var editing = services.CreateAsyncScope())
        {
            var db = editing.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
            var provider = await db.ProviderInstances.SingleAsync(token);
            provider.UpdateConfiguration("Changed", true, provider.Configuration, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(token);
        }
        var conflict = await Assert.ThrowsAsync<DiscoveryInventoryException>(() => refresh.PublishAsync(Summary(0), token));
        Assert.Equal(DiscoveryInventoryError.ContextChanged, conflict.Code);
        await refresh.AbortAsync(conflict.Code, token);
        var providerStore = scope.ServiceProvider.GetRequiredService<IProviderInstanceStore>();
        await providerStore.DeleteAsync(id, 2, token);
        var context = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        Assert.Empty(await context.DiscoveryGenerations.ToListAsync(token));
        Assert.Empty(await context.DiscoveryObservations.ToListAsync(token));
    }

    [Fact]
    public async Task Reader_keeps_one_generation_across_publication_and_cleanup()
    {
        RequireMigrationApproval();
        var token = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        Assert.SkipWhen(database is null, "Configure PostgreSQL 18.");
        await using var services = database!.CreateServices(null);
        await database.MigrateAsync(services, token);
        var id = await CreateInstanceAsync(services, token);
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderDiscoveryInventoryStore>();
        await using var first = await store.BeginRefreshAsync(id, token);
        await first.Sink.AppendAsync(Observation(id, "first"), token);
        await first.PublishAsync(Summary(1), token);
        await using var reader = await store.OpenReaderAsync(id, first.RefreshId, token);
        var beforePublication = new List<string>();
        await foreach (var row in reader.ReadAsync(null, token)) beforePublication.Add(row.NativeKey);
        Assert.Equal(["first"], beforePublication);
        await using var second = await store.BeginRefreshAsync(id, token);
        await second.PublishAsync(Summary(0), token);
        await store.CleanupAsync(id, token);
        var seen = new List<string>();
        await foreach (var row in reader.ReadAsync(null, token)) seen.Add(row.NativeKey);
        Assert.Equal(["first"], seen);
    }

    internal static async Task<Guid> CreateInstanceAsync(ServiceProvider services, CancellationToken token)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        var instance = ProviderInstance.Create(Guid.CreateVersion7(), SsisProviderConfigurationCodec.SsisProviderType,
            "Inventory fixture", true, new SsisProviderConfigurationCodec().Encode(new("fixture", 1433, "fixture-user", false)),
            SecretReference.Environment("INVENTORY_FIXTURE_PASSWORD"), DateTimeOffset.UtcNow);
        db.ProviderInstances.Add(instance);
        await db.SaveChangesAsync(token);
        return instance.Id;
    }

    internal static ProviderDiscoveryObservation Observation(Guid id, string key) => new(id, key, new Codec().Encode(), null);
    internal static ProviderDiscoveryScanResult Summary(long count)
    {
        var now = DateTimeOffset.UtcNow;
        return new(ProviderDiscoveryOutcome.Succeeded, true, ProviderDiscoverySourceCompatibility.Compatible, count, 0, 0, now, now, []);
    }
    private sealed class Codec : ProviderDiscoveryMetadataCodec
    {
        public ProviderDiscoveryMetadataDocument Encode() => CreateValidatedDocument("fixture/observation.v1", "{\"revision\":1}");
    }
}
