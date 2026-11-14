using System.Security.Cryptography;
using FlowPlane.Agent.Protocol;
using FlowPlane.Agent.Runtime.Control;
using FlowPlane.Agent.Runtime.Journal;
using FlowPlane.Agent.Runtime.Transport;

namespace FlowPlane.Agent.Tests;

public sealed class AgentControlRotationTests
{
    [Theory]
    [InlineData("claim")]
    [InlineData("proof")]
    [InlineData("no-arrival")]
    [InlineData("begin-only")]
    [InlineData("activation")]
    public async Task Lost_rotation_ack_keeps_same_pending_key_and_request_ids(string lost)
    {
        using var fixture = new AgentControlFixture();
        AgentPollingTests.PreparePaired(fixture);
        using var journal = AgentOperationJournal.Open(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(fixture.Path)!, "operations.journal"), fixture.Installation, true);
        using var pendingCertificate = AgentControlFixture.CreateCertificate();
        PairingClaimReceipt? claim = null;
        AgentRotationRequest? original = null;
        PairingProofRequest? originalProof = null;
        var proofs = 0;
        var requests = 0;
        var grantId = Guid.NewGuid();
        using var oldTransport = fixture.Transport((_, _) => throw new InvalidOperationException(), async (request, token) =>
        {
            var input = AgentProtocolCodec.Decode<AgentRotationRequest>(await request.Content!.ReadAsByteArrayAsync(token));
            Assert.Equal(input, fixture.Store.State.Rotation!.Request);
            if (original is not null) Assert.Equal(original, input);
            original = input;
            if (++requests == 1 && lost is "no-arrival" or "begin-only") throw new HttpRequestException("reservation-not-established");
            claim = fixture.Claim(grantId, input.ClaimId) with { PinSha256 = Convert.ToHexStringLower(SHA256.HashData(pendingCertificate.RawData)) };
            if (lost == "claim") throw new HttpRequestException("lost-rotation-claim");
            return AgentControlFixture.Reply(claim);
        });
        using var newTransport = fixture.Transport((_, _) => throw new InvalidOperationException(), async (request, token) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                if (claim is null) throw new HttpRequestException("unknown-pending-pin");
                return AgentControlFixture.Reply(claim);
            }
            var input = AgentProtocolCodec.Decode<PairingProofRequest>(await request.Content!.ReadAsByteArrayAsync(token));
            if (proofs++ == 0) originalProof = input;
            else Assert.Equal(originalProof, input);
            if (lost is "proof" or "activation" && proofs == 1) throw new HttpRequestException("lost-rotation-proof-or-activation");
            return AgentControlFixture.Reply(fixture.Status(claim!, PairingStage.Redeemed));
        });
        var coordinator = new AgentRotationCoordinator(fixture.Store, oldTransport, newTransport, pendingCertificate, journal);
        await Assert.ThrowsAsync<AgentTransportException>(() => coordinator.BeginAsync(Guid.NewGuid(), 1, TestContext.Current.CancellationToken));
        var requestId = fixture.Store.State.Rotation!.Request.RequestId;
        fixture.Reopen();
        var receipt = await new AgentRotationCoordinator(fixture.Store, oldTransport, newTransport, pendingCertificate, journal).RecoverAsync(TestContext.Current.CancellationToken);
        Assert.Equal(PairingStage.Redeemed, receipt.Stage);
        Assert.Equal(requestId, original!.RequestId);
        Assert.Equal(original, fixture.Store.State.Rotation!.Request);
        Assert.Equal(claim!.PinSha256, fixture.Store.State.Enrollment!.PinSha256);
    }
}
