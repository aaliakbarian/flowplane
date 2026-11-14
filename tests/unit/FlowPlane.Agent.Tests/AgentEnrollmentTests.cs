using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FlowPlane.Agent.Protocol;
using FlowPlane.Agent.Runtime.Control;
using FlowPlane.Agent.Runtime.Transport;

namespace FlowPlane.Agent.Tests;

public sealed class AgentEnrollmentTests
{
    [Fact]
    public async Task Lost_claim_ack_reopens_and_proves_same_key_ids_without_the_code()
    {
        using var fixture = new AgentControlFixture();
        PairingClaimReceipt? committed = null;
        PairingProofRequest? proof = null;
        using var transport = fixture.Transport(async (request, token) =>
        {
            var claim = AgentProtocolCodec.Decode<PairingClaimRequest>(await request.Content!.ReadAsByteArrayAsync(token));
            committed = fixture.Claim(claim.GrantId, claim.ClaimId);
            Assert.Equal(claim.ClaimId, fixture.Store.State.Enrollment!.ClaimId);
            throw new HttpRequestException("lost-claim-ack");
        }, async (request, token) =>
        {
            if (request.Method == HttpMethod.Get) return AgentControlFixture.Reply(committed!);
            proof = AgentProtocolCodec.Decode<PairingProofRequest>(await request.Content!.ReadAsByteArrayAsync(token));
            return AgentControlFixture.Reply(fixture.Status(committed!, PairingStage.Proven));
        });
        var code = new string('A', 43).ToCharArray();
        var coordinator = new AgentEnrollmentCoordinator(fixture.Store, transport, fixture.Certificate);
        await Assert.ThrowsAsync<AgentTransportException>(() => coordinator.BeginAsync("https://localhost/", Guid.NewGuid(), code, TestContext.Current.CancellationToken));
        Assert.All(code, value => Assert.Equal('\0', value));
        var claimId = fixture.Store.State.Enrollment!.ClaimId;
        var proofId = fixture.Store.State.Enrollment.ProofRequestId;
        fixture.Reopen();
        var status = await new AgentEnrollmentCoordinator(fixture.Store, transport, fixture.Certificate).RecoverAsync(TestContext.Current.CancellationToken);
        Assert.Equal(PairingStage.Proven, status.Stage);
        Assert.Equal(claimId, proof!.ClaimId);
        Assert.Equal(proofId, proof.ProofRequestId);
        Assert.Equal(committed, fixture.Store.State.Enrollment!.ClaimReceipt);
        Assert.DoesNotContain("pairingCode", File.ReadAllText(fixture.Path), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(new string('A', 43), File.ReadAllText(fixture.Path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lost_proof_ack_replays_original_proof_and_never_confirms_for_operator()
    {
        using var fixture = new AgentControlFixture();
        PairingClaimReceipt? claim = null;
        PairingProofRequest? first = null;
        var attempts = 0;
        using var transport = fixture.Transport(async (request, token) =>
        {
            var input = AgentProtocolCodec.Decode<PairingClaimRequest>(await request.Content!.ReadAsByteArrayAsync(token));
            claim = fixture.Claim(input.GrantId, input.ClaimId);
            return AgentControlFixture.Reply(claim);
        }, async (request, token) =>
        {
            var input = AgentProtocolCodec.Decode<PairingProofRequest>(await request.Content!.ReadAsByteArrayAsync(token));
            if (attempts++ == 0) { first = input; throw new HttpRequestException("lost-proof"); }
            Assert.Equal(first, input);
            return AgentControlFixture.Reply(fixture.Status(claim!, PairingStage.Proven));
        });
        var coordinator = new AgentEnrollmentCoordinator(fixture.Store, transport, fixture.Certificate);
        await Assert.ThrowsAsync<AgentTransportException>(() => coordinator.BeginAsync("https://localhost/", Guid.NewGuid(), new string('A', 43).ToCharArray(), TestContext.Current.CancellationToken));
        var status = await coordinator.RecoverAsync(TestContext.Current.CancellationToken);
        Assert.Equal(PairingStage.Proven, status.Stage);
        Assert.Equal(PairingStage.Proven, fixture.Store.State.Enrollment!.StatusReceipt!.Stage);
    }

    [Fact]
    public async Task Changed_claim_pin_never_gets_persisted_or_proved()
    {
        using var fixture = new AgentControlFixture();
        using var transport = fixture.Transport(async (request, token) =>
        {
            var claim = AgentProtocolCodec.Decode<PairingClaimRequest>(await request.Content!.ReadAsByteArrayAsync(token));
            return AgentControlFixture.Reply(fixture.Claim(claim.GrantId, claim.ClaimId) with { PinSha256 = new string('b', 64) });
        }, (_, _) => throw new InvalidOperationException("must-not-prove"));
        await Assert.ThrowsAsync<AgentControlException>(() => new AgentEnrollmentCoordinator(fixture.Store, transport, fixture.Certificate).BeginAsync("https://localhost/", Guid.NewGuid(), new string('A', 43).ToCharArray(), TestContext.Current.CancellationToken));
        Assert.Null(fixture.Store.State.Enrollment!.ClaimReceipt);
    }
}

internal sealed class AgentControlFixture : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("flowplane-coordinator-");
    public string Path => System.IO.Path.Combine(directory.FullName, "control.state");
    public Guid Installation { get; } = Guid.NewGuid();
    public X509Certificate2 Certificate { get; } = CreateCertificate();
    public AgentControlStore Store { get; private set; }
    public Guid Agent { get; } = Guid.NewGuid();
    public static readonly DateTimeOffset Now = new(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);
    public AgentControlFixture() => Store = AgentControlStore.Open(Path, Installation, true);
    public void Reopen() { Store.Dispose(); Store = AgentControlStore.Open(Path, Installation); }
    public AgentHttpsTransport Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> bootstrap,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> operations) => new("https://localhost/", new AgentTransportTests.Handler(bootstrap), new AgentTransportTests.Handler(operations));
    public PairingClaimReceipt Claim(Guid grant, Guid claim) => new("flowplane.agent/pairing-claim-receipt.v1", grant, claim, Agent,
        Convert.ToHexStringLower(SHA256.HashData(Certificate.RawData)), AgentPairingSecurity.EncodeToken(Enumerable.Repeat((byte)1, 32).ToArray()), Now.AddHours(1), PairingStage.Claimed, 1);
    public PairingStatusReceipt Status(PairingClaimReceipt claim, PairingStage stage) => new("flowplane.agent/pairing-status.v1", claim.GrantId, claim.ClaimId, stage,
        claim.ExpiresAtUtc, AgentPairingSecurity.VerificationPhrase(claim.GrantId, claim.ClaimId, claim.PinSha256, claim.ServerNonce), stage == PairingStage.Redeemed ? 3 : 2);
    public static HttpResponseMessage Reply<T>(T message) where T : class => new(HttpStatusCode.OK)
    { Content = new ByteArrayContent(AgentProtocolCodec.Encode(message)) { Headers = { ContentType = new("application/json") } } };
    public static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(3072);
        var request = new CertificateRequest("CN=Synthetic Runtime Agent", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(30));
    }
    public void Dispose() { Store.Dispose(); Certificate.Dispose(); directory.Delete(true); }
}
