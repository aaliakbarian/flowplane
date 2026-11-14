using FlowPlane.Agent.Protocol;

namespace FlowPlane.Agent.Runtime.Control;

public sealed class AgentControlException() : Exception("Local agent control evidence requires operator review.");
public sealed class AgentEnrollmentRejectedException() : Exception("An explicit fresh pairing grant is required.");

public sealed record AgentEnrollmentState(Guid GrantId, Guid ClaimId, Guid ProofRequestId, string PinSha256,
    PairingClaimReceipt? ClaimReceipt, PairingStatusReceipt? StatusReceipt);

public sealed record AgentRotationState(AgentRotationRequest Request, Guid ProofRequestId, string PinSha256,
    PairingClaimReceipt? ClaimReceipt, PairingStatusReceipt? StatusReceipt);
public sealed record AgentEnrollmentReplacementState(Guid GrantId, string PinSha256, AgentEnrollmentState PreviousEnrollment);

/// <summary>Non-secret control evidence only. Never accepts a code, private key or executable payload.</summary>
public sealed record AgentControlState(string Schema, Guid InstallationId, string? Origin,
    AgentEnrollmentState? Enrollment, AgentConnectRequest? ConnectRequest, AgentSessionReceipt? ConnectReceipt,
    AgentPollRequest? PendingPoll, AgentPollReceipt? LatestPoll, AgentRotationState? Rotation,
    AgentEnrollmentReplacementState? Replacement = null);
