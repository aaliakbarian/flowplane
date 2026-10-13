using FlowPlane.Domain.Providers;

namespace FlowPlane.Application.Providers;

public interface IProviderDiscoveryCapability
{
    ProviderType ProviderType { get; }

    IAsyncEnumerable<ProviderDiscoveryCandidate> DiscoverAsync(
        ProviderInstance providerInstance,
        CancellationToken cancellationToken = default);
}

public sealed record ProviderDiscoveryCandidate(
    string NativeReference,
    string DisplayName,
    bool IsExecutable);
