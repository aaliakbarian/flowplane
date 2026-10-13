using FlowPlane.Application.Providers;

namespace FlowPlane.ProviderConfiguration.Tests;

public sealed class ProviderCapabilityContractTests
{
    [Fact]
    public void A09_capabilities_are_narrow_validation_and_discovery_boundaries()
    {
        Assert.Equal(
            ["ValidateAsync", "get_ProviderType"],
            PublicMethodNames<IProviderInstanceValidationCapability>());
        Assert.Equal(
            ["DiscoverAsync", "get_ProviderType"],
            PublicMethodNames<IProviderDiscoveryCapability>());
    }

    private static string[] PublicMethodNames<T>() => typeof(T)
        .GetMethods()
        .Select(method => method.Name)
        .Order(StringComparer.Ordinal)
        .ToArray();
}
