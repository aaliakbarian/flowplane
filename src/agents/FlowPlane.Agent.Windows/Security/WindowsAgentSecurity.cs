using System.Security.AccessControl;
using System.Security.Principal;

namespace FlowPlane.Agent.Windows.Security;

public sealed class AgentWindowsException() : Exception("The agent Windows security or local evidence check failed.");

public static class WindowsAgentSecurity
{
    public const string ServiceName = "FlowPlaneAgent";
    public const string ServiceAccount = @"NT SERVICE\FlowPlaneAgent";
    public static SecurityIdentifier ServiceSid => (SecurityIdentifier)new NTAccount(ServiceAccount).Translate(typeof(SecurityIdentifier));
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FlowPlaneAgent");
    public static string BinaryRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FlowPlaneAgent");

    public static byte[] KeyDescriptor(SecurityIdentifier serviceSid)
    {
        var descriptor = new RawSecurityDescriptor($"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGX;;;{serviceSid.Value})");
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        return bytes;
    }

    public static bool IsExactKeyDescriptor(byte[]? bytes, SecurityIdentifier serviceSid)
    {
        if (bytes is null) return false;
        var descriptor = new RawSecurityDescriptor(bytes, 0);
        var acl = descriptor.DiscretionaryAcl;
        if (!descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected) || acl?.Count != 3) return false;
        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        { ["S-1-5-18"] = 0x10000000, ["S-1-5-32-544"] = 0x10000000, [serviceSid.Value] = unchecked((int)0xA0000000) };
        foreach (GenericAce ace in acl)
            if (ace is not CommonAce { AceQualifier: AceQualifier.AccessAllowed, AceFlags: AceFlags.None } allowed ||
                !expected.Remove(allowed.SecurityIdentifier.Value, out var mask) || mask != allowed.AccessMask) return false;
        return expected.Count == 0;
    }

    public static void RequireAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem && !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new AgentWindowsException();
    }

    public static void RejectReparsePoints(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new AgentWindowsException();
    }

    public static void VerifyPrivateDirectory(string path)
    {
        RejectReparsePoints(path);
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (!security.AreAccessRulesProtected) throw new AgentWindowsException();
        VerifyRules(security, data: true);
        foreach (var file in Directory.EnumerateFiles(path))
        {
            RejectReparsePoints(file);
            VerifyRules(new FileInfo(file).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner), data: true);
        }
    }

    public static void VerifyBinaryDirectory(string path)
    {
        RejectReparsePoints(path);
        VerifyRules(new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner), data: false);
        foreach (var file in Directory.EnumerateFiles(path))
        {
            RejectReparsePoints(file);
            VerifyRules(new FileInfo(file).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner), data: false);
        }
    }

    private static void VerifyRules(FileSystemSecurity security, bool data)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier))?.Value;
        if (owner is not ("S-1-5-18" or "S-1-5-32-544") && (!data || owner != ServiceSid.Value)) throw new AgentWindowsException();
        var allowedData = new HashSet<string>(StringComparer.Ordinal) { "S-1-5-18", "S-1-5-32-544" };
        if (data) allowedData.Add(ServiceSid.Value);
        const FileSystemRights writes = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            var sid = rule.IdentityReference.Value;
            if (data && !allowedData.Contains(sid) || !data && sid is not ("S-1-5-18" or "S-1-5-32-544") && (rule.FileSystemRights & writes) != 0)
                throw new AgentWindowsException();
        }
    }

    public static void CreatePrivateDirectory(string path)
    {
        RequireAdministrator();
        RejectReparsePoints(path);
        if (Directory.Exists(path)) throw new AgentWindowsException();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), ServiceSid })
            security.AddAccessRule(new FileSystemAccessRule(sid, sid == ServiceSid ? FileSystemRights.Modify : FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
        VerifyPrivateDirectory(path);
    }
}
