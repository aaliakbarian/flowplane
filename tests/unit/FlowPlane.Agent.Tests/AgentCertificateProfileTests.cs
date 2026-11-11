using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using FlowPlane.Agent.Runtime.Security;

namespace FlowPlane.Agent.Tests;

public sealed class AgentCertificateProfileTests
{
    [Fact]
    public void Genuine_self_signed_profile_is_accepted_but_altered_signature_is_rejected()
    {
        using var key = RSA.Create(3072);
        using var certificate = Certificate(key, HashAlgorithmName.SHA256, clientEku: true);
        var der = certificate.RawData;
        Assert.True(AgentCertificateProfile.IsValid(der, DateTimeOffset.UtcNow));
        der[^1] ^= 1;
        Assert.False(AgentCertificateProfile.IsValid(der, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(2048, true, false)]
    [InlineData(3072, false, false)]
    [InlineData(3072, true, true)]
    public void Wrong_key_size_missing_client_eku_or_wrong_signature_hash_fails_closed(int bits, bool eku, bool sha384)
    {
        using var key = RSA.Create(bits);
        using var certificate = Certificate(key, sha384 ? HashAlgorithmName.SHA384 : HashAlgorithmName.SHA256, eku);
        Assert.False(AgentCertificateProfile.IsValid(certificate.RawData, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Malformed_expired_and_exact_expiry_certificates_are_rejected()
    {
        using var key = RSA.Create(3072);
        using var certificate = Certificate(key, HashAlgorithmName.SHA256, true);
        Assert.False(AgentCertificateProfile.IsValid(new byte[] { 1, 2, 3 }, DateTimeOffset.UtcNow));
        Assert.False(AgentCertificateProfile.IsValid(certificate.RawData, new DateTimeOffset(certificate.NotAfter.ToUniversalTime())));
        Assert.False(AgentCertificateProfile.IsValid(certificate.RawData, DateTimeOffset.UtcNow.AddDays(10)));
    }

    private static X509Certificate2 Certificate(RSA key, HashAlgorithmName hash, bool clientEku)
    {
        var request = new CertificateRequest("CN=Synthetic Agent", key, hash, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        if (clientEku) request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
