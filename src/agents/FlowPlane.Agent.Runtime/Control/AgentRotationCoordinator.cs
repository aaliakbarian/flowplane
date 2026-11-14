using System.Security.Cryptography.X509Certificates;
using FlowPlane.Agent.Protocol;
using FlowPlane.Agent.Runtime.Transport;
using FlowPlane.Agent.Runtime.Journal;
namespace FlowPlane.Agent.Runtime.Control;

/// <summary>Uses a pre-staged local key only. Transport ambiguity cannot allocate a replacement key.</summary>
public sealed class AgentRotationCoordinator(AgentControlStore store, AgentHttpsTransport oldTransport, AgentHttpsTransport pendingTransport,
    X509Certificate2 pendingCertificate, IAgentOperationJournal journal)
{
    public async Task<PairingStatusReceipt> BeginAsync(Guid providerInstanceId, long providerVersion, CancellationToken token)
    {
        await RequireSettled(token);
        if (store.State.Rotation is not null) return await RecoverAsync(token);
        var enrollment = store.State.Enrollment ?? throw new AgentControlException();
        var pin = AgentReceiptChecks.Pin(pendingCertificate);
        if (enrollment.StatusReceipt?.Stage != PairingStage.Redeemed || pin == enrollment.PinSha256 ||
            providerInstanceId == Guid.Empty || providerVersion <= 0 || store.State.Origin is null ||
            new Uri(store.State.Origin) != oldTransport.Origin || oldTransport.Origin != pendingTransport.Origin) throw new AgentControlException();
        var rotation = new AgentRotationState(new("flowplane.agent/rotation.v1", providerInstanceId, providerVersion,
            Guid.NewGuid(), Guid.NewGuid(), Convert.ToBase64String(pendingCertificate.RawData)), Guid.NewGuid(), pin, null, null);
        store.Save(store.State with { Rotation = rotation });
        var claim = await oldTransport.RotateAsync(rotation.Request, token);
        AgentReceiptChecks.Claim(claim, claim.GrantId, rotation.Request.ClaimId, pin);
        if (claim.AgentId != enrollment.ClaimReceipt!.AgentId) throw new AgentControlException();
        store.Save(store.State with { Rotation = rotation with { ClaimReceipt = claim } });
        return await RecoverAsync(token);
    }

    public async Task<PairingStatusReceipt> RecoverAsync(CancellationToken token)
    {
        await RequireSettled(token);
        var rotation = store.State.Rotation ?? throw new AgentControlException();
        if (AgentReceiptChecks.Pin(pendingCertificate) != rotation.PinSha256 || Convert.ToBase64String(pendingCertificate.RawData) != rotation.Request.PublicCertificateDerBase64 ||
            store.State.Origin is null || new Uri(store.State.Origin) != pendingTransport.Origin) throw new AgentControlException();
        var claim = rotation.ClaimReceipt;
        if (claim is null)
        {
            try { claim = await pendingTransport.GetClaimReceiptAsync(rotation.Request.ClaimId, token); }
            catch (AgentTransportException exception) when (exception.Code is AgentTransportError.Unavailable or AgentTransportError.Denied or AgentTransportError.Rejected)
            {
                // No arrival or BeginRotation-only commit leaves no pending TLS pin. Reconcile the same
                // durable idempotent request through the old active key; never allocate another key/identity.
                claim = await oldTransport.RotateAsync(rotation.Request, token);
            }
            AgentReceiptChecks.Claim(claim, claim.GrantId, rotation.Request.ClaimId, rotation.PinSha256);
            if (claim.AgentId != store.State.Enrollment?.ClaimReceipt?.AgentId) throw new AgentControlException();
            rotation = rotation with { ClaimReceipt = claim };
            store.Save(store.State with { Rotation = rotation });
        }
        var status = rotation.StatusReceipt is null
            ? await pendingTransport.RotationProofAsync(claim.GrantId, new("flowplane.agent/pairing-proof.v1", claim.ClaimId, rotation.ProofRequestId, claim.ServerNonce), token)
            : await pendingTransport.RotationStatusAsync(claim.GrantId, token);
        AgentReceiptChecks.Status(status, claim, rotation.StatusReceipt);
        if (status.Stage != PairingStage.Redeemed) throw new AgentControlException();
        // Evidence is committed before the Windows host switches the active key pointer.
        store.Save(store.State with
        {
            Rotation = rotation with { StatusReceipt = status },
            Enrollment = new(claim.GrantId, claim.ClaimId, rotation.ProofRequestId, rotation.PinSha256, claim, status),
        });
        return status;
    }

    private async Task RequireSettled(CancellationToken token)
    {
        var summary = await journal.ReadRecoverySummaryAsync(token);
        if (summary.UnresolvedCount != 0 || summary.RequiresReconciliation) throw new AgentControlException();
    }
}
