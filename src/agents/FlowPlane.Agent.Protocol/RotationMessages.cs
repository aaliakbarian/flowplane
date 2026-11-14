namespace FlowPlane.Agent.Protocol;

public sealed record AgentRotationRequest(string Schema, Guid ProviderInstanceId, long ProviderVersion,
    Guid RequestId, Guid ClaimId, string PublicCertificateDerBase64);
