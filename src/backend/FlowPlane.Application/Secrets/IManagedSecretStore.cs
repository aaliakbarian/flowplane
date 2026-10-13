using FlowPlane.Domain.Providers;

namespace FlowPlane.Application.Secrets;

public interface IManagedSecretStore
{
    ValueTask<SecretReference> CreateAsync(
        ReadOnlyMemory<char> secret,
        CancellationToken cancellationToken = default);

    ValueTask ReplaceAsync(
        SecretReference reference,
        ReadOnlyMemory<char> secret,
        CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(
        SecretReference reference,
        CancellationToken cancellationToken = default);
}
