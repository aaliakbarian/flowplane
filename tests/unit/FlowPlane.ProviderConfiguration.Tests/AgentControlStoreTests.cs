using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using FlowPlane.Application.Agents;
using FlowPlane.Infrastructure.Persistence.Agents;

namespace FlowPlane.ProviderConfiguration.Tests;

// Faultable transaction double only. These tests do NOT establish PostgreSQL locking/constraint correctness.
public sealed class AgentControlStoreTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-11T00:00:00Z");
    private static readonly AgentActor Actor = new("https://issuer.example/", "operator");
    private static readonly byte[] TokenHash = SHA256.HashData(new byte[32]);
    private readonly Database db = new();
    private AgentControlStore Store => new(db);

    [Fact]
    public async Task Lost_issue_acknowledgement_recovers_original_grant_without_reproducing_or_replacing_code_hash()
    {
        var request = Issue();
        db.LoseCommitAcknowledgement = true;
        await Error(AgentStoreError.CommitUncertain, () => Store.IssueGrantAsync(request, TestContext.Current.CancellationToken));
        var id = Assert.Single(db.State.Grants).Id;
        var replay = await Store.IssueGrantAsync(request with { TokenHash = new byte[32] }, TestContext.Current.CancellationToken);
        Assert.Equal(id, replay.GrantId);
        Assert.Equal(TokenHash, Assert.Single(db.State.Grants).TokenHash);
        Assert.Single(db.State.Events);
        Assert.DoesNotContain("TokenHash", replay.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Claim_and_proof_replays_are_immutable_and_cannot_substitute_certificate_or_nonce()
    {
        var grant = await Store.IssueGrantAsync(Issue(), TestContext.Current.CancellationToken);
        var claim = new ClaimAgentGrant(grant.GrantId, Guid.NewGuid(), TokenHash, Certificate());
        var receipt = await Store.ClaimAsync(claim, TestContext.Current.CancellationToken);
        db.Now = Now.AddMinutes(1);
        Assert.Equal(receipt, await Store.ClaimAsync(claim, TestContext.Current.CancellationToken));
        await Error(AgentStoreError.InvalidCredential, () => Store.ClaimAsync(claim with { TokenHash = new byte[32] }, TestContext.Current.CancellationToken));
        await Error(AgentStoreError.Conflict, () => Store.ClaimAsync(claim with { CertificateDer = Certificate() }, TestContext.Current.CancellationToken));
        var proof = new ProveAgentGrant(grant.GrantId, claim.ClaimId, Guid.NewGuid(), receipt.Nonce!,
            new(grant.AgentId, receipt.CredentialId!.Value, claim.CertificateDer));
        await Error(AgentStoreError.Conflict, () => Store.RecordProofAsync(proof with { Nonce = new byte[32] }, TestContext.Current.CancellationToken));
        var proved = await Store.RecordProofAsync(proof, TestContext.Current.CancellationToken);
        db.Now = Now.AddMinutes(2);
        Assert.Equal(proved, await Store.RecordProofAsync(proof, TestContext.Current.CancellationToken));
        await Error(AgentStoreError.Conflict, () => Store.RecordProofAsync(proof with { ProofRequestId = Guid.NewGuid() }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Confirmation_checks_original_actor_and_versions_and_activates_only_once()
    {
        var (proof, receipt) = await Proved();
        var confirm = new ConfirmAgentGrant(proof.GrantId, Actor, receipt.Version, proof.Nonce);
        await Error(AgentStoreError.ContextChanged, () => Store.ConfirmAsync(confirm with { Actor = new(Actor.Issuer, "other") }, TestContext.Current.CancellationToken));
        var active = await Store.ConfirmAsync(confirm, TestContext.Current.CancellationToken);
        var epoch = db.State.Agent!.AuthorizationVersion;
        Assert.Equal(AgentGrantState.Redeemed, active.State);
        Assert.Equal(AgentLifecycleState.Active, db.State.Agent.State);
        Assert.Equal(AgentCredentialState.Active, Assert.Single(db.State.Credentials).State);
        Assert.Equal(active, await Store.ConfirmAsync(confirm, TestContext.Current.CancellationToken));
        Assert.Equal(epoch, db.State.Agent.AuthorizationVersion);
        Assert.Equal(1, db.State.Events.Count(row => row.Kind == AgentSecurityEventKind.Activated));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_expiry_or_changed_provider_context_prevents_activation(bool changeProvider)
    {
        var (proof, receipt) = await Proved();
        if (changeProvider) db.State.Providers[db.ProviderId] = new(db.ProviderId, 2, 1, true);
        else db.Now = receipt.ExpiresAtUtc;
        await Error(changeProvider ? AgentStoreError.ContextChanged : AgentStoreError.Expired,
            () => Store.ConfirmAsync(new(proof.GrantId, Actor, receipt.Version, proof.Nonce), TestContext.Current.CancellationToken));
        Assert.Equal(AgentLifecycleState.Pending, db.State.Agent!.State);
    }

    [Fact]
    public async Task Original_connect_and_latest_poll_receipts_replay_without_extending_a_lease()
    {
        var certificate = await Enroll();
        var connect = Connect(certificate);
        var receipt = (await Store.ConnectAsync(connect, TestContext.Current.CancellationToken)).Receipt!;
        Assert.Equal(certificate.CredentialId, Assert.Single(db.State.Events, e => e.Kind == AgentSecurityEventKind.SessionAcquired).CredentialId);
        db.Now = Now.AddSeconds(20);
        var poll = new PollAgent(certificate, receipt.SessionId, receipt.Fence, receipt.AuthorizationVersion,
            Guid.NewGuid(), 1, AgentReadiness.ReadyForControlPlane, true, 0);
        var first = (await Store.PollAsync(poll, TestContext.Current.CancellationToken)).Receipt!;
        db.Now = Now.AddSeconds(30);
        Assert.Equal(first, (await Store.PollAsync(poll, TestContext.Current.CancellationToken)).Receipt);
        Assert.Equal(receipt, (await Store.ConnectAsync(connect, TestContext.Current.CancellationToken)).Receipt);
        var second = (await Store.PollAsync(poll with { RequestId = Guid.NewGuid(), Sequence = 2 }, TestContext.Current.CancellationToken)).Receipt!;
        await Error(AgentStoreError.StaleReceipt, () => Store.PollAsync(poll, TestContext.Current.CancellationToken));
        Assert.Equal(2, second.Sequence);
        db.Now = Now.AddMinutes(2);
        Assert.Equal(receipt, (await Store.ConnectAsync(connect, TestContext.Current.CancellationToken)).Receipt);
        Assert.Equal(second.LeaseExpiresAtUtc, db.State.Agent!.LeaseExpiresAtUtc);
        var status = await Store.ReadSessionAsync(certificate, TestContext.Current.CancellationToken);
        Assert.Equal(second.LeaseExpiresAtUtc, status!.LeaseExpiresAtUtc);
    }

    [Fact]
    public async Task Different_live_boot_quarantines_durably_and_cannot_gain_a_session()
    {
        var certificate = await Enroll();
        var request = Connect(certificate);
        var first = (await Store.ConnectAsync(request, TestContext.Current.CancellationToken)).Receipt!;
        var conflict = await Store.ConnectAsync(request with { BootId = Guid.NewGuid(), RequestId = Guid.NewGuid(), ExpectedFence = first.Fence }, TestContext.Current.CancellationToken);
        Assert.Equal(AgentStoreError.ConflictingIncarnation, conflict.Error);
        Assert.Null(conflict.Receipt);
        Assert.Equal(AgentLifecycleState.Quarantined, db.State.Agent!.State);
        await Error(AgentStoreError.ActionRequired, () => Store.ConnectAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Expired_lease_requires_current_fence_and_ready_evidence_without_reusing_old_session()
    {
        var certificate = await Enroll();
        var request = Connect(certificate);
        var first = (await Store.ConnectAsync(request, TestContext.Current.CancellationToken)).Receipt!;
        db.Now = first.LeaseExpiresAtUtc;
        var next = request with { BootId = Guid.NewGuid(), RequestId = Guid.NewGuid(), ExpectedFence = first.Fence };
        var acquired = (await Store.ConnectAsync(next, TestContext.Current.CancellationToken)).Receipt!;
        Assert.True(acquired.Fence > first.Fence);
        Assert.NotEqual(first.SessionId, acquired.SessionId);
        await Error(AgentStoreError.ContextChanged, () => Store.ConnectAsync(next with { RequestId = Guid.NewGuid() }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Missing_journal_or_unsettled_guard_never_gains_authority()
    {
        var certificate = await Enroll();
        db.NativeWorkSettled = false;
        var denied = await Store.ConnectAsync(Connect(certificate), TestContext.Current.CancellationToken);
        Assert.Equal(AgentStoreError.ActionRequired, denied.Error);
        Assert.Null(db.State.Agent!.SessionId);
    }

    [Fact]
    public async Task Revocation_overrides_saved_receipts_and_cancels_pending_transitions()
    {
        var certificate = await Enroll();
        var connect = Connect(certificate);
        await Store.ConnectAsync(connect, TestContext.Current.CancellationToken);
        await Store.RevokeAsync(new(certificate.AgentId, Actor, db.State.Agent!.Version), TestContext.Current.CancellationToken);
        await Error(AgentStoreError.Revoked, () => Store.ConnectAsync(connect, TestContext.Current.CancellationToken));
        Assert.All(db.State.Credentials, row => Assert.Equal(AgentCredentialState.Revoked, row.State));
        Assert.Equal(AgentLifecycleState.Revoked, db.State.Agent!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revoked_identity_cannot_replay_or_reenroll_into_pending_authority(bool replay)
    {
        var request = Issue();
        var grant = await Store.IssueGrantAsync(request, TestContext.Current.CancellationToken);
        await Store.RevokeAsync(new(grant.AgentId, Actor, db.State.Agent!.Version), TestContext.Current.CancellationToken);
        var retry = replay ? request : request with
        {
            ExistingAgentId = grant.AgentId,
            ExpectedAgentVersion = db.State.Agent!.Version,
            ClientRequestId = Guid.NewGuid()
        };
        await Error(AgentStoreError.Revoked, () => Store.IssueGrantAsync(retry, TestContext.Current.CancellationToken));
        Assert.Equal(AgentLifecycleState.Revoked, db.State.Agent!.State);
    }

    [Fact]
    public async Task Rotation_requires_new_key_proof_and_retires_old_authority_without_receipt_mutation()
    {
        var certificate = await Enroll();
        var rotation = await Store.BeginRotationAsync(new(new(db.ProviderId, 1), certificate,
            db.State.Agent!.AuthorizationVersion, Guid.NewGuid()), TestContext.Current.CancellationToken);
        Assert.Equal(certificate.CredentialId, Assert.Single(db.State.Events, e => e.Kind == AgentSecurityEventKind.RotationIssued).CredentialId);
        var claim = new ClaimAgentGrant(rotation.GrantId, Guid.NewGuid(), ReadOnlyMemory<byte>.Empty, Certificate());
        await Error(AgentStoreError.InvalidCredential, () => Store.ClaimAsync(claim, TestContext.Current.CancellationToken));
        claim = claim with { Initiator = certificate };
        var pending = await Store.ClaimAsync(claim, TestContext.Current.CancellationToken);
        var proof = new ProveAgentGrant(rotation.GrantId, claim.ClaimId, Guid.NewGuid(), pending.Nonce!,
            new(certificate.AgentId, pending.CredentialId!.Value, claim.CertificateDer));
        await Store.RecordProofAsync(proof, TestContext.Current.CancellationToken);
        var active = await Store.ActivateRotationAsync(proof, TestContext.Current.CancellationToken);
        Assert.Equal(AgentGrantState.Redeemed, active.State);
        Assert.Equal(AgentCredentialState.RetiringReceiptsOnly, db.State.Credentials.Single(x => x.Id == certificate.CredentialId).State);
        Assert.Equal(active, await Store.ActivateRotationAsync(proof, TestContext.Current.CancellationToken));
        await Error(AgentStoreError.InvalidCredential, () => Store.ConnectAsync(Connect(certificate), TestContext.Current.CancellationToken));
        Assert.Equal(active, await Store.ReadGrantAsync(rotation.GrantId, certificate, TestContext.Current.CancellationToken));
        db.Now = db.State.Credentials.Single(x => x.Id == certificate.CredentialId).RetiringUntilUtc!.Value;
        await Error(AgentStoreError.InvalidCredential, () => Store.ReadGrantAsync(rotation.GrantId, certificate, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cross_agent_or_same_key_different_der_is_not_authority()
    {
        var certificate = await Enroll();
        await Error(AgentStoreError.InvalidCredential, () => Store.ConnectAsync(Connect(certificate with { AgentId = Guid.NewGuid() }), TestContext.Current.CancellationToken));
        await Error(AgentStoreError.InvalidCredential, () => Store.ConnectAsync(Connect(certificate with { CertificateDer = Certificate() }), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rotation_rejects_reissued_certificate_using_the_same_public_key()
    {
        using var key = RSA.Create(3072);
        var der = Certificate(key);
        var grant = await Store.IssueGrantAsync(Issue(), TestContext.Current.CancellationToken);
        var claimId = Guid.NewGuid();
        var claimed = await Store.ClaimAsync(new(grant.GrantId, claimId, TokenHash, der), TestContext.Current.CancellationToken);
        var certificate = new AgentCertificateContext(grant.AgentId, claimed.CredentialId!.Value, der);
        var proof = new ProveAgentGrant(grant.GrantId, claimId, Guid.NewGuid(), claimed.Nonce!, certificate);
        var proved = await Store.RecordProofAsync(proof, TestContext.Current.CancellationToken);
        await Store.ConfirmAsync(new(grant.GrantId, Actor, proved.Version, proved.Nonce!), TestContext.Current.CancellationToken);
        var rotation = await Store.BeginRotationAsync(new(new(db.ProviderId, 1), certificate,
            db.State.Agent!.AuthorizationVersion, Guid.NewGuid()), TestContext.Current.CancellationToken);
        var reissued = Certificate(key);
        Assert.False(der.AsSpan().SequenceEqual(reissued));
        await Error(AgentStoreError.InvalidCredential, () => Store.ClaimAsync(new(rotation.GrantId, Guid.NewGuid(),
            ReadOnlyMemory<byte>.Empty, reissued, certificate), TestContext.Current.CancellationToken));
        Assert.Single(db.State.Credentials);
    }

    [Fact]
    public void Provider_version_change_suspends_only_target_assignment_but_fences_entire_agent_and_pending_grants()
    {
        var agent = new AgentRecord { Id = Guid.NewGuid(), Version = 4, AuthorizationVersion = 7, SessionFence = 9 };
        var assignment = new AgentAssignmentRecord { ProviderInstanceId = db.ProviderId, AgentId = agent.Id, State = AgentAssignmentState.Active, Version = 2 };
        var other = assignment with { ProviderInstanceId = Guid.NewGuid() };
        var grant = new AgentGrantRecord { State = AgentGrantState.Proven, TokenHash = TokenHash };
        AgentDatabase.FenceProviderRecords(agent, [assignment, other], [grant], [db.ProviderId], Now);
        Assert.Equal(8, agent.AuthorizationVersion);
        Assert.Equal(10, agent.SessionFence);
        Assert.Equal(AgentAssignmentState.Suspended, assignment.State);
        Assert.Equal(3, assignment.Version);
        Assert.Equal(AgentAssignmentState.Active, other.State);
        Assert.Equal(AgentGrantState.Cancelled, grant.State);
        Assert.Null(grant.TokenHash);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Provider_deletion_guard_rejects_retained_agent_ownership(bool assignments, bool grants) =>
        Assert.Equal(AgentStoreError.Conflict, Assert.Throws<AgentStoreException>(() => AgentDatabase.EnsureProviderDeletable(assignments, grants)).Code);

    [Fact]
    public async Task Checked_fence_overflow_rolls_back_without_partial_receipts_or_events()
    {
        var certificate = await Enroll();
        db.State.Agent!.SessionFence = long.MaxValue;
        var events = db.State.Events.Count;
        await Error(AgentStoreError.Capacity, () => Store.ConnectAsync(Connect(certificate), TestContext.Current.CancellationToken));
        Assert.Null(db.State.Agent.SessionId);
        Assert.Equal(events, db.State.Events.Count);
    }

    private IssueAgentGrant Issue() => new(new(db.ProviderId, 1), Actor, Guid.NewGuid(), "Synthetic Agent", TokenHash);
    private ConnectAgent Connect(AgentCertificateContext certificate) => new(certificate, Guid.NewGuid(), Guid.NewGuid(),
        db.State.Agent!.SessionFence, db.State.Agent.AuthorizationVersion, 1, "0.1.0", true, 0);
    private async Task<(ProveAgentGrant, AgentGrantReceipt)> Proved()
    {
        var grant = await Store.IssueGrantAsync(Issue(), TestContext.Current.CancellationToken);
        var der = Certificate();
        var claimId = Guid.NewGuid();
        var claim = await Store.ClaimAsync(new(grant.GrantId, claimId, TokenHash, der), TestContext.Current.CancellationToken);
        var proof = new ProveAgentGrant(grant.GrantId, claimId, Guid.NewGuid(), claim.Nonce!, new(grant.AgentId, claim.CredentialId!.Value, der));
        return (proof, await Store.RecordProofAsync(proof, TestContext.Current.CancellationToken));
    }
    private async Task<AgentCertificateContext> Enroll()
    {
        var (proof, receipt) = await Proved();
        await Store.ConfirmAsync(new(proof.GrantId, Actor, receipt.Version, proof.Nonce), TestContext.Current.CancellationToken);
        return proof.Certificate;
    }
    private static byte[] Certificate()
    {
        using var key = RSA.Create(3072);
        return Certificate(key);
    }
    private static byte[] Certificate(RSA key)
    {
        var request = new CertificateRequest("CN=Synthetic", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, true));
        using var certificate = request.CreateSelfSigned(Now.AddDays(-1), Now.AddDays(90));
        return certificate.RawData;
    }
    private static async Task Error(AgentStoreError code, Func<Task> action) =>
        Assert.Equal(code, (await Assert.ThrowsAsync<AgentStoreException>(action)).Code);

    private sealed class Database : IAgentDatabase
    {
        public Guid ProviderId { get; } = Guid.NewGuid();
        public DateTimeOffset Now { get; set; } = AgentControlStoreTests.Now;
        public bool LoseCommitAcknowledgement { get; set; }
        public bool NativeWorkSettled { get; set; } = true;
        public AgentTransaction State { get; private set; }
        public Database() => State = new() { Providers = new() { [ProviderId] = new(ProviderId, 1, 1, true) } };
        public async Task<T> ExecuteAsync<T>(AgentStoreScope scope, Func<AgentTransaction, CancellationToken, Task<T>> operation, CancellationToken token)
        {
            var tx = new AgentTransaction
            {
                Now = Now,
                Agent = State.Agent is null ? null : State.Agent with { },
                Credentials = State.Credentials.Select(x => x with { }).ToList(),
                Grants = State.Grants.Select(x => x with { }).ToList(),
                Assignments = State.Assignments.Select(x => x with { }).ToList(),
                Events = State.Events.Select(x => x with { }).ToList(),
                Providers = State.Providers.ToDictionary(),
                NativeWorkSettled = NativeWorkSettled,
                FindPinAsync = (hash, _) => Task.FromResult(State.Credentials.SingleOrDefault(x => x.DerSha256.AsSpan().SequenceEqual(hash)))
            };
            var result = await operation(tx, token);
            foreach (var row in tx.DeleteGrants) tx.Grants.Remove(row);
            foreach (var row in tx.DeleteEvents) tx.Events.Remove(row);
            State = tx;
            if (LoseCommitAcknowledgement) { LoseCommitAcknowledgement = false; throw new AgentStoreException(AgentStoreError.CommitUncertain); }
            return result;
        }
    }
}
