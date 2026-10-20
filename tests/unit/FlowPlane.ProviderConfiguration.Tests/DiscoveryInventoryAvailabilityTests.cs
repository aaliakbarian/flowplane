using FlowPlane.Application.Providers;

namespace FlowPlane.ProviderConfiguration.Tests;

public sealed class DiscoveryInventoryAvailabilityTests
{
    [Fact]
    public void Inventory_refresh_boundary_is_available_without_a_database_or_external_provider()
    {
        var application = typeof(IProviderDiscoveryCapability).Assembly;
        Assert.NotNull(application.GetType(
            "FlowPlane.Application.Providers.DiscoveryInventory.ProviderDiscoveryRefreshService"));
    }
}
