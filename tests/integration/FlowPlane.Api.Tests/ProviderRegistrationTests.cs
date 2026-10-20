using FlowPlane.Application.Providers;
using FlowPlane.Domain.Providers;
using FlowPlane.Providers.Ssis.Discovery;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FlowPlane.Api.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SsisConfigurationCollection
{
    public const string Name = "ssis-configuration";
}

[Collection(SsisConfigurationCollection.Name)]
public sealed class ProviderRegistrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> factory;

    public ProviderRegistrationTests(WebApplicationFactory<Program> factory)
    {
        this.factory = factory;
    }

    [Fact]
    public void Api_composition_root_registers_the_SSIS_capabilities()
    {
        using var configuredFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:FlowPlane"] =
                        "Host=127.0.0.1;Port=1;Database=flowplane;Username=flowplane;Password=test-only"
                });
            });
        });
        using var scope = configuredFactory.Services.CreateScope();

        var validation = Assert.Single(
            scope.ServiceProvider.GetServices<IProviderInstanceValidationCapability>());
        var discovery = Assert.Single(
            scope.ServiceProvider.GetServices<IProviderDiscoveryCapability>());

        Assert.Equal(ProviderType.Parse("ssis"), validation.ProviderType);
        Assert.Equal(ProviderType.Parse("ssis"), discovery.ProviderType);
    }

    [Fact]
    public void Default_SSIS_discovery_configuration_is_registered_without_contacting_SSIS()
    {
        using var configuredFactory = CreateFactory(new Dictionary<string, string?>());

        var options = configuredFactory.Services.GetRequiredService<SsisDiscoveryOptions>();

        Assert.Equal(20, options.MaxProjectsPerBatch);
        Assert.Equal(5, options.ConnectionTimeoutSeconds);
        Assert.Equal(15, options.CommandTimeoutSeconds);
        Assert.Equal(1800, options.OverallTimeoutSeconds);
    }

    [Theory]
    [InlineData("MaxCandidates")]
    [InlineData("MaxParametersPerCandidate")]
    [InlineData("MaxEnvironmentReferencesPerProject")]
    public void Retired_total_limits_are_rejected_instead_of_silently_ignored(string key)
    {
        using var configuredFactory = CreateFactory(new Dictionary<string, string?>
        {
            [$"FlowPlane:Ssis:{key}"] = "10",
        });

        var exception = Assert.Throws<InvalidOperationException>(() => configuredFactory.Services);

        Assert.Contains(key, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Custom_SSIS_discovery_configuration_is_bound_and_registered()
    {
        using var configuredFactory = CreateFactory(new Dictionary<string, string?>
        {
            ["FlowPlane:Ssis:MaxProjectsPerBatch"] = "12",
            ["FlowPlane:Ssis:ConnectionTimeoutSeconds"] = "3",
            ["FlowPlane:Ssis:CommandTimeoutSeconds"] = "9",
            ["FlowPlane:Ssis:OverallTimeoutSeconds"] = "20",
        });

        var options = configuredFactory.Services.GetRequiredService<SsisDiscoveryOptions>();

        Assert.Equal(12, options.MaxProjectsPerBatch);
        Assert.Equal(3, options.ConnectionTimeoutSeconds);
        Assert.Equal(9, options.CommandTimeoutSeconds);
        Assert.Equal(20, options.OverallTimeoutSeconds);
    }

    [Fact]
    public void Standard_environment_variables_override_SSIS_discovery_configuration()
    {
        const string environmentName = "FlowPlane__Ssis__MaxProjectsPerBatch";
        var previous = Environment.GetEnvironmentVariable(environmentName);
        Environment.SetEnvironmentVariable(environmentName, "17");
        try
        {
            using var configuredFactory = CreateFactory(new Dictionary<string, string?>());

            var options = configuredFactory.Services.GetRequiredService<SsisDiscoveryOptions>();

            Assert.Equal(17, options.MaxProjectsPerBatch);
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentName, previous);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("not-an-integer")]
    public void Invalid_SSIS_discovery_configuration_prevents_startup_with_a_specific_error(
        string configuredValue)
    {
        using var configuredFactory = CreateFactory(new Dictionary<string, string?>
        {
            ["FlowPlane:Ssis:MaxProjectsPerBatch"] = configuredValue,
        });

        var exception = Assert.Throws<InvalidOperationException>(() => configuredFactory.Services);

        Assert.Contains("FlowPlane:Ssis:MaxProjectsPerBatch", exception.ToString(),
            StringComparison.Ordinal);
    }

    private WebApplicationFactory<Program> CreateFactory(
        IReadOnlyDictionary<string, string?> ssisConfiguration) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting(
                "ConnectionStrings:FlowPlane",
                "Host=127.0.0.1;Port=1;Database=flowplane;Username=flowplane;Password=test-only");
            foreach (var setting in ssisConfiguration)
            {
                builder.UseSetting(setting.Key, setting.Value);
            }
        });
}
