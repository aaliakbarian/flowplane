using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using FlowPlane.Agent.Windows.Security;
using FlowPlane.Agent.Runtime.Security;
using FlowPlane.Agent.Runtime.Control;

namespace FlowPlane.Agent.Windows.Identity;

public interface IWindowsAgentIdentity
{
    ValueTask<WindowsAgentCertificate> LoadOrCreateAsync(bool explicitInstall, CancellationToken cancellationToken);
}

public sealed class WindowsAgentCertificate(Guid installationId, X509Certificate2 certificate) : IDisposable
{
    public Guid InstallationId { get; } = installationId;
    public X509Certificate2 Certificate { get; } = certificate;
    public override string ToString() => "WindowsAgentCertificate(protected)";
    public void Dispose() => Certificate.Dispose();
}

public sealed class WindowsAgentIdentity(string directory) : IWindowsAgentIdentity
{
    private const string PlatformProvider = "Microsoft Platform Crypto Provider";
    private const string SoftwareProvider = "Microsoft Software Key Storage Provider";
    private string IdentityFile => Path.Combine(directory, "identity.public");

    public ValueTask<WindowsAgentCertificate> LoadOrCreateAsync(bool explicitInstall, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            new ServiceTokenVerifier().Verify();
            WindowsAgentSecurity.VerifyPrivateDirectory(directory);
            using var ownership = new FileStream(Path.Combine(directory, "identity.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            WindowsAgentSecurity.RejectReparsePoints(IdentityFile);
            if (explicitInstall && File.Exists(IdentityFile)) throw new AgentWindowsException();
            return ValueTask.FromResult(File.Exists(IdentityFile) ? Load() : explicitInstall ? Create() : throw new AgentWindowsException());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or AgentControlException or SystemException && exception is not OperationCanceledException)
        { throw new AgentWindowsException(); }
    }

    private WindowsAgentCertificate Create()
    {
        var marker = Path.Combine(directory, "install.pending");
        WindowsAgentSecurity.RejectReparsePoints(marker);
        var markerBytes = File.ReadAllBytes(marker);
        if (markerBytes.Length != 16) throw new AgentWindowsException();
        var installation = new Guid(markerBytes);
        if (installation == Guid.Empty || File.Exists(Path.Combine(directory, "operations.journal"))) throw new AgentWindowsException();
        var name = "FlowPlaneAgent-" + installation.ToString("D");
        var hasPlatform = PlatformAvailable();
        if (CngKey.Exists(name, new(SoftwareProvider), CngKeyOpenOptions.MachineKey) || hasPlatform && CngKey.Exists(name, new(PlatformProvider), CngKeyOpenOptions.MachineKey))
            throw new AgentWindowsException(); // Interrupted install: preserve, never create a replacement key.
        var provider = hasPlatform ? PlatformProvider : SoftwareProvider;
        var parameters = new CngKeyCreationParameters
        {
            Provider = new(provider),
            KeyCreationOptions = CngKeyCreationOptions.MachineKey,
            ExportPolicy = CngExportPolicies.None,
            KeyUsage = CngKeyUsages.Signing,
        };
        parameters.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(3072), CngPropertyOptions.None));
        parameters.Parameters.Add(new CngProperty("Security Descr", WindowsAgentSecurity.KeyDescriptor(WindowsAgentSecurity.ServiceSid), (CngPropertyOptions)4));
        using var key = CngKey.Create(CngAlgorithm.Rsa, name, parameters);
        using var rsa = new RSACng(key);
        var request = new CertificateRequest("CN=FlowPlane Agent", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(90));
        var der = certificate.Export(X509ContentType.Cert); // Public DER only; never export a private key/PFX.
        using (var output = new FileStream(IdentityFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        using (var writer = new BinaryWriter(output, Encoding.UTF8, true))
        { writer.Write(1); writer.Write(installation.ToByteArray()); writer.Write(provider == PlatformProvider); writer.Write(der.Length); writer.Write(der); writer.Flush(); output.Flush(true); }
        return Load();
    }

    private WindowsAgentCertificate Load()
    {
        var facts = ReadRecord(directory);
        var controlPath = Path.Combine(directory, "control.state");
        if (File.Exists(controlPath) || File.Exists(controlPath + ".expected"))
        {
            using var control = AgentControlStore.Open(controlPath, facts.InstallationId);
            if (control.State.Replacement is { } replacement &&
                (control.State.Enrollment?.StatusReceipt?.Stage != FlowPlane.Agent.Protocol.PairingStage.Redeemed ||
                 replacement.PinSha256 == control.State.Enrollment.PinSha256))
                facts = new WindowsAgentRotationIdentity(directory).SelectEnrollment(facts, control.State, identityLocked: true);
            else if (control.State.Enrollment is { } enrollment && enrollment.PinSha256 != Convert.ToHexStringLower(SHA256.HashData(facts.Der)))
            {
                new WindowsAgentRotationIdentity(directory).Promote(control.State, identityLocked: true);
                facts = ReadRecord(directory);
            }
        }
        return LoadRecord(facts);
    }

    internal static WindowsAgentCertificate LoadRecord(AgentIdentityPublicRecord facts)
    {
        if (!AgentCertificateProfile.IsValid(facts.Der, DateTimeOffset.UtcNow)) throw new AgentWindowsException();
        using var key = CngKey.Open("FlowPlaneAgent-" + facts.KeyId.ToString("D"), new(facts.Platform ? PlatformProvider : SoftwareProvider), CngKeyOpenOptions.MachineKey);
        if (!key.IsMachineKey || key.ExportPolicy != CngExportPolicies.None || key.KeyUsage != CngKeyUsages.Signing ||
            !WindowsAgentSecurity.IsExactKeyDescriptor(key.GetProperty("Security Descr", (CngPropertyOptions)4).GetValue(), WindowsAgentSecurity.ServiceSid))
            throw new AgentWindowsException();
        using var rsa = new RSACng(key);
        using var certificate = X509CertificateLoader.LoadCertificate(facts.Der);
        using var publicKey = certificate.GetRSAPublicKey();
        if (rsa.KeySize != 3072 || publicKey is null || !rsa.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(publicKey.ExportSubjectPublicKeyInfo()) ||
            DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() || DateTime.UtcNow >= certificate.NotAfter.ToUniversalTime() ||
            certificate.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault() is not { CertificateAuthority: false } ||
            certificate.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault()?.KeyUsages != X509KeyUsageFlags.DigitalSignature ||
            certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault() is not { } eku ||
            eku.EnhancedKeyUsages.Count != 1 || eku.EnhancedKeyUsages[0].Value != "1.3.6.1.5.5.7.3.2") throw new AgentWindowsException();
        var challenge = RandomNumberGenerator.GetBytes(32);
        if (!publicKey.VerifyData(challenge, rsa.SignData(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new AgentWindowsException();
        return new(facts.InstallationId, certificate.CopyWithPrivateKey(rsa));
    }

    public static (Guid Installation, bool Platform, byte[] Der) ReadPublic(string directory)
    {
        var record = ReadRecord(directory);
        return (record.InstallationId, record.Platform, record.Der);
    }

    internal static AgentIdentityPublicRecord ReadRecord(string directory)
    {
        WindowsAgentSecurity.VerifyPrivateDirectory(directory);
        var path = Path.Combine(directory, "identity.public");
        WindowsAgentSecurity.RejectReparsePoints(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 26 or > 16425) throw new AgentWindowsException();
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        return AgentIdentityPublicRecord.Decode(bytes);
    }

    internal static byte[] CreateRotationKey(Guid keyId, bool platform)
    {
        var provider = platform ? PlatformProvider : SoftwareProvider;
        var name = "FlowPlaneAgent-" + keyId.ToString("D");
        if (keyId == Guid.Empty || CngKey.Exists(name, new(provider), CngKeyOpenOptions.MachineKey)) throw new AgentWindowsException();
        var parameters = new CngKeyCreationParameters
        {
            Provider = new(provider),
            KeyCreationOptions = CngKeyCreationOptions.MachineKey,
            ExportPolicy = CngExportPolicies.None,
            KeyUsage = CngKeyUsages.Signing,
        };
        parameters.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(3072), CngPropertyOptions.None));
        parameters.Parameters.Add(new CngProperty("Security Descr", WindowsAgentSecurity.KeyDescriptor(WindowsAgentSecurity.ServiceSid), (CngPropertyOptions)4));
        using var key = CngKey.Create(CngAlgorithm.Rsa, name, parameters);
        using var rsa = new RSACng(key);
        var request = new CertificateRequest("CN=FlowPlane Agent", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(90));
        return certificate.Export(X509ContentType.Cert);
    }

    private static bool PlatformAvailable()
    {
        var result = NativeKeys.NCryptOpenStorageProvider(out var handle, PlatformProvider, 0);
        if (result == 0) { NativeKeys.NCryptFreeObject(handle); return true; }
        // No silent fallback after a TPM key-creation failure or access denial.
        if (result is unchecked((int)0x80090029) or unchecked((int)0x80090017)) return false;
        throw new AgentWindowsException();
    }

    private static class NativeKeys
    {
        [System.Runtime.InteropServices.DllImport("ncrypt.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        internal static extern int NCryptOpenStorageProvider(out IntPtr provider, string name, int flags);
        [System.Runtime.InteropServices.DllImport("ncrypt.dll")]
        internal static extern int NCryptFreeObject(IntPtr handle);
    }
}
