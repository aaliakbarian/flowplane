namespace FlowPlane.Application.Agents;

public enum AgentGrantKind { Enrollment, Rotation }
public enum AgentGrantState { Issued, Claimed, Proven, Redeemed, Expired, Cancelled }
public enum AgentAssignmentState { Pending, Active, Suspended, Revoked }
public enum AgentReadiness { Unknown, ReadyForControlPlane, Unavailable, ActionRequired }
public enum AgentStoreError
{
    InvalidRequest, NotFound, ContextChanged, Conflict, Expired, Revoked, InvalidCredential, PinCollision, Capacity,
    ActionRequired, SessionAlreadyActive, ConflictingIncarnation, StaleReceipt, Unavailable, CommitUncertain
}
public sealed class AgentStoreException(AgentStoreError code) : Exception("The agent lifecycle operation could not be completed safely.")
{
    public AgentStoreError Code { get; } = code;
}

// These contexts are supplied by authenticated/resource-authorized callers, never by trusting HTTP bodies.
// F4 owns TLS possession/profile validation. The store independently checks exact persisted DER and versions.
public sealed record AgentCertificateContext(Guid AgentId, Guid CredentialId, ReadOnlyMemory<byte> CertificateDer);
public sealed record AgentProviderContext(Guid ProviderInstanceId, long ProviderVersion);
public sealed record IssueAgentGrant(AgentProviderContext Provider, AgentActor Actor, Guid ClientRequestId,
    string DisplayName, ReadOnlyMemory<byte> TokenHash, Guid? ExistingAgentId = null, long? ExpectedAgentVersion = null);
public sealed record ClaimAgentGrant(Guid GrantId, Guid ClaimId, ReadOnlyMemory<byte> TokenHash, ReadOnlyMemory<byte> CertificateDer,
    AgentCertificateContext? Initiator = null)
{
    public override string ToString() => "ClaimAgentGrant(redacted)";
}
public sealed record ProveAgentGrant(Guid GrantId, Guid ClaimId, Guid ProofRequestId, ReadOnlyMemory<byte> Nonce, AgentCertificateContext Certificate);
public sealed record ConfirmAgentGrant(Guid GrantId, AgentActor Actor, long ExpectedGrantVersion, ReadOnlyMemory<byte> Nonce);
public sealed record RotateAgentCredential(AgentProviderContext Provider, AgentCertificateContext Certificate,
    long ExpectedAuthorizationVersion, Guid ClientRequestId);
public sealed record RevokeAgent(Guid AgentId, AgentActor Actor, long ExpectedVersion);
public sealed record ConnectAgent(AgentCertificateContext Certificate, Guid BootId, Guid RequestId, long ExpectedFence,
    long ExpectedAuthorizationVersion, int ProtocolVersion, string PackageVersion, bool JournalReady, int UnresolvedCount);
public sealed record PollAgent(AgentCertificateContext Certificate, Guid SessionId, long Fence, long AuthorizationVersion,
    Guid RequestId, long Sequence, AgentReadiness Readiness, bool JournalReady, int UnresolvedCount);

// Code hashes and public certificate bodies are deliberately absent from all receipts.
public sealed record AgentGrantReceipt(Guid GrantId, Guid AgentId, Guid ProviderInstanceId, AgentGrantState State,
    long Version, DateTimeOffset RecordedAtUtc, DateTimeOffset ExpiresAtUtc, Guid? ClaimId, Guid? CredentialId, byte[]? Nonce,
    long? ActivatedAuthorizationVersion);
public sealed record AgentSessionReceipt(Guid AgentId, Guid SessionId, Guid BootId, long Fence, long AuthorizationVersion,
    DateTimeOffset AcceptedAtUtc, DateTimeOffset LeaseExpiresAtUtc);
public sealed record AgentSessionStatus(Guid AgentId, Guid SessionId, Guid BootId, long Fence, long AuthorizationVersion,
    long LeaseAuthorizationVersion, DateTimeOffset LeaseExpiresAtUtc, DateTimeOffset DatabaseTimeUtc,
    bool AuthorityCurrent, bool RecoveryRequired);
public sealed record AgentPollReceipt(Guid AgentId, Guid SessionId, long Fence, long AuthorizationVersion, Guid RequestId,
    long Sequence, DateTimeOffset AcknowledgedAtUtc, DateTimeOffset LeaseExpiresAtUtc, int NextPollSeconds);
public sealed record AgentConnectOutcome(AgentStoreError? Error, AgentSessionReceipt? Receipt);
public sealed record AgentPollOutcome(AgentStoreError? Error, AgentPollReceipt? Receipt);

/// <summary>Persistence only. No transport, native-call admission, dispatch, key generation or SQL credentials.</summary>
public interface IAgentControlStore
{
    Task<AgentGrantReceipt> IssueGrantAsync(IssueAgentGrant request, CancellationToken token = default);
    Task<AgentGrantReceipt> ClaimAsync(ClaimAgentGrant request, CancellationToken token = default);
    Task<AgentGrantReceipt> RecordProofAsync(ProveAgentGrant request, CancellationToken token = default);
    Task<AgentGrantReceipt> ConfirmAsync(ConfirmAgentGrant request, CancellationToken token = default);
    Task RevokeAsync(RevokeAgent request, CancellationToken token = default);
    Task<AgentConnectOutcome> ConnectAsync(ConnectAgent request, CancellationToken token = default);
    Task<AgentPollOutcome> PollAsync(PollAgent request, CancellationToken token = default);
    Task<AgentGrantReceipt> BeginRotationAsync(RotateAgentCredential request, CancellationToken token = default);
    Task<AgentGrantReceipt> ActivateRotationAsync(ProveAgentGrant request, CancellationToken token = default);
    Task<AgentSessionStatus?> ReadSessionAsync(AgentCertificateContext certificate, CancellationToken token = default);
    Task<AgentGrantReceipt> ReadGrantAsync(Guid grantId, AgentCertificateContext certificate, CancellationToken token = default);
    Task<int> CleanupAsync(Guid agentId, CancellationToken token = default);
}
