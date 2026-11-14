using System.Data;
using System.Data.Common;
using System.Security;
using System.Security.Cryptography;

using FlowPlane.Application.Agents;
using FlowPlane.Application.Providers.DiscoveryInventory;
using FlowPlane.Infrastructure.Persistence.Discovery;

using Microsoft.EntityFrameworkCore;

namespace FlowPlane.Infrastructure.Persistence.Agents;

internal sealed class AgentControlReader(FlowPlaneDbContext db) : IAgentControlReader
{
    public Task<IReadOnlyList<AgentPublicPin>> ReadPinsAsync(CancellationToken token = default) =>
        SnapshotAsync(async (now, ct) => Pins(await PinsQuery(db, now).ToListAsync(ct)), token);

    public Task<AgentCredentialAuthority?> ReadCredentialAsync(ReadOnlyMemory<byte> exactCertificateDer, CancellationToken token = default)
    {
        if (exactCertificateDer.IsEmpty || exactCertificateDer.Length > 16384) return Task.FromResult<AgentCredentialAuthority?>(null);
        var der = exactCertificateDer.ToArray();
        return SnapshotAsync<AgentCredentialAuthority?>(async (now, ct) =>
        {
            var credential = await CredentialQuery(db, der).SingleOrDefaultAsync(ct);
            if (credential is null) return null;
            var tx = await ContextAsync(credential.AgentId, now, ct);
            return tx is null ? null : Authority(tx, credential);
        }, token);
    }

    public Task<AgentGrantView?> ReadGrantAsync(Guid grantId, CancellationToken token = default) => grantId == Guid.Empty
        ? Task.FromResult<AgentGrantView?>(null)
        : SnapshotAsync<AgentGrantView?>(async (_, ct) => await ViewAsync(
            await db.AgentGrants.AsNoTracking().SingleOrDefaultAsync(g => g.Id == grantId, ct), ct), token);

    public Task<AgentGrantView?> FindClaimAsync(Guid claimId, Guid credentialId, CancellationToken token = default) => claimId == Guid.Empty || credentialId == Guid.Empty
        ? Task.FromResult<AgentGrantView?>(null)
        : SnapshotAsync<AgentGrantView?>(async (_, ct) => await ViewAsync(
            await ClaimQuery(db, claimId, credentialId).SingleOrDefaultAsync(ct), ct), token);

    public Task<AgentHealthView?> ReadHealthAsync(Guid agentId, CancellationToken token = default) => agentId == Guid.Empty
        ? Task.FromResult<AgentHealthView?>(null)
        : SnapshotAsync<AgentHealthView?>(async (now, ct) =>
        {
            var tx = await ContextAsync(agentId, now, ct);
            return tx is null ? null : Health(tx);
        }, token);

    public Task<bool> MatchesIssuedCodeAsync(Guid grantId, ReadOnlyMemory<byte> tokenHash, CancellationToken token = default)
    {
        if (grantId == Guid.Empty || tokenHash.Length != 32) return Task.FromResult(false);
        var hash = tokenHash.ToArray();
        return SnapshotAsync(async (now, ct) =>
        {
            var grant = await db.AgentGrants.AsNoTracking().SingleOrDefaultAsync(g => g.Id == grantId, ct);
            return MatchesCode(grant, hash, now) && await db.Agents.AsNoTracking().AnyAsync(a => a.Id == grant!.AgentId &&
                a.State != AgentLifecycleState.Revoked && a.RevokedAtUtc == null, ct);
        }, token);
    }

    // A snapshot is a read result, never mutation admission. F3 still checks current rows under its locks.
    private async Task<T> SnapshotAsync<T>(Func<DateTimeOffset, CancellationToken, Task<T>> read, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges()) throw Error(AgentStoreError.Unavailable);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
            await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", token);
            await DiscoveryDatabase.SetTimeoutsAsync(db, 10, token);
            var now = await DiscoveryDatabase.NowAsync(db, token);
            var result = await read(now, token);
            await transaction.CommitAsync(token);
            return result;
        }
        catch (Exception exception) when (exception is DbException or IOException or SecurityException or TimeoutException)
        { throw Error(AgentStoreError.Unavailable); }
    }

    private async Task<AgentTransaction?> ContextAsync(Guid agentId, DateTimeOffset now, CancellationToken token)
    {
        var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Id == agentId, token);
        if (agent is null) return null;
        var assignments = await db.AgentAssignments.AsNoTracking().Where(a => a.AgentId == agentId)
            .OrderBy(a => a.ProviderInstanceId).Take(33).ToListAsync(token);
        var credentials = await db.AgentCredentials.AsNoTracking().Where(c => c.AgentId == agentId)
            .OrderBy(c => c.Id).Take(65).ToListAsync(token);
        var grants = await db.AgentGrants.AsNoTracking().Where(g => g.AgentId == agentId)
            .OrderBy(g => g.Id).Take(129).ToListAsync(token);
        if (assignments.Count > 32 || credentials.Count > 64 || grants.Count > 128) throw Error(AgentStoreError.Capacity);
        var providers = new Dictionary<Guid, AgentProviderSnapshot>();
        foreach (var assignment in assignments)
        {
            // Do not load provider configuration or managed credential ciphertext into a health/read model.
            var provider = await db.ProviderInstances.AsNoTracking().Where(p => p.Id == assignment.ProviderInstanceId)
                .Select(p => new { p.Id, p.Version, p.IsEnabled, p.SecretReference }).SingleOrDefaultAsync(token);
            if (provider is null) continue;
            long? credentialVersion = null;
            var enabled = provider.IsEnabled;
            if (provider.SecretReference.TryGetFlowPlaneId(out var secretId))
            {
                credentialVersion = await db.SecretRecords.AsNoTracking().Where(s => s.Id == secretId)
                    .Select(s => (long?)s.Version).SingleOrDefaultAsync(token);
                enabled &= credentialVersion is not null;
            }
            var deleting = await db.DiscoveryStates.AsNoTracking().AnyAsync(s => s.ProviderInstanceId == provider.Id &&
                s.Lifecycle == DiscoveryStateLifecycle.Deleting, token);
            providers.Add(provider.Id, new(provider.Id, provider.Version, credentialVersion, enabled && !deleting));
        }
        return new() { Now = now, Agent = agent, Credentials = credentials, Assignments = assignments, Grants = grants, Providers = providers };
    }

    private async Task<AgentGrantView?> ViewAsync(AgentGrantRecord? grant, CancellationToken token)
    {
        if (grant is null) return null;
        var credential = grant.ClaimedCredentialId is { } id ? await db.AgentCredentials.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == id && c.AgentId == grant.AgentId, token) : null;
        if (grant.ClaimedCredentialId is not null && credential is null) throw Error(AgentStoreError.Unavailable);
        return GrantView(grant, credential);
    }

    internal static IQueryable<AgentPublicPin> PinsQuery(FlowPlaneDbContext context, DateTimeOffset now) =>
        (from credential in context.AgentCredentials.AsNoTracking()
         join agent in context.Agents.AsNoTracking() on credential.AgentId equals agent.Id
         where agent.State != AgentLifecycleState.Revoked && agent.RevokedAtUtc == null && credential.RevokedAtUtc == null &&
             credential.NotBeforeUtc <= now && credential.NotAfterUtc > now &&
             (credential.State == AgentCredentialState.Active ||
              credential.State == AgentCredentialState.RetiringReceiptsOnly && credential.RetiringUntilUtc > now ||
              credential.State == AgentCredentialState.PendingProof && context.AgentGrants.Any(g =>
                  g.AgentId == credential.AgentId && g.ClaimedCredentialId == credential.Id && g.ExpiresAtUtc > now &&
                  (g.State == AgentGrantState.Claimed || g.State == AgentGrantState.Proven)))
         orderby credential.Id
         select new AgentPublicPin(credential.AgentId, credential.Id, credential.Der)).Take(4097);

    internal static IQueryable<AgentCredentialRecord> CredentialQuery(FlowPlaneDbContext context, byte[] der)
    {
        var hash = SHA256.HashData(der);
        return context.AgentCredentials.AsNoTracking().Where(c => c.DerSha256 == hash && c.Der == der);
    }

    internal static IQueryable<AgentGrantRecord> ClaimQuery(FlowPlaneDbContext context, Guid claimId, Guid credentialId) =>
        context.AgentGrants.AsNoTracking().Where(g => g.ClaimId == claimId && g.ClaimedCredentialId == credentialId);

    internal static AgentCredentialAuthority Authority(AgentTransaction tx, AgentCredentialRecord credential)
    {
        var agent = tx.Agent ?? throw Error(AgentStoreError.Unavailable);
        var current = AgentCurrent(agent) && ValidCredential(credential, agent.Id, tx.Now) &&
            (credential.State == AgentCredentialState.PendingProof ? PendingCurrent(tx, credential) :
                agent.State == AgentLifecycleState.Active && AssignmentsCurrent(tx, activeOnly: true));
        return new(new(agent.Id, credential.Id, credential.Der.ToArray()), agent.State, credential.State, agent.Version,
            agent.AuthorizationVersion, agent.SessionFence, credential.NotBeforeUtc, credential.NotAfterUtc,
            credential.RetiringUntilUtc, current, agent.RecoveryRequired, tx.Now);
    }

    internal static AgentHealthView Health(AgentTransaction tx)
    {
        var agent = tx.Agent ?? throw Error(AgentStoreError.Unavailable);
        var current = AgentCurrent(agent) && agent.State == AgentLifecycleState.Active && AssignmentsCurrent(tx, activeOnly: true) &&
            tx.Credentials.Any(c => c.State == AgentCredentialState.Active && ValidCredential(c, agent.Id, tx.Now)) &&
            agent.ConnectFence == agent.SessionFence && agent.LeaseAuthorizationVersion == agent.AuthorizationVersion && agent.LeaseExpiresAtUtc > tx.Now;
        return new(agent.Id, agent.DisplayName, agent.State, agent.Version, agent.AuthorizationVersion, agent.SessionFence,
            agent.PackageVersion, agent.ProtocolVersion, agent.Readiness, agent.LastContactAtUtc, agent.LastHeartbeatAtUtc,
            agent.LeaseExpiresAtUtc, tx.Now, current, agent.RecoveryRequired,
            tx.Assignments.Select(a => new AgentProviderContext(a.ProviderInstanceId, a.ProviderVersion)).ToArray());
    }

    private static bool AgentCurrent(AgentRecord agent) => agent.State is AgentLifecycleState.Pending or AgentLifecycleState.Active &&
        agent.RevokedAtUtc is null && !agent.RecoveryRequired && agent.Version > 0 && agent.AuthorizationVersion > 0 && agent.SessionFence > 0;

    private static bool ValidCredential(AgentCredentialRecord credential, Guid agentId, DateTimeOffset now) =>
        credential.AgentId == agentId && credential.RevokedAtUtc is null && credential.NotBeforeUtc <= now && credential.NotAfterUtc > now &&
        (credential.State is AgentCredentialState.PendingProof or AgentCredentialState.Active ||
            credential.State == AgentCredentialState.RetiringReceiptsOnly && credential.RetiringUntilUtc > now);

    private static bool AssignmentsCurrent(AgentTransaction tx, bool activeOnly) => tx.Assignments.Count > 0 && tx.Assignments.All(a =>
        a.AgentId == tx.Agent!.Id && a.Version > 0 && a.ProviderVersion > 0 &&
        (a.State == AgentAssignmentState.Active || !activeOnly && a.State == AgentAssignmentState.Pending) &&
        tx.Providers.TryGetValue(a.ProviderInstanceId, out var provider) && provider.Enabled &&
        a.ProviderVersion == provider.Version && a.ProviderCredentialVersion == provider.CredentialVersion);

    private static bool PendingCurrent(AgentTransaction tx, AgentCredentialRecord credential) => AssignmentsCurrent(tx, activeOnly: false) &&
        tx.Grants.Any(g => g.AgentId == credential.AgentId && g.ClaimedCredentialId == credential.Id && g.ExpiresAtUtc > tx.Now &&
            g.State is AgentGrantState.Claimed or AgentGrantState.Proven && g.ExpectedAgentVersion == tx.Agent!.Version &&
            g.ExpectedAuthorizationVersion == tx.Agent.AuthorizationVersion && tx.Assignments.Any(a =>
                a.ProviderInstanceId == g.ProviderInstanceId && a.Version == g.ExpectedAssignmentVersion &&
                a.ProviderVersion == g.ProviderVersion && a.ProviderCredentialVersion == g.ProviderCredentialVersion));

    internal static AgentGrantView GrantView(AgentGrantRecord grant, AgentCredentialRecord? credential)
    {
        if (credential is not null && (credential.AgentId != grant.AgentId || credential.Id != grant.ClaimedCredentialId))
            throw Error(AgentStoreError.Unavailable);
        var recorded = grant.State switch
        {
            AgentGrantState.Claimed => grant.ClaimedAtUtc ?? grant.CreatedAtUtc,
            AgentGrantState.Proven => grant.ProvedAtUtc ?? grant.CreatedAtUtc,
            AgentGrantState.Redeemed => grant.RedeemedAtUtc ?? grant.CreatedAtUtc,
            _ => grant.CreatedAtUtc
        };
        var receipt = new AgentGrantReceipt(grant.Id, grant.AgentId, grant.ProviderInstanceId, grant.State, grant.Version,
            recorded, grant.ExpiresAtUtc, grant.ClaimId, grant.ClaimedCredentialId, grant.Nonce?.ToArray(), grant.ActivatedAuthorizationVersion);
        return new(receipt, grant.Issuer is not null && grant.Subject is not null ? new(grant.Issuer, grant.Subject) : null,
            grant.InitiatingCredentialId, credential?.Der.ToArray(), grant.ProviderInstanceId, grant.ProviderVersion, grant.ClaimReceiptVersion);
    }

    internal static IReadOnlyList<AgentPublicPin> Pins(IReadOnlyList<AgentPublicPin> pins)
    {
        if (pins.Count > 4096) throw Error(AgentStoreError.Capacity);
        return pins.Select(p => p with { CertificateDer = p.CertificateDer.ToArray() }).ToArray();
    }

    internal static bool MatchesCode(AgentGrantRecord? grant, ReadOnlySpan<byte> tokenHash, DateTimeOffset now) =>
        grant is { Kind: AgentGrantKind.Enrollment, TokenHash.Length: 32 } && tokenHash.Length == 32 &&
        grant.State is AgentGrantState.Issued or AgentGrantState.Claimed or AgentGrantState.Proven && grant.ExpiresAtUtc > now &&
        CryptographicOperations.FixedTimeEquals(grant.TokenHash, tokenHash);

    private static AgentStoreException Error(AgentStoreError error) => new(error);
}
