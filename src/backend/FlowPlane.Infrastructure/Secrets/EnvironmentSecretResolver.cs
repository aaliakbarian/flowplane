using FlowPlane.Application.Secrets;
using FlowPlane.Domain.Providers;

namespace FlowPlane.Infrastructure.Secrets;

public sealed class EnvironmentSecretResolver : ISecretResolver
{
    public async ValueTask<TResult> UseAsync<TResult>(
        SecretReference reference,
        Func<ReadOnlyMemory<char>, CancellationToken, ValueTask<TResult>> use,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(use);

        if (reference.Scheme != SecretReferenceScheme.Environment)
        {
            throw new SecretResolutionException(
                SecretErrorCode.UnsupportedReference,
                "The secret reference scheme is not supported by this resolver.");
        }

        var value = Environment.GetEnvironmentVariable(reference.GetEnvironmentVariableName());
        if (value is null)
        {
            throw new SecretResolutionException(
                SecretErrorCode.SecretNotFound,
                "The referenced environment secret is unavailable.");
        }

        var buffer = value.ToCharArray();
        try
        {
            return await use(buffer, cancellationToken);
        }
        finally
        {
            Array.Clear(buffer);
        }
    }
}
