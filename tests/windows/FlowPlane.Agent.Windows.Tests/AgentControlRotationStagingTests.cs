using FlowPlane.Agent.Windows.Identity;
using FlowPlane.Agent.Windows.Security;
using FlowPlane.Agent.Runtime.Control;
using FlowPlane.Agent.Protocol;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FlowPlane.Agent.Windows.Tests;

public sealed class AgentControlRotationStagingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "PortableBoundary")]
    public void Replacement_restart_selects_pending_key_but_promotes_only_after_confirmed_redemption(bool redeemed)
    {
        var directory = Directory.CreateTempSubdirectory("flowplane-enrollment-promote-");
        using var certificate = Certificate();
        try
        {
            var active = new AgentIdentityPublicRecord(Guid.NewGuid(), Guid.NewGuid(), false, [1]);
            var grant = Guid.NewGuid();
            var claim = new PairingClaimReceipt("flowplane.agent/pairing-claim-receipt.v1", grant, Guid.NewGuid(), Guid.NewGuid(),
                Convert.ToHexStringLower(SHA256.HashData(certificate.RawData)), new string('A', 43), DateTimeOffset.UtcNow.AddMinutes(10), PairingStage.Claimed, 1);
            var status = new PairingStatusReceipt("flowplane.agent/pairing-status.v1", grant, claim.ClaimId, redeemed ? PairingStage.Redeemed : PairingStage.Proven,
                claim.ExpiresAtUtc, AgentPairingSecurity.VerificationPhrase(grant, claim.ClaimId, claim.PinSha256, claim.ServerNonce), 3);
            var previous = new AgentEnrollmentState(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new string('a', 64), null, null);
            var state = new AgentControlState("flowplane.agent/control-state.v1", active.InstallationId, "https://localhost/",
                new(grant, claim.ClaimId, Guid.NewGuid(), claim.PinSha256, claim, status), null, null, null, null, null,
                new(grant, claim.PinSha256, previous));
            var pending = active with { KeyId = Guid.NewGuid(), Der = certificate.RawData };
            File.WriteAllBytes(Path.Combine(directory.FullName, "identity.public"), active.Encode());
            File.WriteAllBytes(Path.Combine(directory.FullName, "enrollment-" + grant.ToString("D") + ".public"), pending.Encode());
            File.WriteAllBytes(Path.Combine(directory.FullName, "operations.journal"), [7, 8, 9]);
            var identity = new WindowsAgentRotationIdentity(directory.FullName, () => { }, (_, _) => throw new InvalidOperationException("must-not-allocate"),
                record => new(record.InstallationId, X509CertificateLoader.LoadCertificate(record.Der)));
            Assert.Equal(pending.KeyId, identity.SelectEnrollment(active, state).KeyId);
            Assert.Equal(redeemed ? pending.KeyId : active.KeyId, AgentIdentityPublicRecord.Decode(File.ReadAllBytes(Path.Combine(directory.FullName, "identity.public"))).KeyId);
            if (redeemed) Assert.Equal(active.KeyId, AgentIdentityPublicRecord.Decode(File.ReadAllBytes(Path.Combine(directory.FullName, "identity.previous.public"))).KeyId);
            Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(Path.Combine(directory.FullName, "operations.journal")));
        }
        finally { directory.Delete(true); }
    }
    [Fact]
    [Trait("Category", "PortableBoundary")]
    public void Explicit_enrollment_grants_stage_distinct_keys_and_reopen_the_same_grant_key()
    {
        var directory = Directory.CreateTempSubdirectory("flowplane-reissue-");
        using var certificate = Certificate();
        try
        {
            var active = new AgentIdentityPublicRecord(Guid.NewGuid(), Guid.NewGuid(), false, [1]);
            var calls = 0;
            var identity = new WindowsAgentRotationIdentity(directory.FullName, () => { }, (_, _) => { calls++; return certificate.RawData; },
                record => new(record.InstallationId, X509CertificateLoader.LoadCertificate(record.Der)));
            var first = Guid.NewGuid();
            using (identity.Stage(active, first)) { }
            using (identity.Stage(active, first)) { }
            Assert.Equal(1, calls);
            using (identity.Stage(active, Guid.NewGuid())) { }
            Assert.Equal(2, calls);
        }
        finally { directory.Delete(true); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "PortableBoundary")]
    public void Promotion_requires_durable_activation_and_preserves_previous_pointer_and_operation_evidence(bool committed)
    {
        var directory = Directory.CreateTempSubdirectory("flowplane-promote-");
        using var certificate = Certificate();
        try
        {
            var installation = Guid.NewGuid();
            var current = new AgentIdentityPublicRecord(installation, installation, false, [1]);
            var pending = new AgentIdentityPublicRecord(installation, Guid.NewGuid(), false, certificate.RawData);
            File.WriteAllBytes(Path.Combine(directory.FullName, "identity.public"), current.Encode());
            File.WriteAllBytes(Path.Combine(directory.FullName, "rotation.public"), pending.Encode());
            File.WriteAllBytes(Path.Combine(directory.FullName, "operations.journal"), [6, 5, 4]);
            var claim = new PairingClaimReceipt("flowplane.agent/pairing-claim-receipt.v1", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                Convert.ToHexStringLower(SHA256.HashData(certificate.RawData)), new string('A', 43), DateTimeOffset.UtcNow.AddMinutes(10), PairingStage.Claimed, 1);
            var status = new PairingStatusReceipt("flowplane.agent/pairing-status.v1", claim.GrantId, claim.ClaimId, PairingStage.Redeemed,
                claim.ExpiresAtUtc, AgentPairingSecurity.VerificationPhrase(claim.GrantId, claim.ClaimId, claim.PinSha256, claim.ServerNonce), 3);
            var state = new AgentControlState("flowplane.agent/control-state.v1", installation, "https://localhost/",
                new(claim.GrantId, claim.ClaimId, Guid.NewGuid(), claim.PinSha256, claim, status), null, null, null, null,
                new(new("flowplane.agent/rotation.v1", Guid.NewGuid(), 1, Guid.NewGuid(), claim.ClaimId, Convert.ToBase64String(certificate.RawData)), Guid.NewGuid(), claim.PinSha256, claim, committed ? status : null));
            var identity = new WindowsAgentRotationIdentity(directory.FullName, () => { }, (_, _) => throw new InvalidOperationException("must-not-create-key"),
                record => new(record.InstallationId, X509CertificateLoader.LoadCertificate(record.Der)));
            if (committed)
            {
                identity.Promote(state);
                Assert.Equal(pending.KeyId, AgentIdentityPublicRecord.Decode(File.ReadAllBytes(Path.Combine(directory.FullName, "identity.public"))).KeyId);
                Assert.Equal(current.KeyId, AgentIdentityPublicRecord.Decode(File.ReadAllBytes(Path.Combine(directory.FullName, "identity.previous.public"))).KeyId);
            }
            else
            {
                Assert.Throws<AgentWindowsException>(() => identity.Promote(state));
                Assert.Equal(current.KeyId, AgentIdentityPublicRecord.Decode(File.ReadAllBytes(Path.Combine(directory.FullName, "identity.public"))).KeyId);
            }
            Assert.Equal(new byte[] { 6, 5, 4 }, File.ReadAllBytes(Path.Combine(directory.FullName, "operations.journal")));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    [Trait("Category", "PortableBoundary")]
    public void Interrupted_key_creation_keeps_intent_and_never_allocates_another_key()
    {
        var directory = Directory.CreateTempSubdirectory("flowplane-staged-");
        try
        {
            var active = new AgentIdentityPublicRecord(Guid.NewGuid(), Guid.NewGuid(), false, [1]);
            var calls = 0;
            var identity = new WindowsAgentRotationIdentity(directory.FullName, () => { }, (_, _) => { calls++; throw new IOException("simulated-CNG-or-disk-failure"); }, _ => throw new InvalidOperationException());
            Assert.Throws<AgentWindowsException>(() => identity.Stage(active));
            Assert.True(File.Exists(Path.Combine(directory.FullName, "rotation.intent")));
            Assert.Throws<AgentWindowsException>(() => identity.Stage(active));
            Assert.Equal(1, calls);
        }
        finally { directory.Delete(true); }
    }

    private static X509Certificate2 Certificate()
    {
        using var key = RSA.Create(3072);
        var request = new CertificateRequest("CN=Synthetic Staged Agent", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(90));
    }
}
