using FlowPlane.Application.Providers;
using FlowPlane.Providers.Ssis.Configuration;
using FlowPlane.Providers.Ssis.Validation;

using Microsoft.Extensions.DependencyInjection;

namespace FlowPlane.Providers.Ssis;

public static class DependencyInjection
{
    public static IServiceCollection AddFlowPlaneSsisValidation(
        this IServiceCollection services,
        bool isDevelopment,
        SsisValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(new SsisValidationSecurityPolicy(isDevelopment));
        services.AddSingleton(options ?? SsisValidationOptions.Default);
        services.AddSingleton<SsisProviderConfigurationCodec>();
        services.AddSingleton<ISsisSqlExecutor, SsisSqlExecutor>();
        services.AddScoped<ISsisValidationProbe, SsisSqlValidationProbe>();
        services.AddScoped<IProviderInstanceValidationCapability, SsisProviderInstanceValidator>();
        return services;
    }
}
