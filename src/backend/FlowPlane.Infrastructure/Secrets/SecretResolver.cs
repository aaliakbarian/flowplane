using FlowPlane.Application.Secrets;
using FlowPlane.Domain.Providers;

namespace FlowPlane.Infrastructure.Secrets;

internal sealed class SecretResolver(
    EnvironmentSecretResolver environmentResolver,
    ManagedSecretStore managedSecretStore) : ISecretResolver
{
    public ValueTask<TResult> UseAsync<TResult>(
        SecretReference reference,
        Func<ReadOnlyMemory<char>, CancellationToken, ValueTask<TResult>> use,
        CancellationToken cancellationToken = default) => reference.Scheme switch
        {
            SecretReferenceScheme.Environment => environmentResolver.UseAsync(
                reference,
                use,
                cancellationToken),
            SecretReferenceScheme.FlowPlane => managedSecretStore.UseAsync(
                reference,
                use,
                cancellationToken),
            _ => throw new SecretResolutionException(
                SecretErrorCode.UnsupportedReference,
                "The secret reference scheme is unsupported."),
        };
}
