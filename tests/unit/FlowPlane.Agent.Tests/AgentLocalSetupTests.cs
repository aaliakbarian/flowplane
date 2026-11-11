using System.Text;

using FlowPlane.Agent.Runtime.Setup;

namespace FlowPlane.Agent.Tests;

public sealed class AgentLocalSetupTests
{
    [Fact]
    public void Only_fixed_local_setup_actions_decode_and_pairing_input_is_redacted()
    {
        var request = LocalSetupCodec.Decode(Encoding.UTF8.GetBytes("""
            {"schema":"flowplane.agent/local-setup.v1","action":"Pair","backendAddress":"https://localhost:8443/","pairingCode":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}
            """));
        Assert.Equal(LocalSetupAction.Pair, request.Action);
        Assert.DoesNotContain(request.PairingCode!, request.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"Status\",\"action\":\"Status\"")]
    [InlineData("\"Execute\"")]
    [InlineData("\"status\"")]
    [InlineData("0")]
    public void Duplicate_unknown_or_noncanonical_actions_fail_closed(string action)
    {
        var json = "{\"schema\":\"flowplane.agent/local-setup.v1\",\"action\":" + action + ",\"backendAddress\":null,\"pairingCode\":null}";
        Assert.Throws<LocalSetupException>(() => LocalSetupCodec.Decode(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("https://user:password@localhost/")]
    [InlineData("https://localhost/?code=forbidden")]
    [InlineData("https://localhost/#fragment")]
    public void Pairing_address_never_accepts_http_or_embedded_credentials(string address) =>
        Assert.Throws<LocalSetupException>(() => LocalSetupCodec.Encode(new(LocalSetupAction.Pair, address, new string('A', 43))));

    [Fact]
    public void Unneeded_pairing_secrets_unknown_members_and_oversize_requests_are_rejected()
    {
        Assert.Throws<LocalSetupException>(() => LocalSetupCodec.Encode(new(LocalSetupAction.Status, null, new string('A', 43))));
        Assert.Throws<LocalSetupException>(() => LocalSetupCodec.Decode(new byte[4097]));
        Assert.Throws<LocalSetupException>(() => LocalSetupCodec.Decode(Encoding.UTF8.GetBytes("""
            {"schema":"flowplane.agent/local-setup.v1","action":"Status","backendAddress":null,"pairingCode":null,"shell":"forbidden"}
            """)));
    }
}
