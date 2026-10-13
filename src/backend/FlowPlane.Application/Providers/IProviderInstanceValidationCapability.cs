using FlowPlane.Domain.Providers;

namespace FlowPlane.Application.Providers;

public interface IProviderInstanceValidationCapability
{
    ProviderType ProviderType { get; }

    Task<ProviderValidationResult> ValidateAsync(
        ProviderInstance providerInstance,
        CancellationToken cancellationToken = default);
}

public sealed record ProviderValidationResult(
    bool IsValid,
    IReadOnlyList<ProviderValidationIssue> Issues);

public sealed record ProviderValidationIssue(string Code, string Message);
