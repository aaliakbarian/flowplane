using System.Text;
using FlowPlane.Agent.Runtime.Setup;
using FlowPlane.Agent.Protocol;

namespace FlowPlane.Agent.Tests;

public sealed class AgentControlSetupTests
{
    [Fact]
    public void Pair_v2_carries_grant_but_legacy_v1_shape_stays_exact()
    {
        var grant = Guid.NewGuid();
        var legacy = LocalSetupCodec.Encode(new(LocalSetupAction.Pair, "https://localhost/", new string('A', 43)));
        Assert.Equal("{\"schema\":\"flowplane.agent/local-setup.v1\",\"action\":\"Pair\",\"backendAddress\":\"https://localhost/\",\"pairingCode\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}", Encoding.UTF8.GetString(legacy));
        var request = new LocalSetupRequest(LocalSetupAction.Pair, "https://localhost/", new string('A', 43), grant);
        var bytes = LocalSetupCodec.Encode(request);
        Assert.Contains("flowplane.agent/local-setup.v2", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.Equal(grant, LocalSetupCodec.Decode(bytes).GrantId);
        Assert.Throws<LocalSetupException>(() => LocalSetupCodec.Encode(new(LocalSetupAction.Status, grantId: grant)));
        Assert.Throws<LocalSetupException>(() => LocalSetupCodec.Encode(new(LocalSetupAction.Pair, "https://localhost/", new string('A', 43), Guid.Empty)));
    }

    [Fact]
    public void Extended_receipt_is_strict_and_cannot_carry_arbitrary_console_text()
    {
        var receipt = new LocalSetupReceipt("flowplane.agent/local-setup-receipt.v2", LocalSetupStatus.PairingPending, "0123-4567-89ab-cdef", PairingStage.Proven);
        Assert.Equal(receipt, LocalSetupCodec.DecodeReceipt(LocalSetupCodec.EncodeReceipt(receipt)));
        Assert.Throws<LocalSetupException>(() => LocalSetupCodec.EncodeReceipt(receipt with { VerificationPhrase = "unsafe\nconsole" }));
        Assert.Throws<LocalSetupException>(() => LocalSetupCodec.DecodeReceipt(Encoding.UTF8.GetBytes("{\"Schema\":\"flowplane.agent/local-setup-receipt.v1\",\"Status\":1,\"Status\":7}")));
    }
}
