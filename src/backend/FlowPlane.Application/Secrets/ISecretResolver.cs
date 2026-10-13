using FlowPlane.Domain.Providers;

namespace FlowPlane.Application.Secrets;

public interface ISecretResolver
{
    ValueTask<TResult> UseAsync<TResult>(
        SecretReference reference,
        Func<ReadOnlyMemory<char>, CancellationToken, ValueTask<TResult>> use,
        CancellationToken cancellationToken = default);
}
