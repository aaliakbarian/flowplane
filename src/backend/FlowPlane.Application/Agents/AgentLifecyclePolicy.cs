namespace FlowPlane.Application.Agents;

/// <summary>Pure decisions only; does not authenticate, acquire a lease, renew, replay or persist anything.</summary>
public static class AgentLifecyclePolicy
{
    public static AgentPolicyDecision Authorize(AgentAuthorityContext context, AgentControlAction action)
    {
        if (context is null || !Enum.IsDefined(context.AgentState) || !Enum.IsDefined(context.CredentialState) || !Enum.IsDefined(action))
            return Deny(AgentControlError.InvalidCredential);
        if (context.AgentState == AgentLifecycleState.Revoked || context.CredentialState == AgentCredentialState.Revoked)
            return Deny(AgentControlError.Revoked);
        if (!context.CertificateWithinValidity || context.CredentialState == AgentCredentialState.Retired)
            return Deny(AgentControlError.InvalidCredential);
        if (!context.IsCurrentAuthority) return Deny(AgentControlError.ContextChanged);
        if (context.AgentState == AgentLifecycleState.Quarantined) return Deny(AgentControlError.ActionRequired);

        var allowed = context.CredentialState switch
        {
            AgentCredentialState.PendingProof => action is AgentControlAction.PairingProof or AgentControlAction.PairingStatus,
            AgentCredentialState.RetiringReceiptsOnly => context.AgentState == AgentLifecycleState.Active && action == AgentControlAction.RotationReceipt,
            AgentCredentialState.Active => context.AgentState == AgentLifecycleState.Active,
            _ => false,
        };
        return allowed ? Allow() : Deny(AgentControlError.OperationNotAllowed);
    }

    public static AgentPolicyDecision CheckVersions(AgentVersionContext expected, AgentVersionContext current) =>
        ValidVersions(current) && expected == current ? Allow() : Deny(AgentControlError.ContextChanged);

    public static AgentPolicyDecision CheckSession(AgentSessionContext expected, AgentSessionContext current, DateTimeOffset databaseNow) =>
        ValidSession(expected) && ValidSession(current) && databaseNow.Offset == TimeSpan.Zero && databaseNow > DateTimeOffset.UnixEpoch &&
        expected.AgentId == current.AgentId && expected.BootId == current.BootId && expected.SessionId == current.SessionId &&
        expected.Fence == current.Fence && current.LeaseExpiresAtUtc > databaseNow ? Allow() : Deny(AgentControlError.ContextChanged);

    public static long NextFence(long current) => current is > 0 and < long.MaxValue ? current + 1 :
        throw new ArgumentOutOfRangeException(nameof(current), "The agent fence cannot advance.");

    private static bool ValidVersions(AgentVersionContext? context) => context is not null &&
        context.AgentId != Guid.Empty && context.ProviderInstanceId != Guid.Empty && context.CredentialId != Guid.Empty &&
        context.ProviderVersion > 0 && context.CredentialVersion > 0 && context.AuthorizationVersion > 0 && context.AssignmentVersion > 0;

    private static bool ValidSession(AgentSessionContext? context) => context is not null && context.AgentId != Guid.Empty &&
        context.BootId != Guid.Empty && context.SessionId != Guid.Empty && context.Fence > 0 &&
        context.LeaseExpiresAtUtc.Offset == TimeSpan.Zero && context.LeaseExpiresAtUtc > DateTimeOffset.UnixEpoch;

    private static AgentPolicyDecision Allow() => new(true, AgentControlError.None);
    private static AgentPolicyDecision Deny(AgentControlError error) => new(false, error);
}
