using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using FlowPlane.Application.Agents;
using FlowPlane.Application.Providers;
using FlowPlane.Infrastructure.Persistence;
using FlowPlane.Infrastructure.Persistence.Agents;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace FlowPlane.Persistence.Tests;

// Compiled only during F3. Execution requires separate migration application/rollback approval.
public sealed class AgentControlPostgreSqlTests
{
    private const string Previous = "20261026084710_DurableDiscoveryHosting";
    private const string Target = "20261111081939_AgentControlFoundation";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly AgentActor Actor = new("https://issuer.example/", "synthetic-operator");
    private static readonly byte[] Hash = SHA256.HashData("disposable-grant"u8);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Fresh_or_upgrade_SQL_and_EF_repeat_and_quiescent_rollback_preserve_existing_data(bool upgrade, bool sql)
    {
        await using var database = await Create();
        await using var services = database.CreateServices(null);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        var migrator = db.GetService<IMigrator>();
        Guid? provider = null;
        if (upgrade)
        {
            await migrator.MigrateAsync(Previous, Token);
            provider = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, Token);
            var inventory = scope.ServiceProvider.GetRequiredService<FlowPlane.Application.Providers.DiscoveryInventory.IProviderDiscoveryInventoryStore>();
            await using var refresh = await inventory.BeginRefreshAsync(provider.Value, Token);
            await refresh.Sink.AppendAsync(ProviderDiscoveryInventoryTests.Observation(provider.Value, "exact-项目"), Token);
            await refresh.PublishAsync(ProviderDiscoveryInventoryTests.Summary(1), Token);
        }
        var before = upgrade ? await ExistingRows(db) : [];
        if (sql && !upgrade) await migrator.MigrateAsync(Previous, Token);
        database.RecordEvidence("before-F3", (await db.Database.GetAppliedMigrationsAsync(Token)).ToArray());
        if (sql) await db.Database.ExecuteSqlRawAsync(ReviewedSql("up"), Token);
        else await migrator.MigrateAsync(Target, Token);
        database.RecordEvidence("after-F3", (await db.Database.GetAppliedMigrationsAsync(Token)).ToArray());
        await migrator.MigrateAsync(Target, Token);
        database.RecordEvidence("after-repeat", (await db.Database.GetAppliedMigrationsAsync(Token)).ToArray());
        Assert.Equal(5, (await db.Database.GetAppliedMigrationsAsync(Token)).Count());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Token));
        Assert.Equal(12, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM pg_tables WHERE schemaname='public'").SingleAsync(Token));
        if (upgrade) Assert.Equal(before, await ExistingRows(db));
        if (sql) await db.Database.ExecuteSqlRawAsync(ReviewedSql("down"), Token);
        else await migrator.MigrateAsync(Previous, Token);
        database.RecordEvidence("after-F3-rollback", (await db.Database.GetAppliedMigrationsAsync(Token)).ToArray());
        Assert.Equal(4, (await db.Database.GetAppliedMigrationsAsync(Token)).Count());
        Assert.Equal(7, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM pg_tables WHERE schemaname='public'").SingleAsync(Token));
        if (upgrade) Assert.Equal(before, await ExistingRows(db));
    }

    [Fact]
    public async Task Concurrent_same_request_recovers_one_grant_and_other_actor_cannot_take_the_assignment()
    {
        await using var database = await Create();
        await using var services = database.CreateServices(null);
        await database.MigrateAsync(services, Token);
        var provider = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, Token);
        var request = new IssueAgentGrant(new(provider, 1), Actor, Guid.NewGuid(), "Synthetic agent", Hash);
        var receipts = await Task.WhenAll(Issue(request), Issue(request));
        Assert.Equal(receipts[0], receipts[1]);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        Assert.Equal(1, await db.Agents.CountAsync(Token));
        Assert.Equal(1, await db.AgentGrants.CountAsync(Token));
        var error = await Assert.ThrowsAsync<AgentStoreException>(() => Issue(request with
        { Actor = new(Actor.Issuer, "different-operator"), ClientRequestId = Guid.NewGuid() }));
        Assert.Equal(AgentStoreError.Conflict, error.Code);
        async Task<AgentGrantReceipt> Issue(IssueAgentGrant input)
        {
            await using var inner = services.CreateAsyncScope();
            return await inner.ServiceProvider.GetRequiredService<IAgentControlStore>().IssueGrantAsync(input, Token);
        }
    }

    [Fact]
    public async Task Proof_confirmation_race_activates_once_and_current_session_status_survives_new_store_scopes()
    {
        await using var database = await Create();
        await using var services = database.CreateServices(null);
        await database.MigrateAsync(services, Token);
        var provider = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, Token);
        var (certificate, confirm) = await Proved(services, provider);
        var receipts = await Task.WhenAll(Confirm(), Confirm());
        Assert.Equal(receipts[0].ActivatedAuthorizationVersion, receipts[1].ActivatedAuthorizationVersion);
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentControlStore>();
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        var row = await db.Agents.AsNoTracking().SingleAsync(Token);
        var request = new ConnectAgent(certificate, Guid.NewGuid(), Guid.NewGuid(), row.SessionFence,
            row.AuthorizationVersion, 1, "0.1.0", true, 0);
        var connected = (await store.ConnectAsync(request, Token)).Receipt!;
        var poll = new PollAgent(certificate, connected.SessionId, connected.Fence, connected.AuthorizationVersion,
            Guid.NewGuid(), 1, AgentReadiness.ReadyForControlPlane, true, 0);
        var acknowledged = (await store.PollAsync(poll, Token)).Receipt!;
        Assert.Equal(acknowledged, (await store.PollAsync(poll, Token)).Receipt);
        Assert.Equal(connected, (await store.ConnectAsync(request, Token)).Receipt);
        await using var recoveredScope = services.CreateAsyncScope();
        var recovered = recoveredScope.ServiceProvider.GetRequiredService<IAgentControlStore>();
        Assert.Equal(acknowledged.LeaseExpiresAtUtc, (await recovered.ReadSessionAsync(certificate, Token))!.LeaseExpiresAtUtc);
        await recovered.RevokeAsync(new(certificate.AgentId, Actor, row.Version), Token);
        var revoked = await Assert.ThrowsAsync<AgentStoreException>(() => store.PollAsync(poll, Token));
        Assert.Equal(AgentStoreError.Revoked, revoked.Code);
        Assert.Equal(1, await db.AgentCredentials.CountAsync(c => c.AgentId == certificate.AgentId, Token));
        async Task<AgentGrantReceipt> Confirm()
        {
            await using var inner = services.CreateAsyncScope();
            return await inner.ServiceProvider.GetRequiredService<IAgentControlStore>().ConfirmAsync(confirm, Token);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rollback_refuses_pending_authority_and_revoked_unresolved_recovery(bool unresolved)
    {
        await using var database = await Create();
        await using var services = database.CreateServices(null);
        await database.MigrateAsync(services, Token);
        var provider = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, Token);
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentControlStore>();
        var receipt = await store.IssueGrantAsync(new(new(provider, 1), Actor, Guid.NewGuid(), "Synthetic agent", Hash), Token);
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        if (unresolved)
        {
            await store.RevokeAsync(new(receipt.AgentId, Actor, 1), Token);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Agents\" SET \"RecoveryRequired\"=true WHERE \"Id\"={receipt.AgentId}", Token);
        }
        var before = await db.AgentGrants.AsNoTracking().Select(g => g.Id).ToListAsync(Token);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(Previous, Token));
        Assert.Equal(PostgresErrorCodes.RaiseException, failure.SqlState);
        Assert.Equal(5, (await db.Database.GetAppliedMigrationsAsync(Token)).Count());
        Assert.Equal(before, await db.AgentGrants.AsNoTracking().Select(g => g.Id).ToListAsync(Token));
    }

    [Fact]
    public async Task Provider_deletion_is_rejected_before_discovery_retirement()
    {
        await using var database = await Create();
        await using var services = database.CreateServices(null);
        await database.MigrateAsync(services, Token);
        var provider = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, Token);
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentControlStore>();
        await store.IssueGrantAsync(new(new(provider, 1), Actor, Guid.NewGuid(), "Synthetic agent", Hash), Token);
        var providers = scope.ServiceProvider.GetRequiredService<IProviderInstanceStore>();
        var failure = await Assert.ThrowsAsync<AgentStoreException>(() => providers.DeleteAsync(provider, 1, Token));
        Assert.Equal(AgentStoreError.Conflict, failure.Code);
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        Assert.True(await db.ProviderInstances.AnyAsync(p => p.Id == provider, Token));
        Assert.Empty(await db.DiscoveryStates.ToListAsync(Token));
    }

    [Fact]
    public async Task Provider_change_reloads_tracked_agent_before_advancing_current_fence()
    {
        await using var database = await Create();
        await using var services = database.CreateServices(null);
        await database.MigrateAsync(services, Token);
        var providerId = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, Token);
        var (certificate, confirm) = await Proved(services, providerId);
        await using (var activation = services.CreateAsyncScope())
            await activation.ServiceProvider.GetRequiredService<IAgentControlStore>().ConfirmAsync(confirm, Token);
        await using var providerScope = services.CreateAsyncScope();
        var trackedDb = providerScope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        var staleAgent = await trackedDb.Agents.SingleAsync(Token);
        await using var sessionScope = services.CreateAsyncScope();
        var store = sessionScope.ServiceProvider.GetRequiredService<IAgentControlStore>();
        var connected = (await store.ConnectAsync(new(certificate, Guid.NewGuid(), Guid.NewGuid(), staleAgent.SessionFence,
            staleAgent.AuthorizationVersion, 1, "0.1.0", true, 0), Token)).Receipt!;
        Assert.True(connected.Fence > staleAgent.SessionFence);
        var providers = providerScope.ServiceProvider.GetRequiredService<IProviderInstanceStore>();
        var provider = (await providers.FindAsync(providerId, Token))!;
        await providers.UpdateAsync(providerId, provider.Version, provider.DisplayName, provider.IsEnabled,
            provider.Configuration, DateTimeOffset.UtcNow, Token);
        await using var afterScope = services.CreateAsyncScope();
        var after = await afterScope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>().Agents.AsNoTracking().SingleAsync(Token);
        Assert.True(after.SessionFence > connected.Fence);
        Assert.True(after.AuthorizationVersion > connected.AuthorizationVersion);
        var denied = await Assert.ThrowsAsync<AgentStoreException>(() => store.ReadSessionAsync(certificate, Token));
        Assert.Equal(AgentStoreError.ContextChanged, denied.Code);
    }

    internal static async Task<PostgreSqlTestDatabase> Create()
    {
        var database = await PostgreSqlTestDatabase.CreateAsync(Token);
        Assert.SkipWhen(database is null, "Configure separately approved isolated PostgreSQL 18.");
        return database!;
    }

    internal static string ReviewedSql(string direction)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FlowPlane.slnx"))) directory = directory.Parent;
        return File.ReadAllText(Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Repository not found."),
            "docs", "development", $"agent-foundation-f3-{direction}.sql"));
    }

    internal static async Task<(AgentCertificateContext Certificate, ConfirmAgentGrant Confirm)> Proved(ServiceProvider services, Guid provider)
    {
        using var key = RSA.Create(3072);
        using var publicCertificate = new CertificateRequest("CN=synthetic-agent", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var der = publicCertificate.Export(X509ContentType.Cert);
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentControlStore>();
        var grant = await store.IssueGrantAsync(new(new(provider, 1), Actor, Guid.NewGuid(), "Synthetic agent", Hash), Token);
        var claimId = Guid.NewGuid();
        var claim = await store.ClaimAsync(new(grant.GrantId, claimId, Hash, der), Token);
        var certificate = new AgentCertificateContext(grant.AgentId, claim.CredentialId!.Value, der);
        var proof = await store.RecordProofAsync(new(grant.GrantId, claimId, Guid.NewGuid(), claim.Nonce!, certificate), Token);
        return (certificate, new(grant.GrantId, Actor, proof.Version, proof.Nonce!));
    }

    private static Task<List<string>> ExistingRows(FlowPlaneDbContext db) => db.Database.SqlQueryRaw<string>("""
        SELECT value AS "Value" FROM (
          SELECT 'provider:' || row_to_json(t)::text AS value FROM "ProviderInstances" t
          UNION ALL SELECT 'key:' || row_to_json(t)::text FROM "DataProtectionKeys" t
          UNION ALL SELECT 'secret:' || row_to_json(t)::text FROM "FlowPlaneSecrets" t
          UNION ALL SELECT 'state:' || row_to_json(t)::text FROM "ProviderDiscoveryStates" t
          UNION ALL SELECT 'generation:' || row_to_json(t)::text FROM "ProviderDiscoveryGenerations" t
          UNION ALL SELECT 'observation:' || row_to_json(t)::text FROM "ProviderDiscoveryObservations" t
        ) rows ORDER BY value COLLATE "C"
        """).ToListAsync(Token); // Compare in memory; never print protected rows or credentials.
}
