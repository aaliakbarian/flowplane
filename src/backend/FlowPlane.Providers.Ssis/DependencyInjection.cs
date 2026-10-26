using FlowPlane.Application.Providers;
using FlowPlane.Application.Providers.DiscoveryInventory;
using FlowPlane.Providers.Ssis.Configuration;
using FlowPlane.Providers.Ssis.Discovery;
using FlowPlane.Providers.Ssis.Validation;

using Microsoft.Extensions.DependencyInjection;

namespace FlowPlane.Providers.Ssis;

public static class DependencyInjection
{
    public static IServiceCollection AddFlowPlaneSsisValidation(
        this IServiceCollection services,
        bool isDevelopment,
        SsisValidationOptions? options = null,
        SsisDiscoveryOptions? discoveryOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(new SsisValidationSecurityPolicy(isDevelopment));
        services.AddSingleton(options ?? SsisValidationOptions.Default);
        var configuredDiscoveryOptions = discoveryOptions ?? SsisDiscoveryOptions.Default;
        configuredDiscoveryOptions.Validate();
        services.AddSingleton(configuredDiscoveryOptions);
        services.AddSingleton<SsisProviderConfigurationCodec>();
        services.AddSingleton<ISsisSqlExecutor, SsisSqlExecutor>();
        services.AddScoped<ISsisValidationProbe, SsisSqlValidationProbe>();
        services.AddScoped<IProviderInstanceValidationCapability, SsisProviderInstanceValidator>();
        services.AddSingleton<ISsisDiscoveryQuery, SsisSqlDiscoveryQuery>();
        services.AddScoped<IProviderDiscoveryCapability, SsisProviderDiscoveryCapability>();
        services.AddSingleton<ISsisInventoryQuery, SsisInventoryQuery>();
        services.AddScoped<IProviderInventoryDisclosure, SsisInventoryDisclosure>();
        return services;
    }
}
