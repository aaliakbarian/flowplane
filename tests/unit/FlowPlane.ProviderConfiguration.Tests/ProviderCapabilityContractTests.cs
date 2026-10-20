using FlowPlane.Application.Providers;
using FlowPlane.Domain.Providers;

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
            ["ScanAsync", "get_ProviderType"],
            PublicMethodNames<IProviderDiscoveryCapability>());
    }

    [Fact]
    public void Discovery_capability_delivers_observations_and_returns_one_terminal_summary()
    {
        var discover = typeof(IProviderDiscoveryCapability).GetMethod("ScanAsync")!;

        Assert.Equal(typeof(Task<ProviderDiscoveryScanResult>), discover.ReturnType);
        Assert.Equal(
            [typeof(ProviderInstance), typeof(IProviderDiscoverySink), typeof(CancellationToken)],
            discover.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
        Assert.Null(typeof(ProviderDiscoveryScanResult).GetProperty("Candidates"));
        Assert.Null(typeof(ProviderDiscoveryScanResult).GetProperty("IsTruncated"));
        Assert.NotNull(typeof(ProviderDiscoveryScanResult).GetProperty("ScanCompleted"));
        Assert.NotNull(typeof(ProviderDiscoveryScanResult).GetProperty("SourceCompatibility"));
    }

    [Fact]
    public void Native_reference_identity_includes_provider_instance_provider_type_and_payload()
    {
        var firstInstance = Guid.Parse("018f8f55-82e7-734b-ab9a-a2d355bc9f6d");
        var secondInstance = Guid.Parse("018f8f55-82e7-734b-ab9a-a2d355bc9f6e");
        var providerType = ProviderType.Parse("fixture");
        var first = TestNativeReferenceCodec.Create(
            firstInstance,
            providerType,
            "fixture/native.v1",
            "{\"name\":\"Package.dtsx\",\"folder\":\"Folder\"}");
        var same = TestNativeReferenceCodec.Create(
            firstInstance,
            providerType,
            "fixture/native.v1",
            "{\"folder\":\"Folder\",\"name\":\"Package.dtsx\"}");
        var otherInstance = TestNativeReferenceCodec.Create(
            secondInstance,
            providerType,
            "fixture/native.v1",
            "{\"folder\":\"Folder\",\"name\":\"Package.dtsx\"}");

        Assert.Equal(first, same);
        Assert.NotEqual(first, otherInstance);
        Assert.Equal(firstInstance, first.ProviderInstanceId);
        Assert.Equal(providerType, first.ProviderType);
        Assert.Equal("fixture/native.v1", first.SchemaId);
        Assert.Equal("{\"folder\":\"Folder\",\"name\":\"Package.dtsx\"}", first.CanonicalJson);
        Assert.DoesNotContain("Package.dtsx", first.ToString(), StringComparison.Ordinal);
    }

    private static string[] PublicMethodNames<T>() => typeof(T)
        .GetMethods()
        .Select(method => method.Name)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private sealed class TestNativeReferenceCodec : NativeDefinitionReferenceCodec
    {
        public static NativeDefinitionReference Create(
            Guid providerInstanceId,
            ProviderType providerType,
            string schemaId,
            string json) => CreateValidatedReference(
                providerInstanceId,
                providerType,
                schemaId,
                json);
    }
}
