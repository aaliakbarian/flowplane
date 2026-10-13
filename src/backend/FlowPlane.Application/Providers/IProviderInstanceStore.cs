using FlowPlane.Domain.Providers;

namespace FlowPlane.Application.Providers;

public interface IProviderInstanceStore
{
    Task<ProviderInstance> CreateWithManagedSecretAsync(
        Guid id,
        ProviderType providerType,
        string displayName,
        bool isEnabled,
        ProviderConfigurationDocument configuration,
        ReadOnlyMemory<char> secret,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task ReplaceManagedSecretAsync(
        Guid providerInstanceId,
        long expectedVersion,
        ReadOnlyMemory<char> secret,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        Guid providerInstanceId,
        long expectedVersion,
        CancellationToken cancellationToken = default);
}
