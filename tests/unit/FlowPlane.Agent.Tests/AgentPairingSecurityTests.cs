using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using FlowPlane.Agent.Protocol;

namespace FlowPlane.Agent.Tests;

public sealed class AgentPairingSecurityTests
{
    [Fact]
    public void Genuine_client_profile_is_accepted_and_signature_substitution_is_rejected()
    {
        using var key = RSA.Create(3072);
        using var certificate = Client(key);
        var bytes = certificate.RawData;
        Assert.True(AgentPairingSecurity.IsClientCertificate(bytes, DateTimeOffset.UtcNow));
        bytes[^1] ^= 1;
        Assert.False(AgentPairingSecurity.IsClientCertificate(bytes, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("missing-eku")]
    [InlineData("server-eku")]
    [InlineData("ca")]
    [InlineData("weak-key")]
    [InlineData("expired")]
    public void Invalid_client_profiles_never_become_pending_pins(string defect)
    {
        using var key = RSA.Create(defect == "weak-key" ? 2048 : 3072);
        using var certificate = Client(key, defect);
        Assert.False(AgentPairingSecurity.IsClientCertificate(certificate.RawData, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Phrase_is_bound_to_exact_grant_claim_nonce_and_DER_pin()
    {
        var grant = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var claim = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var phrase = AgentPairingSecurity.VerificationPhrase(grant, claim, new string('a', 64), new string('A', 43));
        Assert.Matches("^[a-f0-9]{4}(-[a-f0-9]{4}){3}$", phrase);
        Assert.Equal(phrase, AgentPairingSecurity.VerificationPhrase(grant, claim, new string('a', 64), new string('A', 43)));
        Assert.NotEqual(phrase, AgentPairingSecurity.VerificationPhrase(claim, grant, new string('a', 64), new string('A', 43)));
        Assert.NotEqual(phrase, AgentPairingSecurity.VerificationPhrase(grant, claim, new string('b', 64), new string('A', 43)));
        Assert.NotEqual(phrase, AgentPairingSecurity.VerificationPhrase(grant, claim, new string('a', 64), "AQ" + new string('A', 41)));
        Assert.Throws<AgentProtocolException>(() => AgentPairingSecurity.VerificationPhrase(Guid.Empty, claim, new string('a', 64), new string('A', 43)));
    }

    [Fact]
    public void Only_a_locally_settled_journal_can_emit_the_declared_settled_digest()
    {
        var settled = AgentPairingSecurity.RecoveryDigest(0, false);
        Assert.Matches("^[a-f0-9]{64}$", settled);
        Assert.NotEqual(settled, AgentPairingSecurity.RecoveryDigest(1, true));
        Assert.NotEqual(settled, AgentPairingSecurity.RecoveryDigest(0, true));
        Assert.Throws<AgentProtocolException>(() => AgentPairingSecurity.RecoveryDigest(-1, false));
        Assert.Throws<AgentProtocolException>(() => AgentPairingSecurity.RecoveryDigest(4097, true));
    }

    private static X509Certificate2 Client(RSA key, string? defect = null)
    {
        var request = new CertificateRequest("CN=Synthetic Agent", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(defect == "ca", false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        if (defect != "missing-eku") request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new(defect == "server-eku" ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2") }, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), defect == "expired" ? DateTimeOffset.UtcNow.AddDays(-1) : DateTimeOffset.UtcNow.AddDays(1));
    }
}
