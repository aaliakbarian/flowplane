using FlowPlane.Application.Providers;

namespace FlowPlane.Providers.Ssis.Tests;

public sealed class ProviderValidationResultTests
{
    [Fact]
    public void Successful_provider_validation_is_valid_when_capabilities_are_unknown()
    {
        var result = new ProviderValidationResult(
            ProviderValidationOutcome.Succeeded,
            [new ProviderValidationCheck(
                "ssis.catalog.surface-compatible",
                ProviderValidationCheckStatus.Succeeded,
                "The required SSISDB catalog surface is compatible.")],
            [new ProviderCapabilityResult(
                "discovery",
                ProviderCapabilityAvailability.Unknown,
                "ssis.permission.discovery.indeterminate")],
            []);

        Assert.True(result.IsValid);
        Assert.Equal(ProviderCapabilityAvailability.Unknown, result.Capabilities[0].Availability);
    }

    [Fact]
    public void Failed_provider_validation_is_invalid_even_when_a_capability_is_available()
    {
        var result = new ProviderValidationResult(
            ProviderValidationOutcome.Failed,
            [new ProviderValidationCheck(
                "ssis.server-version.unsupported",
                ProviderValidationCheckStatus.Failed,
                "The SQL Server major version is unsupported.")],
            [new ProviderCapabilityResult(
                "discovery",
                ProviderCapabilityAvailability.Available,
                "ssis.permission.discovery.available")],
            []);

        Assert.False(result.IsValid);
    }
}
