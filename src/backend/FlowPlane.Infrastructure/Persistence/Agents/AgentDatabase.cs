using System.Security;

using FlowPlane.Application.Agents;
using FlowPlane.Application.Providers.DiscoveryInventory;
using FlowPlane.Infrastructure.Persistence.Discovery;

using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FlowPlane.Infrastructure.Persistence.Agents;

internal sealed record AgentStoreScope(Guid AgentId, Guid? ProviderId = null, Guid? GrantId = null,
    string? Issuer = null, string? Subject = null, Guid? ClientRequestId = null, Guid? InitiatorId = null, bool Cleanup = false);
internal sealed record AgentProviderSnapshot(Guid Id, long Version, long? CredentialVersion, bool Enabled);
internal sealed class AgentTransaction
{
    public DateTimeOffset Now { get; init; }
    public AgentRecord? Agent { get; set; }
    public List<AgentCredentialRecord> Credentials { get; init; } = [];
    public List<AgentAssignmentRecord> Assignments { get; init; } = [];
    public List<AgentGrantRecord> Grants { get; init; } = [];
    public List<AgentSecurityEventRecord> Events { get; init; } = [];
    public Dictionary<Guid, AgentProviderSnapshot> Providers { get; init; } = [];
    public bool NativeWorkSettled { get; init; }
    public Func<byte[], CancellationToken, Task<AgentCredentialRecord?>> FindPinAsync { get; init; } = (_, _) => Task.FromResult<AgentCredentialRecord?>(null);
    public List<AgentGrantRecord> DeleteGrants { get; } = [];
    public List<AgentSecurityEventRecord> DeleteEvents { get; } = [];
}
internal interface IAgentDatabase
{
    Task<T> ExecuteAsync<T>(AgentStoreScope scope, Func<AgentTransaction, CancellationToken, Task<T>> operation, CancellationToken token);
}

/// <summary>No retry strategy: a commit error is uncertainty, reconciled through the original durable request IDs.</summary>
internal sealed class AgentDatabase(FlowPlaneDbContext db) : IAgentDatabase
{
    public async Task<T> ExecuteAsync<T>(AgentStoreScope scope, Func<AgentTransaction, CancellationToken, Task<T>> operation, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges()) throw Error(AgentStoreError.Unavailable);
        db.ChangeTracker.Clear();
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            await DiscoveryDatabase.SetTimeoutsAsync(db, 30, token);
            var grant = scope.GrantId is { } grantId ? await db.AgentGrants.AsNoTracking().SingleOrDefaultAsync(g => g.Id == grantId, token) : null;
            if (scope.GrantId is not null && grant is null) throw Error(AgentStoreError.NotFound);
            if (grant is not null && scope.AgentId != Guid.Empty && scope.AgentId != grant.AgentId) throw Error(AgentStoreError.InvalidCredential);
            var providerId = grant?.ProviderInstanceId ?? scope.ProviderId;
            AgentProviderSnapshot? lockedProvider = null;
            if (providerId is { } id)
            {
                var provider = await DiscoveryDatabase.LockProviderAsync(db, id, token);
                var secretVersion = await DiscoveryDatabase.LockSecretAsync(db, provider, token);
                var deleting = await db.DiscoveryStates.AsNoTracking().AnyAsync(s => s.ProviderInstanceId == id && s.Lifecycle == DiscoveryStateLifecycle.Deleting, token);
                lockedProvider = new(id, provider.Version, secretVersion, provider.IsEnabled && !deleting);
            }
            // Receipt lookup follows the provider lock. Exact actor comparison handles even an actor-digest collision safely.
            if (scope.ClientRequestId is { } requestId && scope.Issuer is not null)
            {
                var digest = AgentControlStore.ActorHash(scope.Issuer, scope.Subject!);
                var existing = await db.AgentGrants.AsNoTracking().SingleOrDefaultAsync(g => g.ActorDigest == digest && g.ClientRequestId == requestId, token);
                if (existing is not null)
                {
                    if (existing.Issuer != scope.Issuer || existing.Subject != scope.Subject || existing.ProviderInstanceId != providerId)
                        throw Error(AgentStoreError.Conflict);
                    grant = existing;
                }
            }
            var agentId = grant?.AgentId ?? scope.AgentId;
            if (agentId == Guid.Empty) throw Error(AgentStoreError.InvalidRequest);
            if (providerId is { } assignedProvider && await db.AgentAssignments.AsNoTracking()
                .AnyAsync(a => a.ProviderInstanceId == assignedProvider && a.AgentId != agentId, token)) throw Error(AgentStoreError.Conflict);
            var agent = await db.Agents.FromSqlInterpolated($"SELECT * FROM \"Agents\" WHERE \"Id\" = {agentId} FOR UPDATE").SingleOrDefaultAsync(token);
            var assignments = await db.AgentAssignments.FromSqlInterpolated($"SELECT * FROM \"AgentProviderAssignments\" WHERE \"AgentId\" = {agentId} ORDER BY \"ProviderInstanceId\" LIMIT 33 FOR UPDATE").ToListAsync(token);
            var credentials = await db.AgentCredentials.FromSqlInterpolated($"SELECT * FROM \"AgentCredentials\" WHERE \"AgentId\" = {agentId} ORDER BY \"Id\" LIMIT 65 FOR UPDATE").ToListAsync(token);
            var grants = await db.AgentGrants.FromSqlInterpolated($"SELECT * FROM \"AgentEnrollmentGrants\" WHERE \"AgentId\" = {agentId} ORDER BY \"Id\" LIMIT 129 FOR UPDATE").ToListAsync(token);
            if (assignments.Count > 32 || credentials.Count > 64 || grants.Count > 128) throw Error(AgentStoreError.Capacity);
            var now = await DiscoveryDatabase.NowAsync(db, token);
            var providers = new Dictionary<Guid, AgentProviderSnapshot>();
            if (lockedProvider is not null) providers.Add(lockedProvider.Id, lockedProvider);
            // Agent-only session transactions never acquire provider locks after the agent lock.
            // Provider updates hold the provider lock, then fence this row before they can commit.
            foreach (var assignment in assignments.Where(a => !providers.ContainsKey(a.ProviderInstanceId)))
            {
                var provider = await db.ProviderInstances.AsNoTracking().SingleAsync(p => p.Id == assignment.ProviderInstanceId, token);
                long? secretVersion = null;
                if (provider.SecretReference.TryGetFlowPlaneId(out var secretId))
                    secretVersion = await db.SecretRecords.AsNoTracking().Where(s => s.Id == secretId).Select(s => (long?)s.Version).SingleOrDefaultAsync(token);
                var deleting = await db.DiscoveryStates.AsNoTracking().AnyAsync(s => s.ProviderInstanceId == provider.Id && s.Lifecycle == DiscoveryStateLifecycle.Deleting, token);
                providers.Add(provider.Id, new(provider.Id, provider.Version, secretVersion, provider.IsEnabled && !deleting));
            }
            // Foundation has no dispatch endpoint/Run model. A future Run model MUST replace this guard, not silently inherit it.
            var foundationOnly = !db.Model.GetEntityTypes().Any(e => e.GetTableName() is "Runs" or "RunAttempts");
            var tx = new AgentTransaction
            {
                Now = now,
                Agent = agent,
                Credentials = credentials,
                Assignments = assignments,
                Grants = grants,
                Providers = providers,
                NativeWorkSettled = foundationOnly,
                Events = scope.Cleanup ? await db.AgentSecurityEvents.Where(e => e.AgentId == agentId && e.RecordedAtUtc < now.AddDays(-90))
                    .OrderBy(e => e.RecordedAtUtc).ThenBy(e => e.Id).Take(100).ToListAsync(token) : [],
                FindPinAsync = (digest, ct) => db.AgentCredentials.AsNoTracking().SingleOrDefaultAsync(c => c.DerSha256 == digest, ct)
            };
            var result = await operation(tx, token);
            if (tx.Agent is not null && db.Entry(tx.Agent).State == EntityState.Detached) db.Agents.Add(tx.Agent);
            foreach (var row in tx.Credentials.Where(c => db.Entry(c).State == EntityState.Detached)) db.AgentCredentials.Add(row);
            // Free partial unique slots before inserting a replacement grant or activating the new certificate.
            var activating = tx.Credentials.Where(c => db.Entry(c).State != EntityState.Added && c.State == AgentCredentialState.Active &&
                db.Entry(c).OriginalValues.GetValue<AgentCredentialState>(nameof(c.State)) != AgentCredentialState.Active).ToArray();
            foreach (var row in activating) row.State = db.Entry(row).OriginalValues.GetValue<AgentCredentialState>(nameof(row.State));
            await db.SaveChangesAsync(token);
            foreach (var row in activating) row.State = AgentCredentialState.Active;
            foreach (var row in tx.Assignments.Where(a => db.Entry(a).State == EntityState.Detached)) db.AgentAssignments.Add(row);
            foreach (var row in tx.Grants.Where(g => db.Entry(g).State == EntityState.Detached)) db.AgentGrants.Add(row);
            foreach (var row in tx.Events.Where(e => db.Entry(e).State == EntityState.Detached)) db.AgentSecurityEvents.Add(row);
            db.AgentGrants.RemoveRange(tx.DeleteGrants); db.AgentSecurityEvents.RemoveRange(tx.DeleteEvents);
            await db.SaveChangesAsync(token);
            try { await transaction.CommitAsync(token); }
            catch (Exception) { throw Error(AgentStoreError.CommitUncertain); }
            return result;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        { throw Error(AgentStoreError.Conflict); }
        catch (Exception exception) when (exception is DbUpdateException or NpgsqlException or DiscoveryInventoryException or IOException or SecurityException)
        { throw Error(AgentStoreError.Unavailable); }
        finally { db.ChangeTracker.Clear(); }
    }

    internal static async Task GuardProviderDeletionAsync(FlowPlaneDbContext db, Guid providerId, CancellationToken token)
    {
        EnsureProviderDeletable(await db.AgentAssignments.AnyAsync(a => a.ProviderInstanceId == providerId, token),
            await db.AgentGrants.AnyAsync(g => g.ProviderInstanceId == providerId, token));
    }

    internal static void EnsureProviderDeletable(bool hasAssignments, bool hasGrants)
    { if (hasAssignments || hasGrants) throw Error(AgentStoreError.Conflict); }

    internal static void FenceProviderRecords(AgentRecord agent, IReadOnlyList<AgentAssignmentRecord> assignments,
        IReadOnlyList<AgentGrantRecord> grants, IReadOnlyCollection<Guid> providerIds, DateTimeOffset now)
    {
        AgentControlStore.DenyAuthority(agent, now);
        foreach (var assignment in assignments.Where(a => providerIds.Contains(a.ProviderInstanceId)))
        { assignment.State = AgentAssignmentState.Suspended; assignment.Version = checked(assignment.Version + 1); assignment.UpdatedAtUtc = now; }
        foreach (var grant in grants.Where(g => g.State is AgentGrantState.Issued or AgentGrantState.Claimed or AgentGrantState.Proven))
        { grant.State = AgentGrantState.Cancelled; grant.TokenHash = null; grant.Version = checked(grant.Version + 1); }
    }

    // Caller already holds all relevant ProviderInstance locks, and managed-secret lock when involved.
    internal static async Task InvalidateProvidersAsync(FlowPlaneDbContext db, IReadOnlyCollection<Guid> providerIds, CancellationToken token)
    {
        var ids = await db.AgentAssignments.AsNoTracking().Where(a => providerIds.Contains(a.ProviderInstanceId))
            .Select(a => a.AgentId).Distinct().OrderBy(id => id).ToListAsync(token);
        var now = await DiscoveryDatabase.NowAsync(db, token);
        foreach (var id in ids)
        {
            var agent = await db.Agents.FromSqlInterpolated($"SELECT * FROM \"Agents\" WHERE \"Id\" = {id} FOR UPDATE").SingleAsync(token);
            // A long-lived provider-store scope may already track an older session fence. Locks alone do not refresh EF entities.
            await db.Entry(agent).ReloadAsync(token);
            var previousAuthorizationVersion = agent.AuthorizationVersion; var previousFence = agent.SessionFence;
            var assignments = await db.AgentAssignments.FromSqlInterpolated($"SELECT * FROM \"AgentProviderAssignments\" WHERE \"AgentId\" = {id} ORDER BY \"ProviderInstanceId\" LIMIT 33 FOR UPDATE").ToListAsync(token);
            var grants = await db.AgentGrants.FromSqlInterpolated($"SELECT * FROM \"AgentEnrollmentGrants\" WHERE \"AgentId\" = {id} ORDER BY \"Id\" LIMIT 129 FOR UPDATE").ToListAsync(token);
            if (assignments.Count > 32 || grants.Count > 128) throw Error(AgentStoreError.Capacity);
            foreach (var assignment in assignments) await db.Entry(assignment).ReloadAsync(token);
            foreach (var grant in grants) await db.Entry(grant).ReloadAsync(token);
            FenceProviderRecords(agent, assignments, grants, providerIds, now);
            db.AgentSecurityEvents.Add(new()
            {
                Id = Guid.CreateVersion7(),
                AgentId = id,
                Kind = AgentSecurityEventKind.ProviderChanged,
                AuthorizationVersion = agent.AuthorizationVersion,
                Fence = agent.SessionFence,
                PreviousAuthorizationVersion = previousAuthorizationVersion,
                PreviousFence = previousFence,
                RecordedAtUtc = now
            });
        }
    }

    private static AgentStoreException Error(AgentStoreError code) => new(code);
}
