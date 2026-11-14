namespace FlowPlane.Application.Agents;

// Read models contain public identity facts only. They never authorize a mutation on their own.
public sealed record AgentPublicPin(Guid AgentId, Guid CredentialId, byte[] CertificateDer);
public sealed record AgentCredentialAuthority(AgentCertificateContext Certificate, AgentLifecycleState AgentState,
    AgentCredentialState CredentialState, long AgentVersion, long AuthorizationVersion, long Fence,
    DateTimeOffset NotBeforeUtc, DateTimeOffset NotAfterUtc, DateTimeOffset? RetiringUntilUtc,
    bool ContextCurrent, bool RecoveryRequired, DateTimeOffset DatabaseTimeUtc);
public sealed record AgentGrantView(AgentGrantReceipt Receipt, AgentActor? Actor, Guid? InitiatingCredentialId,
    byte[]? CertificateDer, Guid ProviderInstanceId, long ProviderVersion, long? ClaimReceiptVersion = null);
public sealed record AgentHealthView(Guid AgentId, string DisplayName, AgentLifecycleState State, long Version,
    long AuthorizationVersion, long Fence, string? PackageVersion, int? ProtocolVersion, AgentReadiness Readiness,
    DateTimeOffset? LastContactAtUtc, DateTimeOffset? LastHeartbeatAtUtc, DateTimeOffset? LeaseExpiresAtUtc,
    DateTimeOffset DatabaseTimeUtc, bool AuthorityCurrent, bool RecoveryRequired, AgentProviderContext[] Providers);

public interface IAgentControlReader
{
    Task<IReadOnlyList<AgentPublicPin>> ReadPinsAsync(CancellationToken token = default);
    Task<AgentCredentialAuthority?> ReadCredentialAsync(ReadOnlyMemory<byte> exactCertificateDer, CancellationToken token = default);
    Task<AgentGrantView?> ReadGrantAsync(Guid grantId, CancellationToken token = default);
    Task<AgentGrantView?> FindClaimAsync(Guid claimId, Guid credentialId, CancellationToken token = default);
    Task<AgentHealthView?> ReadHealthAsync(Guid agentId, CancellationToken token = default);
    Task<bool> MatchesIssuedCodeAsync(Guid grantId, ReadOnlyMemory<byte> tokenHash, CancellationToken token = default);
}
