namespace FlowPlane.Application.Agents;

public enum AgentLifecycleState { Pending, Active, Quarantined, Revoked }
public enum AgentCredentialState { PendingProof, Active, RetiringReceiptsOnly, Retired, Revoked }
public enum AgentControlAction { PairingProof, PairingStatus, Connect, Poll, RotationReceipt }
public enum AgentControlError { None, InvalidCredential, Revoked, ActionRequired, ContextChanged, OperationNotAllowed }

/// <summary>Evidence supplied by future current-DB/TLS authorization, not self-asserted by a client.</summary>
public sealed record AgentAuthorityContext(AgentLifecycleState AgentState, AgentCredentialState CredentialState,
    bool IsCurrentAuthority, bool CertificateWithinValidity);

public sealed record AgentPolicyDecision(bool Allowed, AgentControlError Error);

public sealed record AgentActor(string Issuer, string Subject)
{
    public override string ToString() => "AgentActor(redacted)";
}

public sealed record AgentVersionContext(Guid AgentId, Guid ProviderInstanceId, Guid CredentialId,
    long ProviderVersion, long CredentialVersion, long AuthorizationVersion, long AssignmentVersion);

public sealed record AgentSessionContext(Guid AgentId, Guid BootId, Guid SessionId,
    long Fence, DateTimeOffset LeaseExpiresAtUtc);
