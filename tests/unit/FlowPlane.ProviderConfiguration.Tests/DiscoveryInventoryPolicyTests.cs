using FlowPlane.Application.Providers;
using FlowPlane.Application.Providers.DiscoveryInventory;

namespace FlowPlane.ProviderConfiguration.Tests;

public sealed class DiscoveryInventoryPolicyTests
{
    [Theory]
    [InlineData(1800, 300, 3600)]
    [InlineData(86400, 300, 86760)]
    [InlineData(3600, 600, 4260)]
    public void Deadline_covers_the_actual_scan_and_all_preparation_budgets(int scan, int prepare, int expected)
    {
        var options = new DiscoveryInventoryOptions { ScanTimeoutSeconds = scan, PreparationTimeoutSeconds = prepare };
        Assert.Equal(TimeSpan.FromSeconds(expected), options.RefreshTimeout);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(86401)]
    [InlineData(int.MaxValue)]
    public void Invalid_scan_budgets_are_rejected_before_a_refresh(int value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiscoveryInventoryOptions { ScanTimeoutSeconds = value }.Validate());

    [Theory]
    [InlineData(true, ProviderDiscoveryOutcome.Succeeded, ProviderDiscoverySourceCompatibility.Compatible, 0, true)]
    [InlineData(true, ProviderDiscoveryOutcome.Partial, ProviderDiscoverySourceCompatibility.Compatible, 1, true)]
    [InlineData(false, ProviderDiscoveryOutcome.Partial, ProviderDiscoverySourceCompatibility.Compatible, 1, false)]
    [InlineData(true, ProviderDiscoveryOutcome.Failed, ProviderDiscoverySourceCompatibility.Compatible, 0, false)]
    [InlineData(true, ProviderDiscoveryOutcome.Succeeded, ProviderDiscoverySourceCompatibility.Unverified, 0, false)]
    [InlineData(true, ProviderDiscoveryOutcome.Succeeded, ProviderDiscoverySourceCompatibility.Compatible, 1, false)]
    public void Publication_requires_complete_coverage_not_universal_definition_compatibility(
        bool complete, ProviderDiscoveryOutcome outcome, ProviderDiscoverySourceCompatibility source, long incompatible, bool publish)
    {
        var now = DateTimeOffset.UtcNow;
        var result = new ProviderDiscoveryScanResult(outcome, complete, source, 2, 0, incompatible, now, now, []);
        Assert.Equal(publish, DiscoveryPublicationPolicy.CanPublish(result, 2, 0));
    }

    [Fact]
    public void Successful_empty_scan_is_publishable_but_durable_count_disagreement_is_not()
    {
        var now = DateTimeOffset.UtcNow;
        var summary = new ProviderDiscoveryScanResult(ProviderDiscoveryOutcome.Succeeded, true,
            ProviderDiscoverySourceCompatibility.Compatible, 0, 0, 0, now, now, []);
        Assert.True(DiscoveryPublicationPolicy.CanPublish(summary, 0, 0));
        Assert.False(DiscoveryPublicationPolicy.CanPublish(summary, 1, 0));
    }
}
