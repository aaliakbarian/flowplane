using System.Diagnostics;

namespace FlowPlane.Agent.Protocol;

/// <summary>Transient write-only input. Never persist or log this request.</summary>
[DebuggerDisplay("{ToString(),nq}")]
public sealed class PairingClaimRequest(string schema, Guid claimId, Guid grantId,
    string pairingCode, string publicCertificateDerBase64)
{
    public string Schema { get; } = schema;
    public Guid ClaimId { get; } = claimId;
    public Guid GrantId { get; } = grantId;
    public string PairingCode { get; } = pairingCode;
    public string PublicCertificateDerBase64 { get; } = publicCertificateDerBase64;

    public override string ToString() => "PairingClaimRequest(redacted)";
}

public sealed record PairingClaimReceipt(string Schema, Guid GrantId, Guid ClaimId, Guid AgentId,
    string PinSha256, string ServerNonce, DateTimeOffset ExpiresAtUtc, PairingStage Stage, long ReceiptVersion);

public sealed record PairingProofRequest(string Schema, Guid ClaimId, Guid ProofRequestId, string ServerNonce);

public sealed record PairingStatusReceipt(string Schema, Guid GrantId, Guid ClaimId, PairingStage Stage,
    DateTimeOffset ExpiresAtUtc, string VerificationPhrase, long ReceiptVersion);
