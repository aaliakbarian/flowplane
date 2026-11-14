using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using FlowPlane.Api.Agents;
using FlowPlane.Application.Agents;

namespace FlowPlane.Api.Tests;

public sealed class AgentBoundaryTests
{
    [Fact]
    public void Agent_listener_explicitly_allows_only_TLS12_and_TLS13()
    {
        using var key = RSA.Create(3072);
        using var certificate = Certificate(key, "server", client: false);
        Assert.Equal(System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
            AgentControlHosting.AgentHttpsOptions(certificate, new AgentPinRegistry()).SslProtocols);
    }
    [Fact]
    public void Enrollment_admission_is_globally_bounded_without_an_unbounded_per_IP_cache()
    {
        var limits = new AgentControlLimits();
        Assert.Equal(10, Enumerable.Range(0, 50).Count(_ => limits.TryEnrollment()));
    }
    [Fact]
    public void Disabled_agent_boundary_does_not_require_certificates_or_schema()
        => new AgentControlOptions().Validate();

    [Theory]
    [InlineData(8080, 8080, "/tmp/server.pfx")]
    [InlineData(8080, 0, "/tmp/server.pfx")]
    [InlineData(8080, 8443, null)]
    [InlineData(8080, 8443, "relative.pfx")]
    public void Enabled_listener_requires_separate_port_and_explicit_certificate(int browser, int agent, string? path)
        => Assert.Throws<InvalidOperationException>(() => new AgentControlOptions
        { Enabled = true, BrowserPort = browser, AgentPort = agent, ServerCertificatePath = path }.Validate());

    [Fact]
    public void Exact_DER_pin_not_subject_or_same_public_key_is_required_and_revocation_replaces_snapshot()
    {
        using var key = RSA.Create(3072);
        using var certificate = Certificate(key, "same-subject");
        using var other = Certificate(key, "same-subject");
        var pins = new AgentPinRegistry();
        Assert.False(pins.Accepts(certificate, DateTimeOffset.UtcNow));
        pins.Replace([new(Guid.NewGuid(), Guid.NewGuid(), certificate.RawData)]);
        Assert.True(pins.Accepts(certificate, DateTimeOffset.UtcNow));
        Assert.False(pins.Accepts(other, DateTimeOffset.UtcNow));
        pins.Replace([]);
        Assert.False(pins.Accepts(certificate, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Invalid_pinned_profile_or_expired_certificate_never_authenticates()
    {
        using var key = RSA.Create(3072);
        using var certificate = Certificate(key, "client", client: false);
        var pins = new AgentPinRegistry();
        pins.Replace([new(Guid.NewGuid(), Guid.NewGuid(), certificate.RawData)]);
        Assert.False(pins.Accepts(certificate, DateTimeOffset.UtcNow));
        using var valid = Certificate(key, "client");
        pins.Replace([new(Guid.NewGuid(), Guid.NewGuid(), valid.RawData)]);
        Assert.False(pins.Accepts(valid, new DateTimeOffset(valid.NotAfter.ToUniversalTime())));
    }

    internal static X509Certificate2 Certificate(RSA key, string name, bool client = true)
    {
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new(client ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1") }, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
    }
}
