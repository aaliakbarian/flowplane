using System.Security.Cryptography;
using FlowPlane.Agent.Protocol;
using FlowPlane.Agent.Runtime.Control;
using FlowPlane.Agent.Runtime.Security;
using FlowPlane.Agent.Windows.Security;

namespace FlowPlane.Agent.Windows.Identity;

internal sealed class WindowsAgentRotationIdentity
{
    private readonly string directory;
    private readonly Action verify;
    private readonly Func<Guid, bool, byte[]> createKey;
    private readonly Func<AgentIdentityPublicRecord, WindowsAgentCertificate> loadKey;

    internal WindowsAgentRotationIdentity(string directory) : this(directory,
        () => { new ServiceTokenVerifier().Verify(); WindowsAgentSecurity.VerifyPrivateDirectory(directory); },
        WindowsAgentIdentity.CreateRotationKey, WindowsAgentIdentity.LoadRecord)
    { }

    internal WindowsAgentRotationIdentity(string directory, Action verify, Func<Guid, bool, byte[]> createKey, Func<AgentIdentityPublicRecord, WindowsAgentCertificate> loadKey)
    { this.directory = directory; this.verify = verify; this.createKey = createKey; this.loadKey = loadKey; }

    internal WindowsAgentCertificate Stage(AgentIdentityPublicRecord active, Guid? enrollmentGrant = null)
    {
        try
        {
            verify();
            using var ownership = new FileStream(Path.Combine(directory, "rotation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (enrollmentGrant == Guid.Empty) throw new AgentWindowsException();
            var role = enrollmentGrant is { } grant ? "enrollment-" + grant.ToString("D") : "rotation";
            var pendingPath = Path.Combine(directory, role + ".public");
            var intentPath = Path.Combine(directory, role + ".intent");
            if (File.Exists(pendingPath))
            {
                var pending = Read(pendingPath);
                if (pending.InstallationId != active.InstallationId) throw new AgentWindowsException();
                if (pending.KeyId != active.KeyId || enrollmentGrant is not null) return loadKey(pending);
                // A completed prior rotation is already the active pointer; retain its public receipt slot.
                File.Move(pendingPath, Path.Combine(directory, "rotation.previous.public"), true);
            }
            if (File.Exists(intentPath))
            {
                if (enrollmentGrant is not null) throw new AgentWindowsException();
                var intent = Read(intentPath);
                if (intent.InstallationId != active.InstallationId || intent.KeyId != active.KeyId) throw new AgentWindowsException();
                File.Move(intentPath, Path.Combine(directory, "rotation.previous.intent"), true);
            }
            if (enrollmentGrant is not null && Directory.EnumerateFiles(directory, "enrollment-*.intent").Take(65).Count() >= 64) throw new AgentWindowsException();
            var keyId = Guid.NewGuid();
            var staged = new AgentIdentityPublicRecord(active.InstallationId, keyId, active.Platform, [0]);
            Write(intentPath, staged.Encode(), FileMode.CreateNew);
            // The durable intent precedes CNG. Interrupted key creation is never retried with a second key.
            var der = createKey(keyId, active.Platform);
            if (!AgentCertificateProfile.IsValid(der, DateTimeOffset.UtcNow)) throw new AgentWindowsException();
            staged = staged with { Der = der };
            Write(pendingPath, staged.Encode(), FileMode.CreateNew);
            return loadKey(staged);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        { throw new AgentWindowsException(); }
    }

    internal AgentIdentityPublicRecord SelectEnrollment(AgentIdentityPublicRecord active, AgentControlState state, bool identityLocked = false)
    {
        verify();
        var replacement = state.Replacement ?? throw new AgentWindowsException();
        var staged = Read(Path.Combine(directory, "enrollment-" + replacement.GrantId.ToString("D") + ".public"));
        if (staged.InstallationId != active.InstallationId || staged.InstallationId != state.InstallationId ||
            Convert.ToHexStringLower(SHA256.HashData(staged.Der)) != replacement.PinSha256) throw new AgentWindowsException();
        using var key = loadKey(staged);
        if (state.Enrollment?.GrantId == replacement.GrantId && state.Enrollment.StatusReceipt?.Stage == PairingStage.Redeemed)
        {
            Promote(state, identityLocked, replacement.GrantId);
            return Read(Path.Combine(directory, "identity.public"));
        }
        return staged; // HTTP pairing only. The active identity pointer remains unchanged until operator confirmation.
    }

    internal void Promote(AgentControlState state, bool identityLocked = false, Guid? enrollmentGrant = null)
    {
        FileStream? owner = null;
        try
        {
            verify();
            if (!identityLocked) owner = new FileStream(Path.Combine(directory, "identity.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var rotationOwner = new FileStream(Path.Combine(directory, "rotation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var role = enrollmentGrant is { } grant ? "enrollment-" + grant.ToString("D") : "rotation";
            var staged = Read(Path.Combine(directory, role + ".public"));
            var active = Read(Path.Combine(directory, "identity.public"));
            var pin = Convert.ToHexStringLower(SHA256.HashData(staged.Der));
            if (staged.InstallationId != state.InstallationId || active.InstallationId != state.InstallationId ||
                state.Enrollment?.StatusReceipt?.Stage != PairingStage.Redeemed || state.Enrollment.PinSha256 != pin) throw new AgentWindowsException();
            if (enrollmentGrant is { } enrollment)
            {
                if (state.Replacement?.GrantId != enrollment || state.Replacement.PinSha256 != pin || state.Enrollment.GrantId != enrollment) throw new AgentWindowsException();
            }
            else if (state.Rotation?.StatusReceipt?.Stage != PairingStage.Redeemed || state.Rotation.PinSha256 != pin ||
                state.Rotation.Request.PublicCertificateDerBase64 != Convert.ToBase64String(staged.Der)) throw new AgentWindowsException();
            using var key = loadKey(staged); // Production validates the genuine local CNG key/profile/ACL before selection.
            if (active.KeyId == staged.KeyId && active.Der.AsSpan().SequenceEqual(staged.Der)) return;
            Write(Path.Combine(directory, "identity.previous.public"), active.Encode(), FileMode.Create);
            var temporary = Path.Combine(directory, "identity.rotation-next");
            Write(temporary, staged.Encode(), FileMode.Create);
            File.Move(temporary, Path.Combine(directory, "identity.public"), true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        { throw new AgentWindowsException(); }
        finally { owner?.Dispose(); }
    }

    private static AgentIdentityPublicRecord Read(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 26 or > 16425) throw new AgentWindowsException();
        var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
        return AgentIdentityPublicRecord.Decode(bytes);
    }
    private static void Write(string path, byte[] bytes, FileMode mode)
    {
        using var output = new FileStream(path, mode, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        output.Write(bytes); output.Flush(true);
    }
}
