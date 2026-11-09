using FlowPlane.Application.Agents;

using static FlowPlane.Application.Agents.AgentControlError;

namespace FlowPlane.Agent.Tests;

public sealed class AgentLifecyclePolicyTests
{
    // Columns are PairingProof, PairingStatus, Connect, Poll and RotationReceipt, respectively.
    public static TheoryData<AgentLifecycleState, AgentCredentialState, AgentControlError[]> AuthorityMatrix => new()
    {
        { AgentLifecycleState.Pending, AgentCredentialState.PendingProof, [None, None, OperationNotAllowed, OperationNotAllowed, OperationNotAllowed] },
        { AgentLifecycleState.Pending, AgentCredentialState.Active, [OperationNotAllowed, OperationNotAllowed, OperationNotAllowed, OperationNotAllowed, OperationNotAllowed] },
        { AgentLifecycleState.Pending, AgentCredentialState.RetiringReceiptsOnly, [OperationNotAllowed, OperationNotAllowed, OperationNotAllowed, OperationNotAllowed, OperationNotAllowed] },
        { AgentLifecycleState.Pending, AgentCredentialState.Retired, [InvalidCredential, InvalidCredential, InvalidCredential, InvalidCredential, InvalidCredential] },
        { AgentLifecycleState.Pending, AgentCredentialState.Revoked, [Revoked, Revoked, Revoked, Revoked, Revoked] },
        { AgentLifecycleState.Active, AgentCredentialState.PendingProof, [None, None, OperationNotAllowed, OperationNotAllowed, OperationNotAllowed] },
        { AgentLifecycleState.Active, AgentCredentialState.Active, [None, None, None, None, None] },
        { AgentLifecycleState.Active, AgentCredentialState.RetiringReceiptsOnly, [OperationNotAllowed, OperationNotAllowed, OperationNotAllowed, OperationNotAllowed, None] },
        { AgentLifecycleState.Active, AgentCredentialState.Retired, [InvalidCredential, InvalidCredential, InvalidCredential, InvalidCredential, InvalidCredential] },
        { AgentLifecycleState.Active, AgentCredentialState.Revoked, [Revoked, Revoked, Revoked, Revoked, Revoked] },
        { AgentLifecycleState.Quarantined, AgentCredentialState.PendingProof, [ActionRequired, ActionRequired, ActionRequired, ActionRequired, ActionRequired] },
        { AgentLifecycleState.Quarantined, AgentCredentialState.Active, [ActionRequired, ActionRequired, ActionRequired, ActionRequired, ActionRequired] },
        { AgentLifecycleState.Quarantined, AgentCredentialState.RetiringReceiptsOnly, [ActionRequired, ActionRequired, ActionRequired, ActionRequired, ActionRequired] },
        { AgentLifecycleState.Quarantined, AgentCredentialState.Retired, [InvalidCredential, InvalidCredential, InvalidCredential, InvalidCredential, InvalidCredential] },
        { AgentLifecycleState.Quarantined, AgentCredentialState.Revoked, [Revoked, Revoked, Revoked, Revoked, Revoked] },
        { AgentLifecycleState.Revoked, AgentCredentialState.PendingProof, [Revoked, Revoked, Revoked, Revoked, Revoked] },
        { AgentLifecycleState.Revoked, AgentCredentialState.Active, [Revoked, Revoked, Revoked, Revoked, Revoked] },
        { AgentLifecycleState.Revoked, AgentCredentialState.RetiringReceiptsOnly, [Revoked, Revoked, Revoked, Revoked, Revoked] },
        { AgentLifecycleState.Revoked, AgentCredentialState.Retired, [Revoked, Revoked, Revoked, Revoked, Revoked] },
        { AgentLifecycleState.Revoked, AgentCredentialState.Revoked, [Revoked, Revoked, Revoked, Revoked, Revoked] },
    };

    [Theory]
    [MemberData(nameof(AuthorityMatrix))]
    public void Every_lifecycle_and_credential_state_returns_the_exact_action_decision(
        AgentLifecycleState agent, AgentCredentialState credential, AgentControlError[] expectedErrors)
    {
        AgentControlAction[] actions = [AgentControlAction.PairingProof, AgentControlAction.PairingStatus,
            AgentControlAction.Connect, AgentControlAction.Poll, AgentControlAction.RotationReceipt];
        Assert.Equal(actions.Length, expectedErrors.Length);
        for (var index = 0; index < actions.Length; index++)
            Assert.Equal(new(expectedErrors[index] == None, expectedErrors[index]),
                AgentLifecyclePolicy.Authorize(new(agent, credential, true, true), actions[index]));
    }

    [Theory]
    [InlineData(true, false, InvalidCredential)]
    [InlineData(false, true, ContextChanged)]
    [InlineData(false, false, InvalidCredential)]
    public void Expired_and_stale_authorities_return_the_exact_error_for_every_action(
        bool current, bool withinValidity, AgentControlError expectedError)
    {
        foreach (var action in Enum.GetValues<AgentControlAction>())
            Assert.Equal(new(false, expectedError), AgentLifecyclePolicy.Authorize(
                new(AgentLifecycleState.Active, AgentCredentialState.Active, current, withinValidity), action));
    }

    [Theory]
    [InlineData(AgentCredentialState.PendingProof)]
    [InlineData(AgentCredentialState.RetiringReceiptsOnly)]
    [InlineData(AgentCredentialState.Retired)]
    [InlineData(AgentCredentialState.Revoked)]
    public void Pending_or_retiring_identity_cannot_poll(AgentCredentialState state) =>
        Assert.False(AgentLifecyclePolicy.Authorize(new(AgentLifecycleState.Active, state, true, true), AgentControlAction.Poll).Allowed);

    [Fact]
    public void Pending_proof_has_no_control_authority()
    {
        var context = new AgentAuthorityContext(AgentLifecycleState.Pending, AgentCredentialState.PendingProof, true, true);
        Assert.True(AgentLifecyclePolicy.Authorize(context, AgentControlAction.PairingProof).Allowed);
        Assert.True(AgentLifecyclePolicy.Authorize(context, AgentControlAction.PairingStatus).Allowed);
        Assert.False(AgentLifecyclePolicy.Authorize(context, AgentControlAction.Connect).Allowed);
    }

    [Fact]
    public void Only_active_current_authority_can_connect_or_poll()
    {
        var context = new AgentAuthorityContext(AgentLifecycleState.Active, AgentCredentialState.Active, true, true);
        Assert.Equal(new AgentPolicyDecision(true, AgentControlError.None), AgentLifecyclePolicy.Authorize(context, AgentControlAction.Connect));
        Assert.True(AgentLifecyclePolicy.Authorize(context, AgentControlAction.Poll).Allowed);
        Assert.False(AgentLifecyclePolicy.Authorize(context with { AgentState = AgentLifecycleState.Pending }, AgentControlAction.Poll).Allowed);
    }

    [Fact]
    public void Revocation_overrides_old_authority_and_receipt_replay()
    {
        var context = new AgentAuthorityContext(AgentLifecycleState.Revoked, AgentCredentialState.Active, true, true);
        foreach (var action in Enum.GetValues<AgentControlAction>())
            Assert.Equal(new(false, AgentControlError.Revoked), AgentLifecyclePolicy.Authorize(context, action));
    }

    [Fact]
    public void Expired_or_stale_authority_is_denied()
    {
        var context = new AgentAuthorityContext(AgentLifecycleState.Active, AgentCredentialState.Active, true, false);
        Assert.False(AgentLifecyclePolicy.Authorize(context, AgentControlAction.Poll).Allowed);
        Assert.Equal(AgentControlError.ContextChanged,
            AgentLifecyclePolicy.Authorize(context with { CertificateWithinValidity = true, IsCurrentAuthority = false }, AgentControlAction.Poll).Error);
    }

    [Fact]
    public void Quarantine_and_unknown_states_fail_closed()
    {
        var context = new AgentAuthorityContext(AgentLifecycleState.Quarantined, AgentCredentialState.Active, true, true);
        Assert.Equal(AgentControlError.ActionRequired, AgentLifecyclePolicy.Authorize(context, AgentControlAction.Connect).Error);
        Assert.Equal(new(false, InvalidCredential), AgentLifecyclePolicy.Authorize(context with { AgentState = (AgentLifecycleState)999 }, AgentControlAction.Poll));
        Assert.Equal(new(false, InvalidCredential), AgentLifecyclePolicy.Authorize(context with { AgentState = AgentLifecycleState.Active, CredentialState = (AgentCredentialState)999 }, AgentControlAction.Poll));
        Assert.Equal(new(false, InvalidCredential), AgentLifecyclePolicy.Authorize(context, (AgentControlAction)999));
        Assert.Equal(new(false, InvalidCredential), AgentLifecyclePolicy.Authorize(null!, AgentControlAction.Poll));
    }

    [Fact]
    public void Retiring_identity_can_only_recover_its_scoped_receipt()
    {
        var context = new AgentAuthorityContext(AgentLifecycleState.Active, AgentCredentialState.RetiringReceiptsOnly, true, true);
        Assert.True(AgentLifecyclePolicy.Authorize(context, AgentControlAction.RotationReceipt).Allowed);
        Assert.False(AgentLifecyclePolicy.Authorize(context, AgentControlAction.PairingProof).Allowed);
    }

    [Fact]
    public void Provider_credential_and_assignment_changes_fence_old_context()
    {
        var captured = new AgentVersionContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 3, 4, 5, 6);
        Assert.True(AgentLifecyclePolicy.CheckVersions(captured, captured).Allowed);
        AgentVersionContext[] changed = [
            captured with { AgentId = Guid.NewGuid() }, captured with { ProviderInstanceId = Guid.NewGuid() },
            captured with { CredentialId = Guid.NewGuid() }, captured with { ProviderVersion = 4 },
            captured with { CredentialVersion = 5 }, captured with { AuthorizationVersion = 6 },
            captured with { AssignmentVersion = 7 }, captured with { ProviderVersion = 0 },
        ];
        foreach (var current in changed)
            Assert.Equal(AgentControlError.ContextChanged, AgentLifecyclePolicy.CheckVersions(captured, current).Error);
    }

    [Fact]
    public void Exact_lease_expiry_and_obsolete_session_fences_deny_authority()
    {
        var expiry = DateTimeOffset.Parse("2026-11-07T12:00:00Z");
        var session = new AgentSessionContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 10, expiry);
        Assert.True(AgentLifecyclePolicy.CheckSession(session, session, expiry.AddTicks(-1)).Allowed);
        Assert.False(AgentLifecyclePolicy.CheckSession(session, session, expiry).Allowed);
        Assert.False(AgentLifecyclePolicy.CheckSession(session, session, expiry.AddTicks(1)).Allowed);
        Assert.False(AgentLifecyclePolicy.CheckSession(session, session with { Fence = 11 }, expiry.AddSeconds(-1)).Allowed);
        Assert.False(AgentLifecyclePolicy.CheckSession(session, session with { BootId = Guid.NewGuid() }, expiry.AddSeconds(-1)).Allowed);
        Assert.False(AgentLifecyclePolicy.CheckSession(session, session with { SessionId = Guid.NewGuid() }, expiry.AddSeconds(-1)).Allowed);
    }

    [Fact]
    public void Fence_overflow_never_wraps_or_reuses_authority()
    {
        Assert.Equal(9_223_372_036_854_775_807L, AgentLifecyclePolicy.NextFence(9_223_372_036_854_775_806L));
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentLifecyclePolicy.NextFence(long.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentLifecyclePolicy.NextFence(0));
    }
}
