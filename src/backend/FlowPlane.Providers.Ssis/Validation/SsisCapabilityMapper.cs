using FlowPlane.Application.Providers;

namespace FlowPlane.Providers.Ssis.Validation;

internal sealed record SsisPermissionEvidence(
    bool HasReadableScope,
    bool HasExecutableReadableScope,
    bool HasVisibleDefinitions,
    bool IsReadExplicitlyDenied,
    bool HasUnknownDefinitionFormat);

internal static class SsisCapabilityMapper
{
    public static IReadOnlyList<ProviderCapabilityResult> Map(SsisPermissionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        if (evidence.IsReadExplicitlyDenied)
        {
            return
            [
                Result("discovery", ProviderCapabilityAvailability.Unavailable,
                    "ssis.permission.discovery.denied"),
                Result("execution", ProviderCapabilityAvailability.Unavailable,
                    "ssis.permission.execution.denied"),
            ];
        }

        if (evidence.HasUnknownDefinitionFormat)
        {
            return
            [
                Result("discovery", ProviderCapabilityAvailability.Unknown,
                    "ssis.definition-format.unsupported"),
                Result("execution", ProviderCapabilityAvailability.Unknown,
                    "ssis.definition-format.unsupported"),
            ];
        }

        var discovery = evidence.HasReadableScope
            ? Result("discovery", ProviderCapabilityAvailability.Available,
                "ssis.permission.discovery.available")
            : Result("discovery", ProviderCapabilityAvailability.Unknown,
                "ssis.permission.discovery.indeterminate");

        ProviderCapabilityResult execution;
        if (evidence.HasExecutableReadableScope)
        {
            execution = Result("execution", ProviderCapabilityAvailability.Available,
                "ssis.permission.execution.available");
        }
        else if (evidence.HasReadableScope && evidence.HasVisibleDefinitions)
        {
            execution = Result("execution", ProviderCapabilityAvailability.Unavailable,
                "ssis.permission.execution.absent");
        }
        else
        {
            execution = Result("execution", ProviderCapabilityAvailability.Unknown,
                "ssis.permission.execution.indeterminate");
        }

        return [discovery, execution];
    }

    private static ProviderCapabilityResult Result(
        string code,
        ProviderCapabilityAvailability availability,
        string reasonCode) => new(code, availability, reasonCode);
}
