using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

using Microsoft.Win32.SafeHandles;

namespace FlowPlane.Providers.Ssis.Discovery;

internal static class SsisManifestFile
{
    public static FileStream Open() => Open(Path.GetTempPath());

    internal static FileStream Open(string directory)
    {
        if (OperatingSystem.IsLinux()) return OpenLinux(directory);
        if (OperatingSystem.IsWindows()) return OpenWindows(directory);
        throw new PlatformNotSupportedException("Secure discovery temporary storage is unavailable on this platform.");
    }

    [SupportedOSPlatform("linux")]
    private static FileStream OpenLinux(string directory)
    {
        // O_TMPFILE | O_EXCL creates an inode with no pathname and prevents later linking.
        // There is no create/unlink window, and process termination closes the last handle.
        // These Linux flag values apply to the supported x64/arm64 runtimes.
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException("Secure discovery temporary storage is unavailable on this architecture.");
        const int flags = 0x410000 | 0x80 | 0x2 | 0x80000; // O_TMPFILE, O_EXCL, O_RDWR, O_CLOEXEC
        var handle = OpenAnonymous(directory, flags, 0x180); // mode 0600, subject to umask
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new IOException("Anonymous discovery temporary storage is unavailable; use a filesystem supporting O_TMPFILE.");
        }
        try { return new FileStream(handle, FileAccess.ReadWrite, bufferSize: 1, isAsync: false); }
        catch { handle.Dispose(); throw; }
    }

    [SupportedOSPlatform("windows")]
    private static FileStream OpenWindows(string directory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new IOException("The discovery temporary-file owner is unavailable.");
        var security = new FileSecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(Path.Combine(directory, $"flowplane-a11-{Guid.NewGuid():N}.manifest"))
            .Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 1,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose, security);
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern SafeFileHandle OpenAnonymous(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string directory, int flags, uint mode);
}
