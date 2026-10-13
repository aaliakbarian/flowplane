using FlowPlane.Application.Providers;
using FlowPlane.Providers.Ssis.Configuration;

namespace FlowPlane.Providers.Ssis.Validation;

internal interface ISsisValidationProbe
{
    Task<ProviderValidationResult> ValidateAsync(
        SsisProviderConfiguration configuration,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken);
}
