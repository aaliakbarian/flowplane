using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FlowPlane.Agent.Protocol;
using FlowPlane.Agent.Runtime.Transport;
namespace FlowPlane.Agent.Runtime.Control;
/// <summary>Pairing never confirms the operator's phrase or creates operational authority locally.</summary>
public sealed class AgentEnrollmentCoordinator(AgentControlStore store, AgentHttpsTransport transport, X509Certificate2 certificate)
{
    public async Task<PairingStatusReceipt> BeginAsync(string origin, Guid grantId, char[] code, CancellationToken token)
    {
        try
        {
            if (AgentHttpsTransport.ValidateOrigin(origin) != transport.Origin || grantId == Guid.Empty) throw new AgentControlException();
            var pin = AgentReceiptChecks.Pin(certificate);
            if (store.State.Origin is not null && new Uri(store.State.Origin) != transport.Origin) throw new AgentControlException();
            if (store.State.Enrollment is { } existing)
            {
                var replacement = store.State.Replacement;
                if (existing.PinSha256 != pin && (replacement?.GrantId != grantId || replacement.PinSha256 != pin) ||
                    existing.StatusReceipt?.Stage == PairingStage.Redeemed && existing.GrantId != grantId) throw new AgentControlException();
                if (existing.GrantId == grantId) return await RecoverAsync(token);
            }
            var secret = new string(code);
            var decoded = AgentPairingSecurity.DecodeToken(secret);
            CryptographicOperations.ZeroMemory(decoded);
            var pending = new AgentEnrollmentState(grantId, Guid.NewGuid(), Guid.NewGuid(), pin, null, null);
            store.Save(store.State with { Origin = transport.Origin.AbsoluteUri, Enrollment = pending });
            var claim = await transport.ClaimAsync(new("flowplane.agent/pairing-claim.v1", pending.ClaimId, grantId,
                secret, Convert.ToBase64String(certificate.RawData)), token);
            AgentReceiptChecks.Claim(claim, grantId, pending.ClaimId, pin);
            if (store.State.Replacement?.PreviousEnrollment.ClaimReceipt is { } previous && previous.AgentId != claim.AgentId) throw new AgentControlException();
            store.Save(store.State with { Enrollment = pending with { ClaimReceipt = claim } });
            return await RecoverAsync(token);
        }
        finally { CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(code.AsSpan())); }
    }

    public async Task<PairingStatusReceipt> RecoverAsync(CancellationToken token)
    {
        var pending = store.State.Enrollment ?? throw new AgentControlException();
        if (pending.PinSha256 != AgentReceiptChecks.Pin(certificate) || store.State.Origin is null || new Uri(store.State.Origin) != transport.Origin) throw new AgentControlException();
        var claim = pending.ClaimReceipt;
        if (claim is null)
        {
            // A missing code is never replaced or replayed after restart. Pending-key TLS recovers the immutable nonce.
            claim = await transport.GetClaimReceiptAsync(pending.ClaimId, token);
            AgentReceiptChecks.Claim(claim, pending.GrantId, pending.ClaimId, pending.PinSha256);
            if (store.State.Replacement?.PreviousEnrollment.ClaimReceipt is { } previous && previous.AgentId != claim.AgentId) throw new AgentControlException();
            pending = pending with { ClaimReceipt = claim };
            store.Save(store.State with { Enrollment = pending });
        }
        var status = pending.StatusReceipt is null
            ? await transport.PairingProofAsync(new("flowplane.agent/pairing-proof.v1", pending.ClaimId, pending.ProofRequestId, claim.ServerNonce), token)
            : await transport.PairingStatusAsync(pending.ClaimId, token);
        if (status.Stage is PairingStage.Expired or PairingStage.Cancelled) throw new AgentEnrollmentRejectedException();
        AgentReceiptChecks.Status(status, claim, pending.StatusReceipt);
        store.Save(store.State with { Enrollment = pending with { StatusReceipt = status } });
        return status;
    }
}

internal static class AgentReceiptChecks
{
    internal static string Pin(X509Certificate2 certificate)
    {
        if (!certificate.HasPrivateKey || !AgentPairingSecurity.IsClientCertificate(certificate.RawData, DateTimeOffset.UtcNow)) throw new AgentControlException();
        return Convert.ToHexStringLower(SHA256.HashData(certificate.RawData));
    }
    internal static void Claim(PairingClaimReceipt claim, Guid grant, Guid id, string pin)
    {
        if (claim.GrantId != grant || claim.ClaimId != id || claim.PinSha256 != pin || claim.Stage != PairingStage.Claimed) throw new AgentControlException();
    }
    internal static void Status(PairingStatusReceipt status, PairingClaimReceipt claim, PairingStatusReceipt? previous)
    {
        if (status.GrantId != claim.GrantId || status.ClaimId != claim.ClaimId || status.ExpiresAtUtc != claim.ExpiresAtUtc ||
            status.VerificationPhrase != AgentPairingSecurity.VerificationPhrase(claim.GrantId, claim.ClaimId, claim.PinSha256, claim.ServerNonce) ||
            status.Stage < PairingStage.Proven || status.Stage is PairingStage.Expired or PairingStage.Cancelled || status.ReceiptVersion < claim.ReceiptVersion ||
            previous is not null && (status.ReceiptVersion < previous.ReceiptVersion || status.Stage < previous.Stage || status.ReceiptVersion == previous.ReceiptVersion && status != previous))
            throw new AgentControlException();
    }
}
