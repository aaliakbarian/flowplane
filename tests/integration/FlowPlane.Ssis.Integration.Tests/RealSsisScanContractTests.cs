using FlowPlane.Application.Providers;

namespace FlowPlane.Ssis.Integration.Tests;

/// <summary>Read-only existing-catalog evidence; does not replace the undeployed A-11 fixture gate.</summary>
[Collection(RealSsisCollection.Name)]
public sealed class RealSsisScanContractTests
{
    [Fact]
    public async Task Existing_catalog_scan_completes_with_single_project_batches()
    {
        var settings = RealSsisSettings.Load("VALID");
        Assert.SkipWhen(settings is null, RealSsisSettings.SkipMessage("VALID"));

        var scan = await RealSsisDiscoveryTests.ScanAsync(settings!, TestContext.Current.CancellationToken);

        Assert.True(scan.Summary.ScanCompleted,
            string.Join(", ", scan.Summary.Issues.Select(issue => issue.Code)));
        Assert.Equal(ProviderDiscoverySourceCompatibility.Compatible, scan.Summary.SourceCompatibility);
        Assert.True(scan.Summary.ExecutableCandidates > 0);
        Assert.True(scan.Summary.ObservationsDelivered > scan.Summary.ExecutableCandidates);
    }
}
