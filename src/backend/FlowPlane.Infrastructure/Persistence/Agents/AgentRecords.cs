using FlowPlane.Application.Agents;

namespace FlowPlane.Infrastructure.Persistence.Agents;

internal sealed record AgentRecord
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = "";
    public AgentLifecycleState State { get; set; }
    public long Version { get; set; } = 1;
    public long AuthorizationVersion { get; set; } = 1;
    public long SessionFence { get; set; } = 1;
    public Guid? CurrentBootId { get; set; }
    public Guid? ConnectRequestId { get; set; }
    public Guid? SessionId { get; set; }
    public byte[]? ConnectDigest { get; set; }
    public DateTimeOffset? ConnectAcceptedAtUtc { get; set; }
    public DateTimeOffset? ConnectLeaseExpiresAtUtc { get; set; }
    public long? ConnectAuthorizationVersion { get; set; }
    public long? ConnectFence { get; set; }
    public long? LeaseAuthorizationVersion { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
    public Guid? PollRequestId { get; set; }
    public byte[]? PollDigest { get; set; }
    public long PollSequence { get; set; }
    public DateTimeOffset? PollAcknowledgedAtUtc { get; set; }
    public DateTimeOffset? PollLeaseExpiresAtUtc { get; set; }
    public long? PollFence { get; set; }
    public long? PollAuthorizationVersion { get; set; }
    public int? NextPollSeconds { get; set; }
    public int? ProtocolVersion { get; set; }
    public string? PackageVersion { get; set; }
    public AgentReadiness Readiness { get; set; }
    public bool RecoveryRequired { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? LastContactAtUtc { get; set; }
    public DateTimeOffset? LastHeartbeatAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public string? RevokedByIssuer { get; set; }
    public string? RevokedBySubject { get; set; }
}

internal sealed record AgentCredentialRecord
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public byte[] Der { get; set; } = [];
    public byte[] DerSha256 { get; set; } = [];
    public byte[] SpkiSha256 { get; set; } = [];
    public AgentCredentialState State { get; set; }
    public long Version { get; set; } = 1;
    public DateTimeOffset NotBeforeUtc { get; set; }
    public DateTimeOffset NotAfterUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ProvedAtUtc { get; set; }
    public DateTimeOffset? ActivatedAtUtc { get; set; }
    public DateTimeOffset? RetiringUntilUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
}

internal sealed record AgentAssignmentRecord
{
    public Guid ProviderInstanceId { get; set; }
    public Guid AgentId { get; set; }
    public AgentAssignmentState State { get; set; }
    public long Version { get; set; } = 1;
    public long ProviderVersion { get; set; }
    public long? ProviderCredentialVersion { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

internal sealed record AgentGrantRecord
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public Guid ProviderInstanceId { get; set; }
    public AgentGrantKind Kind { get; set; }
    public AgentGrantState State { get; set; }
    public long Version { get; set; } = 1;
    public long ExpectedAgentVersion { get; set; }
    public long ExpectedAuthorizationVersion { get; set; }
    public long ExpectedAssignmentVersion { get; set; }
    public long ProviderVersion { get; set; }
    public long? ProviderCredentialVersion { get; set; }
    public string? Issuer { get; set; }
    public string? Subject { get; set; }
    public byte[]? ActorDigest { get; set; }
    public Guid ClientRequestId { get; set; }
    public byte[] RequestDigest { get; set; } = [];
    public byte[]? TokenHash { get; set; }
    public Guid? InitiatingCredentialId { get; set; }
    public Guid? ExpectedActiveCredentialId { get; set; }
    public Guid? ClaimId { get; set; }
    public byte[]? ClaimDigest { get; set; }
    public Guid? ClaimedCredentialId { get; set; }
    public byte[]? Nonce { get; set; }
    public long? ClaimReceiptVersion { get; set; }
    public Guid? ProofRequestId { get; set; }
    public long? ProofReceiptVersion { get; set; }
    public long? RedemptionReceiptVersion { get; set; }
    public long? ActivatedAuthorizationVersion { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? ClaimedAtUtc { get; set; }
    public DateTimeOffset? ProvedAtUtc { get; set; }
    public DateTimeOffset? RedeemedAtUtc { get; set; }
}

internal enum AgentSecurityEventKind { GrantIssued, ClaimBound, PossessionProved, Activated, RotationIssued, Rotated, Revoked, ProviderChanged, SessionAcquired, ConflictingIncarnation, RecoveryRequired }
internal sealed record AgentSecurityEventRecord
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public Guid? CredentialId { get; set; }
    public Guid? GrantId { get; set; }
    public AgentSecurityEventKind Kind { get; set; }
    public string? Issuer { get; set; }
    public string? Subject { get; set; }
    public long AuthorizationVersion { get; set; }
    public long Fence { get; set; }
    public long PreviousAuthorizationVersion { get; set; }
    public long PreviousFence { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
}
