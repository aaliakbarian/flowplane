using System.Text;
using FlowPlane.Agent.Windows.Identity;
using FlowPlane.Agent.Windows.Security;

namespace FlowPlane.Agent.Windows.Tests;

public sealed class AgentControlIdentityRecordTests
{
    [Fact]
    [Trait("Category", "PortableBoundary")]
    public void Legacy_identity_keeps_installation_key_and_v2_selects_only_explicit_staged_key()
    {
        var installation = Guid.NewGuid();
        var staged = Guid.NewGuid();
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
        { writer.Write(1); writer.Write(installation.ToByteArray()); writer.Write(false); writer.Write(1); writer.Write((byte)1); }
        var legacy = AgentIdentityPublicRecord.Decode(bytes.ToArray());
        Assert.Equal(installation, legacy.InstallationId);
        Assert.Equal(installation, legacy.KeyId);
        var next = new AgentIdentityPublicRecord(installation, staged, false, [1, 2]);
        var reopened = AgentIdentityPublicRecord.Decode(next.Encode());
        Assert.Equal(staged, reopened.KeyId);
        Assert.Equal(installation, reopened.InstallationId);
        Assert.Equal(new byte[] { 1, 2 }, reopened.Der);
        Assert.Throws<AgentWindowsException>(() => AgentIdentityPublicRecord.Decode(next.Encode()[..^1]));
    }
}
