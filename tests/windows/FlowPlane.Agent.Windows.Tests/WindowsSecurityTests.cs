using System.Security.AccessControl;
using System.Security.Principal;

using FlowPlane.Agent.Windows.Identity;
using FlowPlane.Agent.Windows.Security;
using FlowPlane.Agent.Setup;

namespace FlowPlane.Agent.Windows.Tests;

// Intentionally outside the portable solution. No OS skips count as Windows acceptance.
public sealed class WindowsSecurityTests
{
    [Fact]
    public void Interactive_operator_is_not_the_service_token()
    {
        Assert.True(OperatingSystem.IsWindows());
        Assert.Throws<AgentWindowsException>(() => new ServiceTokenVerifier().Verify());
    }

    [Fact]
    public void Private_key_acl_excludes_ordinary_users_and_anonymous_callers()
    {
        var sid = new SecurityIdentifier("S-1-5-80-1-2-3-4-5");
        var descriptor = WindowsAgentSecurity.KeyDescriptor(sid);
        Assert.True(WindowsAgentSecurity.IsExactKeyDescriptor(descriptor, sid));
        var permissive = new RawSecurityDescriptor("D:(A;;GA;;;WD)");
        var bytes = new byte[permissive.BinaryLength];
        permissive.GetBinaryForm(bytes, 0);
        Assert.False(WindowsAgentSecurity.IsExactKeyDescriptor(bytes, sid));
    }

    [Fact]
    public async Task Missing_identity_never_creates_a_key_during_normal_load()
    {
        // Requires an explicitly authorized isolated Windows test environment; no service installation.
        var missing = Path.Combine(Path.GetTempPath(), "flowplane-no-identity-" + Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAsync<AgentWindowsException>(async () => await new WindowsAgentIdentity(missing).LoadOrCreateAsync(false, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(missing));
    }

    [Theory]
    [InlineData(@"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;S-1-5-80-1-2-3-4-5)")]
    [InlineData(@"D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGX;;;S-1-5-80-1-2-3-4-5)")]
    [InlineData(@"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGX;;;S-1-5-80-1-2-3-4-5)(A;;GR;;;WD)")]
    public void Broadened_or_inheriting_private_key_acl_is_rejected(string sddl)
    {
        var descriptor = new RawSecurityDescriptor(sddl);
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        Assert.False(WindowsAgentSecurity.IsExactKeyDescriptor(bytes, new SecurityIdentifier("S-1-5-80-1-2-3-4-5")));
    }

    [Theory]
    [InlineData("powershell.exe")]
    [InlineData(@"C:\Temp\FlowPlane.Agent.Windows.exe")]
    [InlineData(@"C:\Temp\FlowPlane.Agent.Windows.exe\"" --command forbidden")]
    public void Installer_rejects_unversioned_untrusted_or_argument_bearing_paths(string path) =>
        Assert.Throws<AgentWindowsException>(() => WindowsServiceInstaller.ValidateBinaryPath(path));
}
