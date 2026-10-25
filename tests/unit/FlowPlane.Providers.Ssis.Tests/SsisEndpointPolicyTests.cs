using FlowPlane.Providers.Ssis.Configuration;

namespace FlowPlane.Providers.Ssis.Tests;

public sealed class SsisEndpointPolicyTests
{
    [Theory]
    [InlineData("sql.example.test", 1433, true)]
    [InlineData("SQL.example.test", 1433, false)]
    [InlineData("other.sql.example.test", 1433, false)]
    [InlineData("sql.example.test.attacker.invalid", 1433, false)]
    [InlineData("sql.example.test", 1434, false)]
    public void Only_the_exact_configured_host_and_port_are_allowed(string host, int port, bool allowed)
    {
        var policy = new SsisEndpointPolicy([new("sql.example.test", 1433)]);
        Assert.Equal(allowed, policy.Allows(new(host, port, "user", false)));
    }

    [Fact]
    public void Empty_configuration_denies_all_endpoints()
    {
        Assert.False(new SsisEndpointPolicy([]).Allows(new("sql.example.test", 1433, "user", false)));
    }

    [Theory]
    [InlineData("http://sql.example.test", 1433)]
    [InlineData("sql.example.test:1433", 1433)]
    [InlineData("sql.example.test,1433", 1433)]
    [InlineData("sql.example.test\\instance", 1433)]
    [InlineData("*.example.test", 1433)]
    [InlineData("sql.example.test;User ID=abc", 1433)]
    [InlineData("[::1]", 1433)]
    [InlineData("::1", 1433)]
    [InlineData("127.1", 1433)]
    [InlineData(" sql.example.test", 1433)]
    [InlineData("sql.example.test", 0)]
    [InlineData("sql.example.test", 65536)]
    public void Unsafe_operator_configuration_is_rejected(string host, int port)
    {
        Assert.Throws<InvalidOperationException>(() => new SsisEndpointPolicy([new(host, port)]));
    }
}
