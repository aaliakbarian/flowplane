namespace FlowPlane.Agent.Protocol;

public sealed record AgentConnectRequest(string Schema, Guid BootId, Guid ConnectRequestId,
    string PackageVersion, int ProtocolVersion, long ExpectedFence, string JournalRecoveryDigest);

public sealed record AgentSessionStatusReceipt(string Schema, Guid AgentId, long Fence,
    DateTimeOffset ServerTimeUtc, DateTimeOffset? LeaseExpiresAtUtc, AgentRecoveryMode RecoveryMode);

public sealed record AgentSessionReceipt(string Schema, Guid AgentId, Guid SessionId, Guid BootId,
    long Fence, long AuthorizationVersion, DateTimeOffset ServerTimeUtc, DateTimeOffset LeaseExpiresAtUtc,
    AgentRecoveryMode RecoveryMode);

public sealed record AgentPollRequest(string Schema, Guid SessionId, long Fence, long Sequence, Guid RequestId,
    AgentAvailability Availability, AgentReadiness Readiness, int UnresolvedEvidenceCount);

public sealed record AgentPollReceipt(string Schema, Guid SessionId, long Fence, long Sequence,
    DateTimeOffset AcknowledgedAtUtc, DateTimeOffset LeaseExpiresAtUtc, int NextPollSeconds, AgentWorkKind Work);
