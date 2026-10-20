using FlowPlane.Providers.Ssis.Discovery;

namespace FlowPlane.Providers.Ssis.Tests;

public sealed class SsisDiscoveryOptionsTests
{
    [Fact]
    public void Defaults_support_a_complete_scan_without_total_limits()
    {
        var options = SsisDiscoveryOptions.Default;
        Assert.Equal(20, options.MaxProjectsPerBatch);
        Assert.Equal(5, options.ConnectionTimeoutSeconds);
        Assert.Equal(15, options.CommandTimeoutSeconds);
        Assert.Equal(1800, options.OverallTimeoutSeconds);
        options.Validate();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    public void Invalid_batch_sizes_fail_clearly(int value)
    {
        var options = new SsisDiscoveryOptions { MaxProjectsPerBatch = value };
        Assert.Contains("MaxProjectsPerBatch",
            Assert.Throws<InvalidOperationException>(options.Validate).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Boundary_values_are_accepted_without_imposing_a_catalog_limit()
    {
        new SsisDiscoveryOptions
        {
            MaxProjectsPerBatch = 100,
            ConnectionTimeoutSeconds = 60,
            CommandTimeoutSeconds = 600,
            OverallTimeoutSeconds = 86400,
        }.Validate();
    }

    [Theory]
    [InlineData(0, 15, 1800)]
    [InlineData(61, 15, 1800)]
    [InlineData(5, -1, 1800)]
    [InlineData(5, 601, 1800)]
    [InlineData(5, 15, 0)]
    [InlineData(5, 15, 86401)]
    [InlineData(5, 15, 10)]
    public void Unsafe_timeouts_fail_clearly(int connection, int command, int overall)
    {
        Assert.Throws<InvalidOperationException>(new SsisDiscoveryOptions
        {
            ConnectionTimeoutSeconds = connection,
            CommandTimeoutSeconds = command,
            OverallTimeoutSeconds = overall,
        }.Validate);
    }

    [Theory]
    [InlineData("MaxCandidates")]
    [InlineData("maxparameterspercandidate")]
    [InlineData("MaxEnvironmentReferencesPerProject")]
    public void Retired_total_limits_fail_instead_of_being_ignored(string key)
    {
        Assert.Contains(key, Assert.Throws<InvalidOperationException>(() =>
            SsisDiscoveryOptions.RejectObsoleteKeys([key])).Message, StringComparison.Ordinal);
    }
}
