using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using FlowPlane.Application.Agents;

namespace FlowPlane.Infrastructure.Persistence.Agents;

/// <summary>All lifecycle decisions run inside a short current-DB transaction; never transport or native I/O.</summary>
internal sealed class AgentControlStore(IAgentDatabase database) : IAgentControlStore
{
    public Task<AgentGrantReceipt> IssueGrantAsync(IssueAgentGrant request, CancellationToken token = default)
    {
        Actor(request.Actor); Id(request.ClientRequestId); Provider(request.Provider); Bytes(request.TokenHash, 32);
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 200 || request.DisplayName.Any(char.IsControl))
            throw Error(AgentStoreError.InvalidRequest);
        var agentId = request.ExistingAgentId ?? Guid.CreateVersion7();
        Id(agentId);
        var digest = Digest(request.Provider.ProviderInstanceId, request.Provider.ProviderVersion, request.DisplayName,
            request.ExistingAgentId, request.ExpectedAgentVersion);
        return database.ExecuteAsync(new(agentId, request.Provider.ProviderInstanceId, Issuer: request.Actor.Issuer,
            Subject: request.Actor.Subject, ClientRequestId: request.ClientRequestId), (tx, _) =>
        {
            var provider = CurrentProvider(tx, request.Provider);
            if (tx.Agent?.State == AgentLifecycleState.Revoked) throw Error(AgentStoreError.Revoked);
            var replay = tx.Grants.SingleOrDefault(g => g.Kind == AgentGrantKind.Enrollment && g.Issuer == request.Actor.Issuer &&
                g.Subject == request.Actor.Subject && g.ClientRequestId == request.ClientRequestId);
            if (replay is not null)
            {
                if (!Equal(replay.RequestDigest, digest)) throw Error(AgentStoreError.Conflict);
                return Task.FromResult(Receipt(replay, AgentGrantState.Issued));
            }
            if (!tx.NativeWorkSettled || tx.Agent?.RecoveryRequired == true) throw Error(AgentStoreError.ActionRequired);
            if (tx.Grants.Count >= 128 || tx.Assignments.Count >= 32 && tx.Assignments.All(a => a.ProviderInstanceId != provider.Id))
                throw Error(AgentStoreError.Capacity);
            var agent = tx.Agent;
            var previousAuthorizationVersion = agent?.AuthorizationVersion ?? 1;
            if (agent is null)
            {
                if (request.ExistingAgentId is not null) throw Error(AgentStoreError.NotFound);
                agent = tx.Agent = new() { Id = agentId, DisplayName = request.DisplayName, CreatedAtUtc = tx.Now, UpdatedAtUtc = tx.Now };
            }
            else
            {
                if (request.ExistingAgentId != agent.Id || request.ExpectedAgentVersion != agent.Version) throw Error(AgentStoreError.ContextChanged);
                var existing = tx.Assignments.SingleOrDefault(a => a.ProviderInstanceId == provider.Id);
                if (existing?.State == AgentAssignmentState.Active && agent.State == AgentLifecycleState.Active)
                    throw Error(AgentStoreError.Conflict); // Not an implicit assignment replacement.
                agent.Version = Next(agent.Version);
                agent.AuthorizationVersion = Next(agent.AuthorizationVersion);
                agent.State = AgentLifecycleState.Pending;
                agent.RevokedAtUtc = null; agent.RevokedByIssuer = null; agent.RevokedBySubject = null;
                foreach (var credential in tx.Credentials)
                { credential.State = AgentCredentialState.Revoked; credential.RevokedAtUtc = tx.Now; credential.Version = Next(credential.Version); }
            }
            CancelLiveGrants(tx);
            var assignment = tx.Assignments.SingleOrDefault(a => a.ProviderInstanceId == provider.Id);
            if (assignment is null)
            {
                assignment = new() { ProviderInstanceId = provider.Id, AgentId = agent.Id, CreatedAtUtc = tx.Now };
                tx.Assignments.Add(assignment);
            }
            else assignment.Version = Next(assignment.Version);
            assignment.State = AgentAssignmentState.Pending; assignment.ProviderVersion = provider.Version;
            assignment.ProviderCredentialVersion = provider.CredentialVersion; assignment.UpdatedAtUtc = tx.Now;
            var grant = NewGrant(tx, assignment, AgentGrantKind.Enrollment, request.ClientRequestId, digest);
            grant.Issuer = request.Actor.Issuer; grant.Subject = request.Actor.Subject;
            grant.ActorDigest = Digest(request.Actor.Issuer, request.Actor.Subject); grant.TokenHash = request.TokenHash.ToArray();
            tx.Grants.Add(grant); Event(tx, AgentSecurityEventKind.GrantIssued, grant, request.Actor, previousAuthorizationVersion);
            return Task.FromResult(Receipt(grant, AgentGrantState.Issued));
        }, token);
    }

    public Task<AgentGrantReceipt> ClaimAsync(ClaimAgentGrant request, CancellationToken token = default)
    {
        Id(request.GrantId); Id(request.ClaimId);
        if (request.CertificateDer.IsEmpty || request.CertificateDer.Length > 16384) throw Error(AgentStoreError.InvalidCredential);
        return database.ExecuteAsync(new(Guid.Empty, GrantId: request.GrantId), async (tx, ct) =>
        {
            var grant = Grant(tx, request.GrantId);
            if (grant.State == AgentGrantState.Redeemed) ActivatedContext(tx, grant); else GrantContext(tx, grant);
            if (grant.Kind == AgentGrantKind.Enrollment && !Equal(grant.TokenHash, request.TokenHash.Span))
                throw Error(AgentStoreError.InvalidCredential);
            if (grant.Kind == AgentGrantKind.Rotation)
            {
                if (request.Initiator is null || request.Initiator.CredentialId != grant.InitiatingCredentialId)
                    throw Error(AgentStoreError.InvalidCredential);
                ActiveContext(tx, request.Initiator);
            }
            var digest = Digest(request.GrantId, request.ClaimId, Convert.ToHexString(SHA256.HashData(request.CertificateDer.Span)));
            if (grant.ClaimId is not null)
            {
                if (grant.ClaimId != request.ClaimId || !Equal(grant.ClaimDigest, digest)) throw Error(AgentStoreError.Conflict);
                var credential = tx.Credentials.Single(c => c.Id == grant.ClaimedCredentialId);
                if (!request.CertificateDer.Span.SequenceEqual(credential.Der)) throw Error(AgentStoreError.Conflict);
                return Receipt(grant, AgentGrantState.Claimed);
            }
            if (grant.State != AgentGrantState.Issued) throw Error(AgentStoreError.Conflict);
            if (grant.Kind == AgentGrantKind.Enrollment && !Equal(grant.TokenHash, request.TokenHash.Span)) throw Error(AgentStoreError.InvalidCredential);
            if (grant.Kind == AgentGrantKind.Rotation)
            {
                if (request.Initiator is null || request.Initiator.CredentialId != grant.InitiatingCredentialId)
                    throw Error(AgentStoreError.InvalidCredential);
                ActiveContext(tx, request.Initiator);
                if (!request.TokenHash.IsEmpty) throw Error(AgentStoreError.InvalidRequest);
            }
            if (tx.Credentials.Count >= 64) throw Error(AgentStoreError.Capacity);
            var facts = Certificate(request.CertificateDer, tx.Now);
            if (grant.Kind == AgentGrantKind.Rotation && tx.Credentials.Any(c => Equal(c.SpkiSha256, facts.SpkiSha256)))
                throw Error(AgentStoreError.InvalidCredential);
            var existing = await tx.FindPinAsync(facts.DerSha256, ct);
            if (existing is not null) throw Error(existing.Der.AsSpan().SequenceEqual(facts.Der) ? AgentStoreError.Conflict : AgentStoreError.PinCollision);
            facts.Id = Guid.CreateVersion7(); facts.AgentId = grant.AgentId; facts.State = AgentCredentialState.PendingProof;
            tx.Credentials.Add(facts);
            grant.ClaimId = request.ClaimId; grant.ClaimDigest = digest; grant.ClaimedCredentialId = facts.Id;
            grant.Nonce = RandomNumberGenerator.GetBytes(32); grant.ClaimedAtUtc = tx.Now;
            grant.State = AgentGrantState.Claimed; grant.Version = Next(grant.Version); grant.ClaimReceiptVersion = grant.Version;
            Event(tx, AgentSecurityEventKind.ClaimBound, grant);
            return Receipt(grant, AgentGrantState.Claimed);
        }, token);
    }

    public Task<AgentGrantReceipt> RecordProofAsync(ProveAgentGrant request, CancellationToken token = default)
    {
        ProofInput(request);
        return database.ExecuteAsync(new(request.Certificate.AgentId, GrantId: request.GrantId), (tx, _) =>
        {
            var grant = Grant(tx, request.GrantId);
            if (grant.State == AgentGrantState.Redeemed) ActivatedContext(tx, grant); else GrantContext(tx, grant);
            ProofContext(tx, grant, request);
            if (grant.ProofRequestId is not null)
            {
                if (grant.ProofRequestId != request.ProofRequestId) throw Error(AgentStoreError.Conflict);
                return Task.FromResult(Receipt(grant, AgentGrantState.Proven));
            }
            if (grant.State != AgentGrantState.Claimed) throw Error(AgentStoreError.Conflict);
            grant.ProofRequestId = request.ProofRequestId; grant.ProvedAtUtc = tx.Now; grant.State = AgentGrantState.Proven;
            grant.Version = Next(grant.Version); grant.ProofReceiptVersion = grant.Version;
            tx.Credentials.Single(c => c.Id == grant.ClaimedCredentialId).ProvedAtUtc = tx.Now;
            Event(tx, AgentSecurityEventKind.PossessionProved, grant);
            return Task.FromResult(Receipt(grant, AgentGrantState.Proven));
        }, token);
    }

    public Task<AgentGrantReceipt> ConfirmAsync(ConfirmAgentGrant request, CancellationToken token = default)
    {
        Actor(request.Actor); Id(request.GrantId); Positive(request.ExpectedGrantVersion); Bytes(request.Nonce, 32);
        return database.ExecuteAsync(new(Guid.Empty, GrantId: request.GrantId), (tx, _) =>
        {
            var grant = Grant(tx, request.GrantId);
            if (grant.Kind != AgentGrantKind.Enrollment || grant.Issuer != request.Actor.Issuer || grant.Subject != request.Actor.Subject)
                throw Error(AgentStoreError.ContextChanged);
            if (!Equal(grant.Nonce, request.Nonce.Span)) throw Error(AgentStoreError.Conflict);
            if (grant.State == AgentGrantState.Redeemed)
            {
                ActivatedContext(tx, grant);
                if (request.ExpectedGrantVersion != grant.ProofReceiptVersion) throw Error(AgentStoreError.ContextChanged);
                return Task.FromResult(Receipt(grant, AgentGrantState.Redeemed));
            }
            GrantContext(tx, grant);
            if (grant.State != AgentGrantState.Proven || grant.Version != request.ExpectedGrantVersion) throw Error(AgentStoreError.ContextChanged);
            Activate(tx, grant, request.Actor);
            return Task.FromResult(Receipt(grant, AgentGrantState.Redeemed));
        }, token);
    }

    public Task RevokeAsync(RevokeAgent request, CancellationToken token = default)
    {
        Id(request.AgentId); Actor(request.Actor); Positive(request.ExpectedVersion);
        return database.ExecuteAsync(new(request.AgentId), (tx, _) =>
        {
            var agent = Agent(tx);
            if (agent.Id != request.AgentId || agent.Version != request.ExpectedVersion) throw Error(AgentStoreError.ContextChanged);
            var previousAuthorizationVersion = agent.AuthorizationVersion; var previousFence = agent.SessionFence;
            agent.State = AgentLifecycleState.Revoked; agent.RevokedAtUtc = tx.Now;
            agent.RevokedByIssuer = request.Actor.Issuer; agent.RevokedBySubject = request.Actor.Subject;
            DenyAuthority(agent, tx.Now);
            foreach (var credential in tx.Credentials)
            { credential.State = AgentCredentialState.Revoked; credential.RevokedAtUtc = tx.Now; credential.Version = SecurityNext(credential.Version); }
            foreach (var assignment in tx.Assignments)
            { assignment.State = AgentAssignmentState.Revoked; assignment.Version = SecurityNext(assignment.Version); assignment.UpdatedAtUtc = tx.Now; }
            CancelLiveGrants(tx, securityDenial: true);
            Event(tx, AgentSecurityEventKind.Revoked, actor: request.Actor, previousAuthorizationVersion: previousAuthorizationVersion, previousFence: previousFence);
            return Task.FromResult(true);
        }, token);
    }

    public Task<AgentGrantReceipt> BeginRotationAsync(RotateAgentCredential request, CancellationToken token = default)
    {
        Provider(request.Provider); Id(request.ClientRequestId); Positive(request.ExpectedAuthorizationVersion);
        return database.ExecuteAsync(new(request.Certificate.AgentId, request.Provider.ProviderInstanceId,
            ClientRequestId: request.ClientRequestId, InitiatorId: request.Certificate.CredentialId), (tx, _) =>
        {
            var credential = ActiveContext(tx, request.Certificate);
            if (Agent(tx).AuthorizationVersion != request.ExpectedAuthorizationVersion) throw Error(AgentStoreError.ContextChanged);
            var provider = CurrentProvider(tx, request.Provider);
            var digest = Digest(provider.Id, provider.Version, request.ExpectedAuthorizationVersion, credential.Id);
            var replay = tx.Grants.SingleOrDefault(g => g.Kind == AgentGrantKind.Rotation &&
                g.InitiatingCredentialId == credential.Id && g.ClientRequestId == request.ClientRequestId);
            if (replay is not null)
            {
                if (!Equal(replay.RequestDigest, digest)) throw Error(AgentStoreError.Conflict);
                return Task.FromResult(Receipt(replay, AgentGrantState.Issued));
            }
            if (!tx.NativeWorkSettled || Agent(tx).RecoveryRequired) throw Error(AgentStoreError.ActionRequired);
            ExpireGrants(tx);
            if (tx.Grants.Count >= 128 || tx.Grants.Any(g => Live(g.State))) throw Error(AgentStoreError.Conflict);
            var assignment = tx.Assignments.SingleOrDefault(a => a.ProviderInstanceId == provider.Id) ?? throw Error(AgentStoreError.ContextChanged);
            var grant = NewGrant(tx, assignment, AgentGrantKind.Rotation, request.ClientRequestId, digest);
            grant.InitiatingCredentialId = grant.ExpectedActiveCredentialId = credential.Id;
            tx.Grants.Add(grant); Event(tx, AgentSecurityEventKind.RotationIssued, grant, credentialId: request.Certificate.CredentialId);
            return Task.FromResult(Receipt(grant, AgentGrantState.Issued));
        }, token);
    }

    public Task<AgentGrantReceipt> ActivateRotationAsync(ProveAgentGrant request, CancellationToken token = default)
    {
        ProofInput(request);
        return database.ExecuteAsync(new(request.Certificate.AgentId, GrantId: request.GrantId), (tx, _) =>
        {
            var grant = Grant(tx, request.GrantId);
            ProofContext(tx, grant, request);
            if (grant.Kind != AgentGrantKind.Rotation || grant.ProofRequestId != request.ProofRequestId) throw Error(AgentStoreError.Conflict);
            if (grant.State == AgentGrantState.Redeemed) ActivatedContext(tx, grant);
            else { GrantContext(tx, grant); if (grant.State != AgentGrantState.Proven) throw Error(AgentStoreError.Conflict); Activate(tx, grant); }
            return Task.FromResult(Receipt(grant, AgentGrantState.Redeemed));
        }, token);
    }

    public Task<AgentConnectOutcome> ConnectAsync(ConnectAgent request, CancellationToken token = default)
    {
        Id(request.BootId); Id(request.RequestId); Positive(request.ExpectedFence); Positive(request.ExpectedAuthorizationVersion);
        if (request.ProtocolVersion != 1 || request.UnresolvedCount is < 0 or > 4096 || request.PackageVersion is not { Length: > 0 and <= 64 } ||
            request.PackageVersion.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))) throw Error(AgentStoreError.InvalidRequest);
        var digest = Digest(request.Certificate.CredentialId, request.BootId, request.RequestId, request.ExpectedFence, request.ExpectedAuthorizationVersion,
            request.ProtocolVersion, request.PackageVersion, request.JournalReady, request.UnresolvedCount);
        return database.ExecuteAsync(new(request.Certificate.AgentId), (tx, _) =>
        {
            ActiveContext(tx, request.Certificate);
            var agent = Agent(tx);
            if (agent.AuthorizationVersion != request.ExpectedAuthorizationVersion) throw Error(AgentStoreError.ContextChanged);
            if (agent.RecoveryRequired) return Task.FromResult(new AgentConnectOutcome(AgentStoreError.ActionRequired, null));
            if (agent.ConnectRequestId == request.RequestId && agent.CurrentBootId == request.BootId)
            {
                if (!Equal(agent.ConnectDigest, digest)) throw Error(AgentStoreError.Conflict);
                return Task.FromResult(new AgentConnectOutcome(null, Session(agent)));
            }
            if (agent.ConnectRequestId == request.RequestId) throw Error(AgentStoreError.Conflict);
            if (request.ExpectedFence != agent.SessionFence) throw Error(AgentStoreError.ContextChanged);
            if (agent.LeaseExpiresAtUtc > tx.Now && agent.LeaseAuthorizationVersion == agent.AuthorizationVersion)
            {
                if (agent.CurrentBootId == request.BootId)
                    return Task.FromResult(new AgentConnectOutcome(AgentStoreError.SessionAlreadyActive, null));
                agent.State = AgentLifecycleState.Quarantined;
                var previousAuthorizationVersion = agent.AuthorizationVersion; var previousFence = agent.SessionFence;
                DenyAuthority(agent, tx.Now); Event(tx, AgentSecurityEventKind.ConflictingIncarnation,
                    previousAuthorizationVersion: previousAuthorizationVersion, previousFence: previousFence, credentialId: request.Certificate.CredentialId);
                return Task.FromResult(new AgentConnectOutcome(AgentStoreError.ConflictingIncarnation, null));
            }
            if (!tx.NativeWorkSettled || agent.RecoveryRequired || !request.JournalReady || request.UnresolvedCount != 0)
            {
                agent.RecoveryRequired = true; agent.Readiness = AgentReadiness.ActionRequired;
                Event(tx, AgentSecurityEventKind.RecoveryRequired, credentialId: request.Certificate.CredentialId);
                return Task.FromResult(new AgentConnectOutcome(AgentStoreError.ActionRequired, null));
            }
            var acquiredFromFence = agent.SessionFence;
            agent.SessionFence = Next(agent.SessionFence);
            agent.CurrentBootId = request.BootId; agent.ConnectRequestId = request.RequestId;
            agent.SessionId = Guid.CreateVersion7();
            agent.ConnectDigest = digest; agent.ConnectAcceptedAtUtc = tx.Now; agent.ConnectLeaseExpiresAtUtc = tx.Now.AddSeconds(60);
            agent.ConnectAuthorizationVersion = agent.LeaseAuthorizationVersion = agent.AuthorizationVersion;
            agent.ConnectFence = agent.SessionFence; agent.LeaseExpiresAtUtc = agent.ConnectLeaseExpiresAtUtc;
            agent.PollRequestId = null; agent.PollDigest = null; agent.PollSequence = 0; agent.PollAcknowledgedAtUtc = null;
            agent.PollLeaseExpiresAtUtc = null; agent.PollFence = null; agent.PollAuthorizationVersion = null; agent.NextPollSeconds = null;
            agent.ProtocolVersion = request.ProtocolVersion; agent.PackageVersion = request.PackageVersion;
            agent.LastContactAtUtc = agent.UpdatedAtUtc = tx.Now; agent.Readiness = AgentReadiness.ReadyForControlPlane;
            Event(tx, AgentSecurityEventKind.SessionAcquired, previousFence: acquiredFromFence, credentialId: request.Certificate.CredentialId);
            return Task.FromResult(new AgentConnectOutcome(null, Session(agent)));
        }, token);
    }

    public Task<AgentPollOutcome> PollAsync(PollAgent request, CancellationToken token = default)
    {
        Id(request.SessionId); Id(request.RequestId); Positive(request.Fence); Positive(request.AuthorizationVersion); Positive(request.Sequence);
        if (!Enum.IsDefined(request.Readiness) || request.UnresolvedCount is < 0 or > 4096) throw Error(AgentStoreError.InvalidRequest);
        var digest = Digest(request.SessionId, request.Fence, request.AuthorizationVersion, request.RequestId, request.Sequence,
            request.Readiness, request.JournalReady, request.UnresolvedCount);
        return database.ExecuteAsync(new(request.Certificate.AgentId), (tx, _) =>
        {
            ActiveContext(tx, request.Certificate);
            var agent = Agent(tx);
            if (agent.SessionId != request.SessionId ||
                agent.SessionFence != request.Fence || agent.AuthorizationVersion != request.AuthorizationVersion ||
                agent.LeaseAuthorizationVersion != agent.AuthorizationVersion) throw Error(AgentStoreError.ContextChanged);
            if (agent.RecoveryRequired) return Task.FromResult(new AgentPollOutcome(AgentStoreError.ActionRequired, null));
            if (request.Sequence < agent.PollSequence) throw Error(AgentStoreError.StaleReceipt);
            if (request.Sequence == agent.PollSequence)
            {
                if (request.RequestId != agent.PollRequestId || !Equal(agent.PollDigest, digest)) throw Error(AgentStoreError.Conflict);
                return Task.FromResult(new AgentPollOutcome(null, PollReceipt(agent)));
            }
            if (agent.PollSequence == long.MaxValue || request.Sequence != agent.PollSequence + 1 ||
                request.RequestId == agent.PollRequestId || agent.LeaseExpiresAtUtc <= tx.Now) throw Error(AgentStoreError.ContextChanged);
            if (!tx.NativeWorkSettled || agent.RecoveryRequired || !request.JournalReady || request.UnresolvedCount != 0)
            {
                var previousAuthorizationVersion = agent.AuthorizationVersion; var previousFence = agent.SessionFence;
                agent.RecoveryRequired = true; DenyAuthority(agent, tx.Now);
                Event(tx, AgentSecurityEventKind.RecoveryRequired, previousAuthorizationVersion: previousAuthorizationVersion, previousFence: previousFence,
                    credentialId: request.Certificate.CredentialId);
                return Task.FromResult(new AgentPollOutcome(AgentStoreError.ActionRequired, null));
            }
            agent.PollRequestId = request.RequestId; agent.PollSequence = request.Sequence; agent.PollDigest = digest;
            agent.PollAcknowledgedAtUtc = tx.Now; agent.PollLeaseExpiresAtUtc = tx.Now.AddSeconds(60);
            agent.PollFence = agent.SessionFence; agent.PollAuthorizationVersion = agent.AuthorizationVersion; agent.NextPollSeconds = 5;
            agent.LeaseExpiresAtUtc = agent.PollLeaseExpiresAtUtc; agent.LastContactAtUtc = agent.UpdatedAtUtc = tx.Now;
            agent.LastHeartbeatAtUtc = tx.Now;
            agent.Readiness = request.Readiness;
            return Task.FromResult(new AgentPollOutcome(null, PollReceipt(agent)));
        }, token);
    }

    public Task<AgentSessionStatus?> ReadSessionAsync(AgentCertificateContext certificate, CancellationToken token = default) =>
        database.ExecuteAsync(new(certificate.AgentId), (tx, _) =>
        {
            ActiveContext(tx, certificate);
            var agent = Agent(tx);
            return Task.FromResult(agent.SessionId is null ? null : new AgentSessionStatus(agent.Id, agent.SessionId.Value,
                agent.CurrentBootId!.Value, agent.SessionFence, agent.AuthorizationVersion, agent.LeaseAuthorizationVersion!.Value,
                agent.LeaseExpiresAtUtc!.Value, tx.Now, agent.ConnectFence == agent.SessionFence &&
                agent.LeaseAuthorizationVersion == agent.AuthorizationVersion && agent.LeaseExpiresAtUtc > tx.Now && !agent.RecoveryRequired,
                agent.RecoveryRequired));
        }, token);

    public Task<AgentGrantReceipt> ReadGrantAsync(Guid grantId, AgentCertificateContext certificate, CancellationToken token = default)
    {
        Id(grantId);
        return database.ExecuteAsync(new(certificate.AgentId, GrantId: grantId), (tx, _) =>
        {
            var grant = Grant(tx, grantId); var credential = Pin(tx, certificate);
            var oldRotationKey = grant.Kind == AgentGrantKind.Rotation && grant.InitiatingCredentialId == certificate.CredentialId;
            if (grant.ClaimedCredentialId != certificate.CredentialId && !oldRotationKey) throw Error(AgentStoreError.InvalidCredential);
            if (oldRotationKey && grant.State == AgentGrantState.Redeemed && credential.State != AgentCredentialState.RetiringReceiptsOnly)
                throw Error(AgentStoreError.InvalidCredential);
            if (grant.State == AgentGrantState.Redeemed) ActivatedContext(tx, grant);
            else GrantContext(tx, grant);
            return Task.FromResult(Receipt(grant, grant.State));
        }, token);
    }

    public Task<int> CleanupAsync(Guid agentId, CancellationToken token = default)
    {
        Id(agentId);
        return database.ExecuteAsync(new(agentId, Cleanup: true), (tx, _) =>
        {
            if (Agent(tx).RecoveryRequired || !tx.NativeWorkSettled) return Task.FromResult(0);
            ExpireGrants(tx);
            tx.DeleteGrants.AddRange(tx.Grants.Where(g => !Live(g.State) && g.ExpiresAtUtc < tx.Now.AddHours(-24))
                .OrderBy(g => g.ExpiresAtUtc).ThenBy(g => g.Id).Take(100));
            tx.DeleteEvents.AddRange(tx.Events.Where(e => e.RecordedAtUtc < tx.Now.AddDays(-90))
                .OrderBy(e => e.RecordedAtUtc).ThenBy(e => e.Id).Take(100 - tx.DeleteGrants.Count));
            return Task.FromResult(tx.DeleteGrants.Count + tx.DeleteEvents.Count);
        }, token);
    }

    private static void Activate(AgentTransaction tx, AgentGrantRecord grant, AgentActor? actor = null)
    {
        var agent = Agent(tx);
        if (!tx.NativeWorkSettled || agent.RecoveryRequired) throw Error(AgentStoreError.ActionRequired);
        var credential = tx.Credentials.Single(c => c.Id == grant.ClaimedCredentialId);
        if (credential.State != AgentCredentialState.PendingProof || credential.NotBeforeUtc > tx.Now || credential.NotAfterUtc <= tx.Now)
            throw Error(AgentStoreError.InvalidCredential);
        var old = tx.Credentials.SingleOrDefault(c => c.State == AgentCredentialState.Active);
        if (grant.Kind == AgentGrantKind.Rotation)
        {
            if (old is null || old.Id != grant.ExpectedActiveCredentialId) throw Error(AgentStoreError.ContextChanged);
            old.State = AgentCredentialState.RetiringReceiptsOnly; old.RetiringUntilUtc = tx.Now.AddHours(1); old.Version = Next(old.Version);
        }
        else if (old is not null) throw Error(AgentStoreError.Conflict);
        var previousAuthorizationVersion = agent.AuthorizationVersion;
        agent.AuthorizationVersion = Next(agent.AuthorizationVersion); agent.Version = Next(agent.Version);
        agent.State = AgentLifecycleState.Active; agent.UpdatedAtUtc = tx.Now;
        credential.State = AgentCredentialState.Active; credential.ActivatedAtUtc = tx.Now; credential.Version = Next(credential.Version);
        var assignment = tx.Assignments.Single(a => a.ProviderInstanceId == grant.ProviderInstanceId);
        assignment.State = AgentAssignmentState.Active; assignment.Version = Next(assignment.Version); assignment.UpdatedAtUtc = tx.Now;
        grant.State = AgentGrantState.Redeemed; grant.TokenHash = null; grant.RedeemedAtUtc = tx.Now; grant.Version = Next(grant.Version);
        grant.RedemptionReceiptVersion = grant.Version; grant.ActivatedAuthorizationVersion = agent.AuthorizationVersion;
        Event(tx, grant.Kind == AgentGrantKind.Enrollment ? AgentSecurityEventKind.Activated : AgentSecurityEventKind.Rotated, grant, actor, previousAuthorizationVersion);
    }

    private static AgentCredentialRecord ActiveContext(AgentTransaction tx, AgentCertificateContext certificate)
    {
        var credential = Pin(tx, certificate); var agent = Agent(tx);
        if (credential.State != AgentCredentialState.Active || agent.State != AgentLifecycleState.Active)
            throw Error(AgentStoreError.InvalidCredential);
        if (!tx.Assignments.Any() || tx.Assignments.Any(a => a.State != AgentAssignmentState.Active ||
            !tx.Providers.TryGetValue(a.ProviderInstanceId, out var provider) || !provider.Enabled ||
            a.ProviderVersion != provider.Version || a.ProviderCredentialVersion != provider.CredentialVersion))
            throw Error(AgentStoreError.ContextChanged);
        return credential;
    }

    private static AgentCredentialRecord Pin(AgentTransaction tx, AgentCertificateContext certificate)
    {
        var agent = Agent(tx);
        if (agent.Id != certificate.AgentId) throw Error(AgentStoreError.InvalidCredential);
        if (agent.State == AgentLifecycleState.Revoked) throw Error(AgentStoreError.Revoked);
        if (agent.State == AgentLifecycleState.Quarantined) throw Error(AgentStoreError.ActionRequired);
        var credential = tx.Credentials.SingleOrDefault(c => c.Id == certificate.CredentialId && c.AgentId == agent.Id);
        if (credential is null || certificate.CertificateDer.IsEmpty || certificate.CertificateDer.Length > 16384 ||
            !credential.Der.AsSpan().SequenceEqual(certificate.CertificateDer.Span)) throw Error(AgentStoreError.InvalidCredential);
        if (credential.State == AgentCredentialState.Revoked) throw Error(AgentStoreError.Revoked);
        if (credential.State == AgentCredentialState.Retired || credential.NotBeforeUtc > tx.Now || credential.NotAfterUtc <= tx.Now ||
            credential.State == AgentCredentialState.RetiringReceiptsOnly && credential.RetiringUntilUtc <= tx.Now)
            throw Error(AgentStoreError.InvalidCredential);
        return credential;
    }

    private static void ProofContext(AgentTransaction tx, AgentGrantRecord grant, ProveAgentGrant request)
    {
        Pin(tx, request.Certificate);
        if (grant.AgentId != request.Certificate.AgentId || grant.ClaimId != request.ClaimId ||
            grant.ClaimedCredentialId != request.Certificate.CredentialId || !Equal(grant.Nonce, request.Nonce.Span))
            throw Error(AgentStoreError.Conflict);
    }

    private static void GrantContext(AgentTransaction tx, AgentGrantRecord grant)
    {
        var agent = Agent(tx);
        if (agent.State == AgentLifecycleState.Revoked) throw Error(AgentStoreError.Revoked);
        if (agent.State == AgentLifecycleState.Quarantined || agent.RecoveryRequired) throw Error(AgentStoreError.ActionRequired);
        if (!Live(grant.State)) throw Error(AgentStoreError.Conflict);
        if (grant.ExpiresAtUtc <= tx.Now) throw Error(AgentStoreError.Expired);
        var assignment = tx.Assignments.SingleOrDefault(a => a.ProviderInstanceId == grant.ProviderInstanceId && a.AgentId == grant.AgentId);
        CurrentProvider(tx, new(grant.ProviderInstanceId, grant.ProviderVersion));
        if (agent.Version != grant.ExpectedAgentVersion || agent.AuthorizationVersion != grant.ExpectedAuthorizationVersion ||
            assignment is null || assignment.Version != grant.ExpectedAssignmentVersion || assignment.ProviderVersion != grant.ProviderVersion ||
            assignment.ProviderCredentialVersion != grant.ProviderCredentialVersion ||
            tx.Providers[grant.ProviderInstanceId].CredentialVersion != grant.ProviderCredentialVersion)
            throw Error(AgentStoreError.ContextChanged);
    }

    private static void ActivatedContext(AgentTransaction tx, AgentGrantRecord grant)
    {
        var credential = tx.Credentials.SingleOrDefault(c => c.Id == grant.ClaimedCredentialId);
        if (credential is null) throw Error(AgentStoreError.InvalidCredential);
        ActiveContext(tx, new(grant.AgentId, credential.Id, credential.Der));
        if (Agent(tx).AuthorizationVersion != grant.ActivatedAuthorizationVersion) throw Error(AgentStoreError.ContextChanged);
    }

    private static AgentGrantRecord NewGrant(AgentTransaction tx, AgentAssignmentRecord assignment, AgentGrantKind kind, Guid requestId, byte[] digest) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            AgentId = Agent(tx).Id,
            ProviderInstanceId = assignment.ProviderInstanceId,
            Kind = kind,
            ExpectedAgentVersion = Agent(tx).Version,
            ExpectedAuthorizationVersion = Agent(tx).AuthorizationVersion,
            ExpectedAssignmentVersion = assignment.Version,
            ProviderVersion = assignment.ProviderVersion,
            ProviderCredentialVersion = assignment.ProviderCredentialVersion,
            ClientRequestId = requestId,
            RequestDigest = digest,
            CreatedAtUtc = tx.Now,
            ExpiresAtUtc = tx.Now.AddMinutes(10)
        };

    private static AgentGrantReceipt Receipt(AgentGrantRecord grant, AgentGrantState stage) =>
        new(grant.Id, grant.AgentId, grant.ProviderInstanceId, stage,
            stage switch
            {
                AgentGrantState.Claimed => grant.ClaimReceiptVersion!.Value,
                AgentGrantState.Proven => grant.ProofReceiptVersion!.Value,
                AgentGrantState.Redeemed => grant.RedemptionReceiptVersion!.Value,
                _ => 1
            },
            stage switch
            {
                AgentGrantState.Claimed => grant.ClaimedAtUtc!.Value,
                AgentGrantState.Proven => grant.ProvedAtUtc!.Value,
                AgentGrantState.Redeemed => grant.RedeemedAtUtc!.Value,
                _ => grant.CreatedAtUtc
            },
            grant.ExpiresAtUtc, stage == AgentGrantState.Issued ? null : grant.ClaimId, stage == AgentGrantState.Issued ? null : grant.ClaimedCredentialId,
            stage == AgentGrantState.Issued ? null : grant.Nonce, stage == AgentGrantState.Redeemed ? grant.ActivatedAuthorizationVersion : null);

    private static AgentSessionReceipt Session(AgentRecord agent) => new(agent.Id, agent.SessionId!.Value, agent.CurrentBootId!.Value,
        agent.ConnectFence!.Value, agent.ConnectAuthorizationVersion!.Value, agent.ConnectAcceptedAtUtc!.Value, agent.ConnectLeaseExpiresAtUtc!.Value);
    private static AgentPollReceipt PollReceipt(AgentRecord agent) => new(agent.Id, agent.SessionId!.Value, agent.PollFence!.Value,
        agent.PollAuthorizationVersion!.Value, agent.PollRequestId!.Value, agent.PollSequence, agent.PollAcknowledgedAtUtc!.Value,
        agent.PollLeaseExpiresAtUtc!.Value, agent.NextPollSeconds!.Value);
    private static AgentGrantRecord Grant(AgentTransaction tx, Guid id) => tx.Grants.SingleOrDefault(g => g.Id == id) ?? throw Error(AgentStoreError.NotFound);
    private static AgentRecord Agent(AgentTransaction tx) => tx.Agent ?? throw Error(AgentStoreError.NotFound);
    private static AgentProviderSnapshot CurrentProvider(AgentTransaction tx, AgentProviderContext expected) =>
        tx.Providers.TryGetValue(expected.ProviderInstanceId, out var provider) && provider.Enabled && provider.Version == expected.ProviderVersion
            ? provider : throw Error(AgentStoreError.ContextChanged);

    internal static void DenyAuthority(AgentRecord agent, DateTimeOffset now)
    {
        // A terminal security denial must still commit at numeric exhaustion; no addition wraps.
        agent.Version = SecurityNext(agent.Version); agent.AuthorizationVersion = SecurityNext(agent.AuthorizationVersion);
        agent.SessionFence = SecurityNext(agent.SessionFence); agent.Readiness = AgentReadiness.ActionRequired; agent.UpdatedAtUtc = now;
    }
    private static void CancelLiveGrants(AgentTransaction tx, bool securityDenial = false)
    {
        foreach (var grant in tx.Grants.Where(g => Live(g.State))) { grant.State = AgentGrantState.Cancelled; grant.Version = securityDenial ? SecurityNext(grant.Version) : Next(grant.Version); grant.TokenHash = null; }
    }
    private static void ExpireGrants(AgentTransaction tx)
    {
        foreach (var grant in tx.Grants.Where(g => Live(g.State) && g.ExpiresAtUtc <= tx.Now))
        { grant.State = AgentGrantState.Expired; grant.Version = Next(grant.Version); grant.TokenHash = null; }
    }
    private static bool Live(AgentGrantState state) => state is AgentGrantState.Issued or AgentGrantState.Claimed or AgentGrantState.Proven;
    private static long Next(long value) => value is > 0 and < long.MaxValue ? value + 1 : throw Error(AgentStoreError.Capacity);
    private static long SecurityNext(long value) => value == long.MaxValue ? value : Next(value);
    private static void Id(Guid value) { if (value == Guid.Empty) throw Error(AgentStoreError.InvalidRequest); }
    private static void Positive(long value) { if (value <= 0) throw Error(AgentStoreError.InvalidRequest); }
    private static void Provider(AgentProviderContext provider) { Id(provider.ProviderInstanceId); Positive(provider.ProviderVersion); }
    private static void Bytes(ReadOnlyMemory<byte> bytes, int length) { if (bytes.Length != length) throw Error(AgentStoreError.InvalidRequest); }
    private static bool Equal(byte[]? left, ReadOnlySpan<byte> right) => left is not null && CryptographicOperations.FixedTimeEquals(left, right);
    private static void Actor(AgentActor actor)
    {
        if (actor is null || string.IsNullOrEmpty(actor.Issuer) || actor.Issuer.Length > 2048 || actor.Issuer.Any(char.IsControl) ||
            string.IsNullOrEmpty(actor.Subject) || actor.Subject.Length > 512 || actor.Subject.Any(char.IsControl)) throw Error(AgentStoreError.InvalidRequest);
    }
    private static void ProofInput(ProveAgentGrant request) { Id(request.GrantId); Id(request.ClaimId); Id(request.ProofRequestId); Bytes(request.Nonce, 32); }
    private static byte[] Digest(params object?[] fields)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write("flowplane.agent/store.v1");
        foreach (var field in fields) writer.Write(Convert.ToString(field, System.Globalization.CultureInfo.InvariantCulture) ?? "<null>");
        writer.Flush();
        return SHA256.HashData(stream.ToArray());
    }
    internal static byte[] ActorHash(string issuer, string subject) => Digest(issuer, subject);
    private static AgentCredentialRecord Certificate(ReadOnlyMemory<byte> der, DateTimeOffset now)
    {
        try
        {
            using var certificate = X509CertificateLoader.LoadCertificate(der.Span);
            using var key = certificate.GetRSAPublicKey();
            if (key is null || key.KeySize != 3072 || !certificate.RawData.AsSpan().SequenceEqual(der.Span) ||
                certificate.NotBefore.ToUniversalTime() > now.UtcDateTime || certificate.NotAfter.ToUniversalTime() <= now.UtcDateTime)
                throw Error(AgentStoreError.InvalidCredential);
            return new()
            {
                Der = der.ToArray(),
                DerSha256 = SHA256.HashData(der.Span),
                SpkiSha256 = SHA256.HashData(key.ExportSubjectPublicKeyInfo()),
                NotBeforeUtc = new(certificate.NotBefore.ToUniversalTime()),
                NotAfterUtc = new(certificate.NotAfter.ToUniversalTime()),
                CreatedAtUtc = now
            };
        }
        catch (CryptographicException) { throw Error(AgentStoreError.InvalidCredential); }
    }
    private static void Event(AgentTransaction tx, AgentSecurityEventKind kind, AgentGrantRecord? grant = null, AgentActor? actor = null,
        long? previousAuthorizationVersion = null, long? previousFence = null, Guid? credentialId = null) =>
        tx.Events.Add(new()
        {
            Id = Guid.CreateVersion7(),
            AgentId = Agent(tx).Id,
            CredentialId = credentialId ?? grant?.ClaimedCredentialId,
            GrantId = grant?.Id,
            Kind = kind,
            Issuer = actor?.Issuer,
            Subject = actor?.Subject,
            AuthorizationVersion = Agent(tx).AuthorizationVersion,
            Fence = Agent(tx).SessionFence,
            PreviousAuthorizationVersion = previousAuthorizationVersion ?? Agent(tx).AuthorizationVersion,
            PreviousFence = previousFence ?? Agent(tx).SessionFence,
            RecordedAtUtc = tx.Now
        });
    private static AgentStoreException Error(AgentStoreError code) => new(code);
}
