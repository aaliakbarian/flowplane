namespace FlowPlane.Agent.Protocol;

public static class AgentProtocol
{
    public const int Version = 1;
    public const int MaxPayloadBytes = 65_536;
    public const int MaxCertificateDerBytes = 16_384;
    public const int MaxDepth = 8;
}

public enum PairingStage { Issued, Claimed, Proven, Redeemed, Expired, Cancelled }
public enum AgentAvailability { Unknown, Unavailable, Available }
public enum AgentReadiness { Unavailable, ReadyForControlPlane, ActionRequired }
public enum AgentRecoveryMode { None, ActionRequired, ObservationOnly }
public enum AgentWorkKind { None }
public enum AgentAcknowledgementPhase { AgentAccepted, NativeCallAdmitted, MayHaveInvoked, NativeResultKnown, CoreResultCommitted }

/// <summary>Correlation evidence only. It is not a command or a native-call permit.</summary>
public sealed record AgentOperationKey(Guid ContextId, Guid OperationId, int Ordinal, long Fence, string PayloadDigest);

public sealed record AgentOperationAcknowledgement(string Schema, AgentOperationKey Operation,
    AgentAcknowledgementPhase Phase, Guid ReceiptId, DateTimeOffset RecordedAtUtc);

public sealed class AgentProtocolException() : Exception("The agent protocol message is invalid.");
