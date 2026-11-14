using System.Data.Common;
using System.Security.Cryptography;

using FlowPlane.Application.Agents;
using FlowPlane.Infrastructure.Persistence;
using FlowPlane.Infrastructure.Persistence.Agents;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FlowPlane.ProviderConfiguration.Tests;

public sealed class AgentControlReaderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-14T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly Guid AgentId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid ProviderId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid CredentialId = Guid.Parse("30000000-0000-0000-0000-000000000001");

    [Theory]
    [InlineData("provider-version")]
    [InlineData("secret-version")]
    [InlineData("disabled")]
    [InlineData("missing-provider")]
    [InlineData("assignment-state")]
    [InlineData("no-assignment")]
    [InlineData("second-assignment")]
    [InlineData("agent-state")]
    [InlineData("agent-version")]
    [InlineData("epoch")]
    [InlineData("fence")]
    [InlineData("recovery")]
    public void Active_authority_denies_every_obsolete_or_missing_context(string change)
    {
        var tx = Current();
        switch (change)
        {
            case "provider-version": tx.Providers[ProviderId] = new(ProviderId, 8, 4, true); break;
            case "secret-version": tx.Providers[ProviderId] = new(ProviderId, 7, 5, true); break;
            case "disabled": tx.Providers[ProviderId] = new(ProviderId, 7, 4, false); break;
            case "missing-provider": tx.Providers.Clear(); break;
            case "assignment-state": tx.Assignments[0].State = AgentAssignmentState.Suspended; break;
            case "no-assignment": tx.Assignments.Clear(); break;
            case "second-assignment": tx.Assignments.Add(new() { AgentId = AgentId, ProviderInstanceId = Guid.NewGuid(), State = AgentAssignmentState.Active }); break;
            case "agent-state": tx.Agent!.State = AgentLifecycleState.Quarantined; break;
            case "agent-version": tx.Agent!.Version = 0; break;
            case "epoch": tx.Agent!.AuthorizationVersion = 0; break;
            case "fence": tx.Agent!.SessionFence = 0; break;
            case "recovery": tx.Agent!.RecoveryRequired = true; break;
        }
        Assert.False(AgentControlReader.Authority(tx, tx.Credentials[0]).ContextCurrent);
        Assert.False(AgentControlReader.Health(tx).AuthorityCurrent);
    }

    [Fact]
    public void Authority_returns_current_epoch_and_public_identity_with_independent_buffers()
    {
        var tx = Current();
        var authority = AgentControlReader.Authority(tx, tx.Credentials[0]);
        Assert.True(authority.ContextCurrent);
        Assert.Equal(11, authority.AgentVersion);
        Assert.Equal(13, authority.AuthorizationVersion);
        Assert.Equal(17, authority.Fence);
        Assert.Equal(Now, authority.DatabaseTimeUtc);
        Assert.Equal(AgentId, authority.Certificate.AgentId);
        Assert.Equal(CredentialId, authority.Certificate.CredentialId);
        tx.Credentials[0].Der[0] = 99;
        Assert.Equal(new byte[] { 1, 2, 3 }, authority.Certificate.CertificateDer.ToArray());
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("not-yet-valid")]
    [InlineData("revoked")]
    [InlineData("retired")]
    [InlineData("retiring-no-deadline")]
    [InlineData("retiring-deadline")]
    [InlineData("revoked-at")]
    [InlineData("agent-revoked-at")]
    public void Invalid_credential_never_reports_current_context(string change)
    {
        var tx = Current();
        var credential = tx.Credentials[0];
        switch (change)
        {
            case "expired": credential.NotAfterUtc = Now; break;
            case "not-yet-valid": credential.NotBeforeUtc = Now.AddTicks(1); break;
            case "revoked": credential.State = AgentCredentialState.Revoked; break;
            case "retired": credential.State = AgentCredentialState.Retired; break;
            case "retiring-no-deadline": credential.State = AgentCredentialState.RetiringReceiptsOnly; break;
            case "retiring-deadline": credential.State = AgentCredentialState.RetiringReceiptsOnly; credential.RetiringUntilUtc = Now; break;
            case "revoked-at": credential.RevokedAtUtc = Now; break;
            case "agent-revoked-at": tx.Agent!.RevokedAtUtc = Now; break;
        }
        Assert.False(AgentControlReader.Authority(tx, credential).ContextCurrent);
        Assert.False(AgentControlReader.Health(tx).AuthorityCurrent);
    }

    [Fact]
    public void Pending_proof_requires_exact_live_grant_and_captured_agent_assignment_provider_context()
    {
        var tx = Current();
        tx.Agent!.State = AgentLifecycleState.Pending;
        tx.Credentials[0].State = AgentCredentialState.PendingProof;
        tx.Assignments[0].State = AgentAssignmentState.Pending;
        var grant = BoundGrant();
        tx.Grants.Add(grant);
        Assert.True(AgentControlReader.Authority(tx, tx.Credentials[0]).ContextCurrent);
        grant.ExpectedAuthorizationVersion++;
        Assert.False(AgentControlReader.Authority(tx, tx.Credentials[0]).ContextCurrent);
        grant.ExpectedAuthorizationVersion--;
        grant.ExpiresAtUtc = Now;
        Assert.False(AgentControlReader.Authority(tx, tx.Credentials[0]).ContextCurrent);
    }

    [Fact]
    public void Health_uses_database_time_and_rejects_expired_or_obsolete_lease_epoch()
    {
        var tx = Current();
        var health = AgentControlReader.Health(tx);
        Assert.Equal(Now, health.DatabaseTimeUtc);
        Assert.True(health.AuthorityCurrent);
        Assert.Equal(new AgentProviderContext(ProviderId, 7), Assert.Single(health.Providers));
        tx.Agent!.LeaseAuthorizationVersion = 12;
        Assert.False(AgentControlReader.Health(tx).AuthorityCurrent);
        tx.Agent.LeaseAuthorizationVersion = 13;
        tx.Agent.LeaseExpiresAtUtc = Now;
        Assert.False(AgentControlReader.Health(tx).AuthorityCurrent);
    }

    [Fact]
    public void Health_denies_a_lease_issued_under_an_older_session_fence()
    {
        var tx = Current();
        tx.Agent!.ConnectFence = 16;
        Assert.False(AgentControlReader.Health(tx).AuthorityCurrent);
    }

    [Fact]
    public void Grant_view_preserves_current_receipt_and_exact_actor_initiator_without_aliasing_public_buffers()
    {
        var grant = BoundGrant();
        grant.State = AgentGrantState.Proven;
        grant.Version = 3;
        grant.ProofReceiptVersion = 3;
        grant.ProvedAtUtc = Now.AddMinutes(-1);
        grant.Issuer = "https://issuer";
        grant.Subject = "exact-subject";
        grant.InitiatingCredentialId = Guid.NewGuid();
        grant.Nonce = Enumerable.Repeat((byte)8, 32).ToArray();
        var credential = Current().Credentials[0];
        var view = AgentControlReader.GrantView(grant, credential);
        Assert.Equal(AgentGrantState.Proven, view.Receipt.State);
        Assert.Equal(3, view.Receipt.Version);
        Assert.Equal(Now.AddMinutes(-1), view.Receipt.RecordedAtUtc);
        Assert.Equal(new AgentActor("https://issuer", "exact-subject"), view.Actor);
        Assert.Equal(grant.InitiatingCredentialId, view.InitiatingCredentialId);
        Assert.Equal(2, view.ClaimReceiptVersion);
        grant.Nonce[0] = 9;
        credential.Der[0] = 9;
        Assert.Equal(8, view.Receipt.Nonce![0]);
        Assert.Equal(new byte[] { 1, 2, 3 }, view.CertificateDer);
    }

    [Fact]
    public void Pins_are_bounded_and_snapshot_buffers_cannot_mutate_persisted_material()
    {
        var input = new AgentPublicPin(AgentId, CredentialId, [1, 2, 3]);
        var pins = AgentControlReader.Pins([input]);
        input.CertificateDer[0] = 9;
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(pins).CertificateDer);
        Assert.Equal(4096, AgentControlReader.Pins(Enumerable.Repeat(input, 4096).ToArray()).Count);
        var error = Assert.Throws<AgentStoreException>(() => AgentControlReader.Pins(Enumerable.Repeat(input, 4097).ToArray()));
        Assert.Equal(AgentStoreError.Capacity, error.Code);
    }

    [Theory]
    [InlineData(AgentGrantState.Issued, true)]
    [InlineData(AgentGrantState.Claimed, true)]
    [InlineData(AgentGrantState.Proven, true)]
    [InlineData(AgentGrantState.Redeemed, false)]
    [InlineData(AgentGrantState.Expired, false)]
    [InlineData(AgentGrantState.Cancelled, false)]
    public void Code_check_requires_live_enrollment_exact_32_byte_hash_and_unexpired_database_time(AgentGrantState state, bool expected)
    {
        var grant = BoundGrant();
        grant.State = state;
        grant.TokenHash = Enumerable.Repeat((byte)7, 32).ToArray();
        Assert.Equal(expected, AgentControlReader.MatchesCode(grant, Enumerable.Repeat((byte)7, 32).ToArray(), Now));
        Assert.False(AgentControlReader.MatchesCode(grant, Enumerable.Repeat((byte)6, 32).ToArray(), Now));
        Assert.False(AgentControlReader.MatchesCode(grant, new byte[31], Now));
        grant.ExpiresAtUtc = Now;
        Assert.False(AgentControlReader.MatchesCode(grant, grant.TokenHash, Now));
        grant.ExpiresAtUtc = Now.AddMinutes(1);
        grant.Kind = AgentGrantKind.Rotation;
        Assert.False(AgentControlReader.MatchesCode(grant, grant.TokenHash, Now));
    }

    [Fact]
    public void PostgreSql_queries_filter_exact_der_hash_and_claim_credential_and_bound_pin_registry()
    {
        using var db = DesignDb();
        var der = new byte[] { 1, 2, 3 };
        var credentialSql = AgentControlReader.CredentialQuery(db, der).ToQueryString();
        Assert.Contains("\"DerSha256\" =", credentialSql, StringComparison.Ordinal);
        Assert.Contains("\"Der\" =", credentialSql, StringComparison.Ordinal);
        Assert.Contains(Convert.ToHexString(SHA256.HashData(der)), credentialSql, StringComparison.OrdinalIgnoreCase);
        var claimSql = AgentControlReader.ClaimQuery(db, Guid.NewGuid(), CredentialId).ToQueryString();
        Assert.Contains("\"ClaimId\" =", claimSql, StringComparison.Ordinal);
        Assert.Contains("\"ClaimedCredentialId\" =", claimSql, StringComparison.Ordinal);
        var pinsSql = AgentControlReader.PinsQuery(db, Now).ToQueryString();
        Assert.Contains("LIMIT", pinsSql, StringComparison.Ordinal);
        Assert.Contains("4097", pinsSql, StringComparison.Ordinal);
        Assert.Contains("\"ExpiresAtUtc\" >", pinsSql, StringComparison.Ordinal);
        Assert.Contains("\"RetiringUntilUtc\" >", pinsSql, StringComparison.Ordinal);
        Assert.Contains("\"NotAfterUtc\" >", pinsSql, StringComparison.Ordinal);
        Assert.Contains("\"RevokedAtUtc\" IS NULL", pinsSql, StringComparison.Ordinal);
        Assert.Contains("\"ClaimedCredentialId\"", pinsSql, StringComparison.Ordinal);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Malformed_lookup_inputs_deny_before_opening_any_connection()
    {
        using var db = FaultDb(new IOException("This connection must never open."));
        var reader = new AgentControlReader(db);
        Assert.Null(await reader.ReadCredentialAsync(ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken));
        Assert.Null(await reader.ReadCredentialAsync(new byte[16385], TestContext.Current.CancellationToken));
        Assert.Null(await reader.ReadGrantAsync(Guid.Empty, TestContext.Current.CancellationToken));
        Assert.Null(await reader.FindClaimAsync(Guid.NewGuid(), Guid.Empty, TestContext.Current.CancellationToken));
        Assert.Null(await reader.FindClaimAsync(Guid.Empty, Guid.NewGuid(), TestContext.Current.CancellationToken));
        Assert.Null(await reader.ReadHealthAsync(Guid.Empty, TestContext.Current.CancellationToken));
        Assert.False(await reader.MatchesIssuedCodeAsync(Guid.NewGuid(), new byte[31], TestContext.Current.CancellationToken));
        Assert.False(await reader.MatchesIssuedCodeAsync(Guid.Empty, new byte[32], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Database_open_failure_returns_only_allowlisted_unavailability_without_exception_details()
    {
        using var db = FaultDb(new IOException("private connection canary"));
        var error = await Assert.ThrowsAsync<AgentStoreException>(() => new AgentControlReader(db).ReadPinsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(AgentStoreError.Unavailable, error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("canary", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var db = FaultDb(new OperationCanceledException(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AgentControlReader(db).ReadPinsAsync(cancellation.Token));
    }

    [Fact]
    public async Task Reader_does_not_clear_or_save_unrelated_pending_changes()
    {
        using var db = FaultDb(new IOException("This connection must never open."));
        var agent = Current().Agent!;
        db.Agents.Add(agent);
        var error = await Assert.ThrowsAsync<AgentStoreException>(() => new AgentControlReader(db).ReadPinsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(AgentStoreError.Unavailable, error.Code);
        Assert.Equal(EntityState.Added, db.Entry(agent).State);
    }

    [Fact]
    public void Grant_view_cannot_expose_another_agents_or_another_claims_public_certificate()
    {
        var grant = BoundGrant();
        var credential = Current().Credentials[0];
        credential.AgentId = Guid.NewGuid();
        Assert.Throws<AgentStoreException>(() => AgentControlReader.GrantView(grant, credential));
        credential.AgentId = AgentId;
        credential.Id = Guid.NewGuid();
        Assert.Throws<AgentStoreException>(() => AgentControlReader.GrantView(grant, credential));
    }

    private static FlowPlaneDbContext FaultDb(Exception failure) => new(new DbContextOptionsBuilder<FlowPlaneDbContext>()
        .UseNpgsql("Host=127.0.0.1;Port=1;Database=design_only;Username=design_only")
        .AddInterceptors(new FailedOpen(failure)).Options);

    private sealed class FailedOpen(Exception failure) : DbConnectionInterceptor
    {
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) => throw failure;
    }

    private static FlowPlaneDbContext DesignDb() => new(new DbContextOptionsBuilder<FlowPlaneDbContext>()
        .UseNpgsql("Host=127.0.0.1;Port=1;Database=design_only;Username=design_only").Options);

    private static AgentTransaction Current() => new()
    {
        Now = Now,
        Agent = new()
        {
            Id = AgentId,
            DisplayName = "Synthetic agent",
            State = AgentLifecycleState.Active,
            Version = 11,
            AuthorizationVersion = 13,
            SessionFence = 17,
            ConnectFence = 17,
            LeaseAuthorizationVersion = 13,
            LeaseExpiresAtUtc = Now.AddMinutes(1)
        },
        Credentials = [new() { Id = CredentialId, AgentId = AgentId, Der = [1, 2, 3], State = AgentCredentialState.Active,
            NotBeforeUtc = Now.AddDays(-1), NotAfterUtc = Now.AddDays(1) }],
        Assignments = [new() { AgentId = AgentId, ProviderInstanceId = ProviderId, State = AgentAssignmentState.Active,
            Version = 19, ProviderVersion = 7, ProviderCredentialVersion = 4 }],
        Providers = new() { [ProviderId] = new(ProviderId, 7, 4, true) }
    };

    private static AgentGrantRecord BoundGrant() => new()
    {
        Id = Guid.NewGuid(),
        AgentId = AgentId,
        ProviderInstanceId = ProviderId,
        Kind = AgentGrantKind.Enrollment,
        State = AgentGrantState.Claimed,
        Version = 2,
        ExpectedAgentVersion = 11,
        ExpectedAuthorizationVersion = 13,
        ExpectedAssignmentVersion = 19,
        ProviderVersion = 7,
        ProviderCredentialVersion = 4,
        ClaimId = Guid.NewGuid(),
        ClaimedCredentialId = CredentialId,
        ClaimReceiptVersion = 2,
        ClaimedAtUtc = Now.AddMinutes(-2),
        CreatedAtUtc = Now.AddMinutes(-3),
        ExpiresAtUtc = Now.AddMinutes(5)
    };
}
