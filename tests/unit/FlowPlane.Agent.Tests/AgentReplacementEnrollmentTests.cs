using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FlowPlane.Agent.Protocol;
using FlowPlane.Agent.Runtime.Control;
using FlowPlane.Agent.Runtime.Journal;
using FlowPlane.Agent.Runtime.Setup;

namespace FlowPlane.Agent.Tests;

public sealed class AgentReplacementEnrollmentTests
{
    [Theory]
    [InlineData("claim")]
    [InlineData("proof")]
    public async Task Replacement_ack_loss_reopens_durable_same_key_claim_and_proof_ids(string lost)
    {
        using var fixture = new AgentControlFixture();
        using var staged = AgentControlFixture.CreateCertificate();
        var old = fixture.Claim(Guid.NewGuid(), Guid.NewGuid());
        fixture.Store.Save(fixture.Store.State with { Origin = "https://localhost/", Enrollment = new(old.GrantId, old.ClaimId, Guid.NewGuid(), old.PinSha256, old, fixture.Status(old, PairingStage.Proven)) });
        fixture.Store.Dispose();
        using var journal = AgentOperationJournal.Open(Path.Combine(Path.GetDirectoryName(fixture.Path)!, "operations.journal"), fixture.Installation, true);
        var grant = Guid.NewGuid();
        PairingClaimReceipt? claim = null;
        PairingProofRequest? proof = null;
        var fail = true;
        var stages = 0;
        FlowPlane.Agent.Runtime.Transport.AgentHttpsTransport Transport(string _, X509Certificate2 certificate) => fixture.Transport(async (request, token) =>
        {
            var input = AgentProtocolCodec.Decode<PairingClaimRequest>(await request.Content!.ReadAsByteArrayAsync(token));
            Assert.Equal(staged.RawData, certificate.RawData);
            claim = fixture.Claim(input.GrantId, input.ClaimId) with { PinSha256 = Convert.ToHexStringLower(SHA256.HashData(staged.RawData)) };
            if (lost == "claim" && fail) { fail = false; throw new HttpRequestException("lost-replacement-claim-ACK"); }
            return AgentControlFixture.Reply(claim);
        }, async (request, token) =>
        {
            Assert.Equal(staged.RawData, certificate.RawData);
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/claim", StringComparison.Ordinal)) return AgentControlFixture.Reply(claim!);
            if (request.Method == HttpMethod.Post)
            {
                var input = AgentProtocolCodec.Decode<PairingProofRequest>(await request.Content!.ReadAsByteArrayAsync(token));
                if (proof is not null) Assert.Equal(proof, input);
                proof = input;
                if (lost == "proof" && fail) { fail = false; throw new HttpRequestException("lost-replacement-proof-ACK"); }
            }
            return AgentControlFixture.Reply(fixture.Status(claim!, PairingStage.Proven));
        });
        Guid claimId;
        Guid proofId;
        using (var host = new AgentControlHost(fixture.Path, fixture.Installation, fixture.Certificate, journal, Transport, _ => { stages++; return Clone(staged); }))
        {
            Assert.Equal(LocalSetupStatus.PairingPending, (await host.PairAsync(new(LocalSetupAction.Pair, "https://localhost/", new string('A', 43), grant), TestContext.Current.CancellationToken)).Status);
            claimId = host.State!.Enrollment!.ClaimId;
            proofId = host.State.Enrollment.ProofRequestId;
        }
        using var reopened = new AgentControlHost(fixture.Path, fixture.Installation, staged, journal, Transport, _ => throw new InvalidOperationException("must-not-replace-key"));
        await reopened.StepAsync(TestContext.Current.CancellationToken);
        Assert.Equal(PairingStage.Proven, reopened.State!.Enrollment!.StatusReceipt!.Stage);
        Assert.Equal(claimId, reopened.State.Enrollment.ClaimId);
        Assert.Equal(proofId, reopened.State.Enrollment.ProofRequestId);
        Assert.Equal(old, reopened.State.Replacement!.PreviousEnrollment.ClaimReceipt);
        Assert.Equal(1, stages);
    }
    [Theory]
    [InlineData("claimed")]
    [InlineData("proven")]
    [InlineData("wrong-code")]
    public Task Explicit_fresh_grant_uses_a_fresh_key_and_requires_operator_confirmation(string previous)
        => Replace(previous, false);

    [Fact]
    public Task Redeemed_replacement_history_survives_rotation_reopen_and_polling()
        => Replace("proven", true);

    private static async Task Replace(string previous, bool rotate)
    {
        using var fixture = new AgentControlFixture();
        using var staged = AgentControlFixture.CreateCertificate();
        using var rotated = AgentControlFixture.CreateCertificate();
        var old = fixture.Claim(Guid.NewGuid(), Guid.NewGuid());
        if (previous != "wrong-code") fixture.Store.Save(fixture.Store.State with { Origin = "https://localhost/", Enrollment = new(old.GrantId, old.ClaimId, Guid.NewGuid(), old.PinSha256, old, previous == "proven" ? fixture.Status(old, PairingStage.Proven) : null) });
        fixture.Store.Dispose();
        using var journal = AgentOperationJournal.Open(Path.Combine(Path.GetDirectoryName(fixture.Path)!, "operations.journal"), fixture.Installation, true);
        var grant = Guid.NewGuid();
        var stages = 0;
        var confirmed = false;
        var polls = 0;
        var sessionId = Guid.NewGuid();
        PairingClaimReceipt? claim = null;
        FlowPlane.Agent.Runtime.Transport.AgentHttpsTransport Transport(string _, X509Certificate2 certificate) => fixture.Transport(async (request, token) =>
        {
            var input = AgentProtocolCodec.Decode<PairingClaimRequest>(await request.Content!.ReadAsByteArrayAsync(token));
            if (input.GrantId != grant) return new HttpResponseMessage(HttpStatusCode.Forbidden);
            // F3 keeps the old DER tombstoned. A fresh grant must not reuse it.
            if (input.PublicCertificateDerBase64 == Convert.ToBase64String(fixture.Certificate.RawData)) return new HttpResponseMessage(HttpStatusCode.Conflict);
            Assert.Equal(Convert.ToBase64String(staged.RawData), input.PublicCertificateDerBase64);
            claim = fixture.Claim(input.GrantId, input.ClaimId) with { PinSha256 = Convert.ToHexStringLower(SHA256.HashData(certificate.RawData)) };
            return AgentControlFixture.Reply(claim);
        }, async (request, token) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/assignments", StringComparison.Ordinal)) return AgentControlFixture.Reply(new AgentAssignmentsReceipt("flowplane.agent/assignments.v1", fixture.Agent, [new(Guid.NewGuid(), 1)]));
            if (path.EndsWith("/rotations", StringComparison.Ordinal))
            {
                Assert.Equal(staged.RawData, certificate.RawData);
                var rotation = AgentProtocolCodec.Decode<AgentRotationRequest>(await request.Content!.ReadAsByteArrayAsync(token));
                claim = fixture.Claim(Guid.NewGuid(), rotation.ClaimId) with { PinSha256 = Convert.ToHexStringLower(SHA256.HashData(rotated.RawData)) };
                return AgentControlFixture.Reply(claim);
            }
            if (path.Contains("/rotations/", StringComparison.Ordinal))
            { Assert.Equal(rotated.RawData, certificate.RawData); return AgentControlFixture.Reply(fixture.Status(claim!, PairingStage.Redeemed)); }
            if (path.EndsWith("/session", StringComparison.Ordinal)) return AgentControlFixture.Reply(new AgentSessionStatusReceipt("flowplane.agent/session-status.v1", fixture.Agent, 1, DateTimeOffset.UtcNow, null, AgentRecoveryMode.None));
            if (path.EndsWith("/sessions", StringComparison.Ordinal))
            {
                var connect = AgentProtocolCodec.Decode<AgentConnectRequest>(await request.Content!.ReadAsByteArrayAsync(token));
                var now = DateTimeOffset.UtcNow;
                return AgentControlFixture.Reply(new AgentSessionReceipt("flowplane.agent/session-receipt.v1", fixture.Agent, sessionId, connect.BootId, 2, 1, now, now.AddSeconds(60), AgentRecoveryMode.None));
            }
            if (path.EndsWith("/polls", StringComparison.Ordinal))
            {
                Assert.Equal(rotated.RawData, certificate.RawData);
                var poll = AgentProtocolCodec.Decode<AgentPollRequest>(await request.Content!.ReadAsByteArrayAsync(token));
                polls++;
                var now = DateTimeOffset.UtcNow;
                return AgentControlFixture.Reply(new AgentPollReceipt("flowplane.agent/poll-receipt.v1", sessionId, 2, poll.Sequence, now, now.AddSeconds(60), 5, AgentWorkKind.None));
            }
            Assert.Equal(staged.RawData, certificate.RawData);
            return AgentControlFixture.Reply(fixture.Status(claim!, confirmed ? PairingStage.Redeemed : PairingStage.Proven));
        });
        using var host = new AgentControlHost(fixture.Path, fixture.Installation, fixture.Certificate, journal, Transport, _ => { stages++; return Clone(staged); });
        if (previous == "wrong-code") Assert.Equal(LocalSetupStatus.ActionRequired,
            (await host.PairAsync(new(LocalSetupAction.Pair, "https://localhost/", new string('A', 43), Guid.NewGuid()), TestContext.Current.CancellationToken)).Status);
        var receipt = await host.PairAsync(new(LocalSetupAction.Pair, "https://localhost/", new string('A', 43), grant), TestContext.Current.CancellationToken);
        Assert.Equal(LocalSetupStatus.PairingPending, receipt.Status);
        Assert.Equal(PairingStage.Proven, receipt.PairingStage);
        Assert.Equal(1, stages);
        await host.StepAsync(TestContext.Current.CancellationToken);
        Assert.Equal(LocalSetupStatus.PairingPending, (await host.StatusAsync(TestContext.Current.CancellationToken)).Status);
        // The authenticated original-operator/ETag confirmation boundary has separate HTTP tests.
        confirmed = true;
        await host.StepAsync(TestContext.Current.CancellationToken);
        Assert.Equal(LocalSetupStatus.Paired, (await host.StatusAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Equal(0, (await journal.ReadRecoverySummaryAsync(TestContext.Current.CancellationToken)).UnresolvedCount);
        if (!rotate) return;
        Assert.Equal(PairingStage.Redeemed, (await host.RotateAsync(rotated, TestContext.Current.CancellationToken)).Stage);
        Assert.Equal(old, host.State!.Replacement!.PreviousEnrollment.ClaimReceipt);
        host.Dispose();
        using var reopened = new AgentControlHost(fixture.Path, fixture.Installation, rotated, journal, Transport);
        await reopened.StepAsync(TestContext.Current.CancellationToken);
        await reopened.StepAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, polls);
        Assert.Equal(1, reopened.State!.LatestPoll!.Sequence);
        Assert.Equal(old, reopened.State.Replacement!.PreviousEnrollment.ClaimReceipt);
        Assert.Equal(LocalSetupStatus.Paired, (await reopened.StatusAsync(TestContext.Current.CancellationToken)).Status);
    }

    [Theory]
    [InlineData("journal")]
    [InlineData("store")]
    public async Task Fresh_grant_does_not_reset_missing_or_unsettled_evidence(string failure)
    {
        using var fixture = new AgentControlFixture();
        var old = fixture.Claim(Guid.NewGuid(), Guid.NewGuid());
        fixture.Store.Save(fixture.Store.State with { Origin = "https://localhost/", Enrollment = new(old.GrantId, old.ClaimId, Guid.NewGuid(), old.PinSha256, old, fixture.Status(old, PairingStage.Proven)) });
        fixture.Store.Dispose();
        using var journal = AgentOperationJournal.Open(Path.Combine(Path.GetDirectoryName(fixture.Path)!, "operations.journal"), fixture.Installation, true);
        if (failure == "journal") await journal.AppendAcceptedAsync(new(Guid.NewGuid(), Guid.NewGuid(), 1, 1, new string('a', 64)), TestContext.Current.CancellationToken);
        using var host = new AgentControlHost(fixture.Path, fixture.Installation, fixture.Certificate, journal,
            (_, _) => throw new InvalidOperationException("must-not-transmit"), _ => throw new InvalidOperationException("must-not-allocate-key"));
        if (failure == "store") Directory.CreateDirectory(fixture.Path + ".next");
        Assert.Equal(LocalSetupStatus.ActionRequired, (await host.PairAsync(new(LocalSetupAction.Pair, "https://localhost/", new string('A', 43), Guid.NewGuid()), TestContext.Current.CancellationToken)).Status);
    }

    internal static X509Certificate2 Clone(X509Certificate2 certificate)
    {
        using var publicCertificate = X509CertificateLoader.LoadCertificate(certificate.RawData);
        using var key = certificate.GetRSAPrivateKey()!;
        return publicCertificate.CopyWithPrivateKey(key);
    }
}
