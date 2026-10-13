using FlowPlane.Domain.Providers;

namespace FlowPlane.Application.Providers;

public interface IProviderInstanceValidationCapability
{
    ProviderType ProviderType { get; }

    Task<ProviderValidationResult> ValidateAsync(
        ProviderInstance providerInstance,
        CancellationToken cancellationToken = default);
}

public enum ProviderValidationOutcome
{
    Succeeded,
    Failed,
    Unavailable,
}

public enum ProviderValidationCheckStatus
{
    Succeeded,
    Failed,
    Unavailable,
}

public enum ProviderCapabilityAvailability
{
    Available,
    Unavailable,
    Unknown,
}

public sealed record ProviderValidationResult(
    ProviderValidationOutcome Outcome,
    IReadOnlyList<ProviderValidationCheck> Checks,
    IReadOnlyList<ProviderCapabilityResult> Capabilities,
    IReadOnlyList<ProviderValidationFact> Facts)
{
    public bool IsValid => Outcome == ProviderValidationOutcome.Succeeded;
}

public sealed record ProviderValidationCheck(
    string Code,
    ProviderValidationCheckStatus Status,
    string Message);

public sealed record ProviderCapabilityResult(
    string Code,
    ProviderCapabilityAvailability Availability,
    string ReasonCode);

public sealed record ProviderValidationFact(string Code, string Value);
