using FlowPlane.Application.Secrets;
using FlowPlane.Domain.Providers;
using FlowPlane.Infrastructure.Secrets;

namespace FlowPlane.ProviderConfiguration.Tests;

public sealed class EnvironmentSecretResolverTests
{
    [Fact]
    public async Task Two_environment_references_resolve_independently_without_persistence()
    {
        const string firstName = "FLOWPLANE_A09_TEST_SECRET_A";
        const string secondName = "FLOWPLANE_A09_TEST_SECRET_B";
        Environment.SetEnvironmentVariable(firstName, "first-canary");
        Environment.SetEnvironmentVariable(secondName, "second-canary");

        try
        {
            var resolver = new EnvironmentSecretResolver();
            var cancellationToken = TestContext.Current.CancellationToken;

            var first = await resolver.UseAsync(
                SecretReference.Parse($"env:{firstName}"),
                (secret, _) => ValueTask.FromResult(new string(secret.Span)),
                cancellationToken);
            var second = await resolver.UseAsync(
                SecretReference.Parse($"env:{secondName}"),
                (secret, _) => ValueTask.FromResult(new string(secret.Span)),
                cancellationToken);

            Assert.Equal("first-canary", first);
            Assert.Equal("second-canary", second);
        }
        finally
        {
            Environment.SetEnvironmentVariable(firstName, null);
            Environment.SetEnvironmentVariable(secondName, null);
        }
    }

    [Fact]
    public async Task Missing_environment_value_returns_safe_structured_error()
    {
        const string variableName = "FLOWPLANE_A09_MISSING_SECRET";
        Environment.SetEnvironmentVariable(variableName, null);
        var resolver = new EnvironmentSecretResolver();
        var cancellationToken = TestContext.Current.CancellationToken;

        var exception = await Assert.ThrowsAsync<SecretResolutionException>(async () =>
            await resolver.UseAsync(
                SecretReference.Parse($"env:{variableName}"),
                static (_, _) => ValueTask.FromResult(true),
                cancellationToken));

        Assert.Equal(SecretErrorCode.SecretNotFound, exception.Code);
        Assert.DoesNotContain(variableName, exception.ToString(), StringComparison.Ordinal);
    }
}
