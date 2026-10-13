using System.Text.Json;

using FlowPlane.Domain.Providers;
using FlowPlane.Providers.Ssis.Configuration;

namespace FlowPlane.ProviderConfiguration.Tests;

public sealed class ProviderModelTests
{
    [Fact]
    public void Existing_provider_instance_has_no_public_secret_reference_mutator()
    {
        var methodsAcceptingSecretReference = typeof(ProviderInstance)
            .GetMethods()
            .Where(method => !method.IsStatic)
            .Where(method => method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(SecretReference)))
            .Select(method => method.Name)
            .ToArray();

        Assert.Empty(methodsAcceptingSecretReference);
    }

    [Theory]
    [InlineData("ssis")]
    [InlineData("future-provider-2")]
    public void ProviderType_accepts_canonical_identifiers(string value)
    {
        var providerType = ProviderType.Parse(value);

        Assert.Equal(value, providerType.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("SSIS")]
    [InlineData("ssis provider")]
    [InlineData("-ssis")]
    public void ProviderType_rejects_noncanonical_identifiers(string value)
    {
        Assert.Throws<ArgumentException>(() => ProviderType.Parse(value));
    }

    [Fact]
    public void SecretReference_round_trips_supported_canonical_forms_without_revealing_locator()
    {
        var id = Guid.Parse("018f8f55-6a21-7f4c-9d3a-2739e843dc74");
        var managed = SecretReference.Parse($"flowplane:{id:D}");
        var environment = SecretReference.Parse("env:FlowPlane_Ssis_Test_2");

        Assert.Equal($"flowplane:{id:D}", managed.ToCanonicalString());
        Assert.Equal("env:FlowPlane_Ssis_Test_2", environment.ToCanonicalString());
        Assert.DoesNotContain(id.ToString("D"), managed.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("FlowPlane_Ssis_Test_2", environment.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("flowplane:018F8F55-6A21-7F4C-9D3A-2739E843DC74")]
    [InlineData("flowplane:not-a-guid")]
    [InlineData("env:9STARTS_WITH_NUMBER")]
    [InlineData("env:HAS-DASH")]
    [InlineData("vault:anything")]
    public void SecretReference_rejects_noncanonical_or_unsupported_forms(string value)
    {
        Assert.Throws<ArgumentException>(() => SecretReference.Parse(value));
    }

    [Fact]
    public void ProviderInstance_contains_only_provider_independent_configuration_and_reference()
    {
        var codec = new SsisProviderConfigurationCodec();
        var document = codec.Encode(new SsisProviderConfiguration(
            "sql-prod.example.internal",
            1433,
            "flowplane_prod",
            TrustServerCertificate: false));
        var secretReference = SecretReference.Parse(
            "flowplane:018f8f55-6a21-7f4c-9d3a-2739e843dc74");
        var now = DateTimeOffset.Parse("2026-10-13T10:00:00Z");

        var instance = ProviderInstance.Create(
            Guid.Parse("018f8f55-82e7-734b-ab9a-a2d355bc9f6d"),
            SsisProviderConfigurationCodec.SsisProviderType,
            "Production SSIS",
            isEnabled: true,
            document,
            secretReference,
            now);

        Assert.Equal("Production SSIS", instance.DisplayName);
        Assert.Equal("ssis", instance.ProviderType.Value);
        Assert.Equal(secretReference, instance.SecretReference);
        Assert.Equal(1, instance.Version);
        Assert.Equal(now, instance.CreatedAtUtc);
        Assert.Equal(now, instance.UpdatedAtUtc);
        var serialized = JsonSerializer.Serialize(instance);
        Assert.DoesNotContain("password", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "018f8f55-6a21-7f4c-9d3a-2739e843dc74",
            serialized,
            StringComparison.Ordinal);
    }
}
