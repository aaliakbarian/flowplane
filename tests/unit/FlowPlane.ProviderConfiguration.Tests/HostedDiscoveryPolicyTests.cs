using FlowPlane.Application.Providers.DiscoveryHosting;

namespace FlowPlane.ProviderConfiguration.Tests;

public sealed class HostedDiscoveryPolicyTests
{
    [Theory]
    [InlineData(-300_000, true)]
    [InlineData(300_000, true)]
    [InlineData(-300_001, false)]
    [InlineData(300_001, false)]
    public void First_admission_uses_database_time_and_bounded_browser_clock_skew(long skew, bool accepted)
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000);
        var key = Guid.CreateVersion7(now.AddMilliseconds(skew));
        Assert.Equal(key, DiscoveryIdempotencyKey.Parse(key.ToString("D")));
        if (accepted) DiscoveryIdempotencyKey.ValidateFresh(key, now);
        else Assert.Equal(HostedDiscoveryError.InvalidKey, Assert.Throws<HostedDiscoveryException>(() =>
            DiscoveryIdempotencyKey.ValidateFresh(key, now)).Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0199a12b-1234-4000-8000-123456789abc")]
    [InlineData("0199a12b-1234-7000-7000-123456789abc")]
    [InlineData("0199A12B-1234-7000-8000-123456789abc")]
    [InlineData("{0199a12b-1234-7000-8000-123456789abc}")]
    public void Noncanonical_non_v7_and_wrong_variant_keys_are_rejected(string? text) =>
        Assert.Throws<HostedDiscoveryException>(() => DiscoveryIdempotencyKey.Parse(text));

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(301, 15, 2)]
    [InlineData(60, 21, 2)]
    [InlineData(60, 15, 21)]
    [InlineData(60, 0, 2)]
    public void Invalid_lease_options_cannot_enable_an_unsafe_runner(int lease, int renew, int poll) =>
        Assert.Throws<InvalidOperationException>(() => new DiscoveryHostingOptions
        { LeaseSeconds = lease, RenewalSeconds = renew, PollSeconds = poll }.Validate());

    [Fact]
    public void Default_runner_does_not_access_unmigrated_schema()
    {
        var options = new DiscoveryHostingOptions();
        options.Validate();
        Assert.False(options.Enabled);
    }
}
