using FlowPlane.Providers.Ssis.Configuration;

namespace FlowPlane.Providers.Ssis.Validation;

internal sealed record SsisValidationSecurityPolicy(bool IsDevelopment)
{
    public bool Allows(SsisProviderConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return !configuration.TrustServerCertificate || IsDevelopment;
    }
}
