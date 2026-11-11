using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

using FlowPlane.Agent.Runtime.Journal;
using FlowPlane.Agent.Windows.Identity;
using FlowPlane.Agent.Windows.Security;

namespace FlowPlane.Agent.Setup;

/// <summary>Fixed local SCM primitives only. No service start, arbitrary executable or credential arguments.</summary>
public sealed class WindowsServiceInstaller
{
    public void Install(string binaryPath)
    {
        WindowsAgentSecurity.RequireAdministrator();
        var quoted = ValidateBinaryPath(binaryPath);
        using var manager = Native.OpenSCManagerW(null, null, 3);
        if (manager.IsInvalid) throw new AgentWindowsException();
        using var existing = Native.OpenServiceW(manager, WindowsAgentSecurity.ServiceName, 1);
        if (!existing.IsInvalid || Marshal.GetLastWin32Error() != 1060) throw new AgentWindowsException();
        // Never overwrite/reuse unexpected prior identity/evidence during installation.
        var data = WindowsAgentSecurity.DataDirectory;
        WindowsAgentSecurity.RejectReparsePoints(data);
        if (Directory.Exists(data) || File.Exists(data)) throw new AgentWindowsException();
        using var service = Native.CreateServiceW(manager, WindowsAgentSecurity.ServiceName, "FlowPlane Agent", 3,
            0x10, 2, 1, quoted, null, IntPtr.Zero, null, WindowsAgentSecurity.ServiceAccount, null);
        if (service.IsInvalid) throw new AgentWindowsException();
        var sidType = 1;
        if (!Native.ChangeServiceConfig2W(service, 5, ref sidType)) throw new AgentWindowsException();
        VerifyConfiguration(service, quoted);
        // Resolve/ACL the virtual account only after SCM has registered that exact service identity.
        WindowsAgentSecurity.CreatePrivateDirectory(data);
        using (var marker = new FileStream(Path.Combine(data, "install.pending"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { marker.Write(Guid.NewGuid().ToByteArray()); marker.Flush(true); }
        // A partial setup is deliberately preserved for review, never hidden by destructive rollback.
    }

    public void Uninstall(string binaryPath)
    {
        WindowsAgentSecurity.RequireAdministrator();
        var quoted = ValidateBinaryPath(binaryPath);
        using var manager = Native.OpenSCManagerW(null, null, 1);
        using var service = Native.OpenServiceW(manager, WindowsAgentSecurity.ServiceName, 0x10005);
        if (manager.IsInvalid || service.IsInvalid) throw new AgentWindowsException();
        VerifyConfiguration(service, quoted);
        if (!Native.QueryServiceStatus(service, out var status) || status.State != 1) throw new AgentWindowsException();
        var facts = WindowsAgentIdentity.ReadPublic(WindowsAgentSecurity.DataDirectory);
        using var journal = AgentOperationJournal.Open(Path.Combine(WindowsAgentSecurity.DataDirectory, "operations.journal"), facts.Installation);
        if (journal.ReadRecoverySummaryAsync().GetAwaiter().GetResult().RequiresReconciliation) throw new AgentWindowsException();
        if (!Native.DeleteService(service)) throw new AgentWindowsException();
        // Preserve all key/certificate/journal files even after a quiescent uninstall. No destructive cleanup API.
    }

    public static string ValidateBinaryPath(string binaryPath)
    {
        if (!Path.IsPathFullyQualified(binaryPath) || binaryPath != Path.GetFullPath(binaryPath) || binaryPath.Contains('"') ||
            Path.GetFileName(binaryPath) != "FlowPlane.Agent.Windows.exe") throw new AgentWindowsException();
        var directory = Path.GetDirectoryName(binaryPath)!;
        var version = Path.GetFileName(directory);
        if (!Version.TryParse(version, out var parsed) || parsed.ToString() != version ||
            !string.Equals(Path.GetDirectoryName(directory), WindowsAgentSecurity.BinaryRoot, StringComparison.OrdinalIgnoreCase)) throw new AgentWindowsException();
        WindowsAgentSecurity.VerifyBinaryDirectory(directory);
        WindowsAgentSecurity.VerifyBinaryDirectory(WindowsAgentSecurity.BinaryRoot);
        if (!File.Exists(binaryPath)) throw new AgentWindowsException();
        return "\"" + binaryPath + "\"";
    }

    private static void VerifyConfiguration(ServiceHandle service, string binaryPath)
    {
        _ = Native.QueryServiceConfigW(service, IntPtr.Zero, 0, out var needed);
        if (Marshal.GetLastWin32Error() != 122 || needed is 0 or > 65536) throw new AgentWindowsException();
        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!Native.QueryServiceConfigW(service, buffer, needed, out _)) throw new AgentWindowsException();
            var config = Marshal.PtrToStructure<ServiceConfig>(buffer);
            if (config.ServiceType != 0x10 || config.StartType != 2 || Marshal.PtrToStringUni(config.BinaryPath) != binaryPath ||
                !string.Equals(Marshal.PtrToStringUni(config.Account), WindowsAgentSecurity.ServiceAccount, StringComparison.OrdinalIgnoreCase)) throw new AgentWindowsException();
            var sid = 0;
            if (!Native.QueryServiceConfig2W(service, 5, ref sid, 4, out _) || sid != 1) throw new AgentWindowsException();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceConfig
    {
        public uint ServiceType, StartType, ErrorControl;
        public IntPtr BinaryPath, LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies, Account, DisplayName;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus { public uint Type, State, Controls, ExitCode, SpecificExitCode, Checkpoint, WaitHint; }
    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => Native.CloseServiceHandle(handle);
    }
    private static class Native
    {
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern ServiceHandle OpenSCManagerW(string? machine, string? database, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern ServiceHandle OpenServiceW(ServiceHandle manager, string name, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern ServiceHandle CreateServiceW(ServiceHandle manager, string name, string displayName, uint access, uint type,
            uint start, uint error, string binaryPath, string? group, IntPtr tag, string? dependencies, string account, string? password);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseServiceHandle(IntPtr handle);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ChangeServiceConfig2W(ServiceHandle service, uint level, ref int info);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryServiceConfigW(ServiceHandle service, IntPtr info, uint size, out uint needed);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryServiceConfig2W(ServiceHandle service, uint level, ref int info, uint size, out uint needed);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryServiceStatus(ServiceHandle service, out ServiceStatus status);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteService(ServiceHandle service);
    }
}
