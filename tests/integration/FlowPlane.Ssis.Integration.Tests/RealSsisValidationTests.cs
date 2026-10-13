using FlowPlane.Application.Providers;
using FlowPlane.Application.Secrets;
using FlowPlane.Domain.Providers;
using FlowPlane.Infrastructure.Secrets;
using FlowPlane.Providers.Ssis;
using FlowPlane.Providers.Ssis.Configuration;
using FlowPlane.Providers.Ssis.Validation;

using Microsoft.Extensions.DependencyInjection;

namespace FlowPlane.Ssis.Integration.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealSsisCollection
{
    public const string Name = "real-ssis";
}

[Collection(RealSsisCollection.Name)]
public sealed class RealSsisValidationTests
{
    [Fact]
    public async Task Least_privilege_principal_has_discovery_and_execution_capabilities()
    {
        var settings = RealSsisSettings.Load("VALID");
        Assert.SkipWhen(settings is null, RealSsisSettings.SkipMessage("VALID"));

        var result = await ValidateAsync(settings!, TestContext.Current.CancellationToken);

        AssertRecognizedUnverified(result);
        Assert.Equal(ProviderCapabilityAvailability.Available, Capability(result, "discovery"));
        Assert.Equal(ProviderCapabilityAvailability.Available, Capability(result, "execution"));
    }

    [Fact]
    public async Task Readonly_principal_reports_execution_unavailable()
    {
        var settings = RealSsisSettings.Load("READONLY");
        Assert.SkipWhen(settings is null, RealSsisSettings.SkipMessage("READONLY"));

        var result = await ValidateAsync(settings!, TestContext.Current.CancellationToken);

        AssertRecognizedUnverified(result);
        Assert.Equal(ProviderCapabilityAvailability.Available, Capability(result, "discovery"));
        Assert.Equal(ProviderCapabilityAvailability.Unavailable, Capability(result, "execution"));
    }

    [Fact]
    public async Task Principal_without_visible_objects_reports_unknown_capabilities_while_version_is_unverified()
    {
        var settings = RealSsisSettings.Load("NO_OBJECT_PERMISSION");
        Assert.SkipWhen(settings is null, RealSsisSettings.SkipMessage("NO_OBJECT_PERMISSION"));

        var result = await ValidateAsync(settings!, TestContext.Current.CancellationToken);

        AssertRecognizedUnverified(result);
        Assert.Equal(ProviderCapabilityAvailability.Unknown, Capability(result, "discovery"));
        Assert.Equal(ProviderCapabilityAvailability.Unknown, Capability(result, "execution"));
    }

    [Fact]
    public async Task Wrong_password_is_rejected_without_disclosure()
    {
        var settings = RealSsisSettings.Load("VALID");
        Assert.SkipWhen(settings is null, RealSsisSettings.SkipMessage("VALID"));
        const string variable = "FLOWPLANE_TEST_SSIS_RUNTIME_WRONG_PASSWORD";
        var canary = Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, canary);
        try
        {
            var result = await ValidateAsync(
                settings! with { PasswordVariable = variable },
                TestContext.Current.CancellationToken);

            Assert.False(result.IsValid);
            Assert.Contains(result.Checks, check => check.Code == "ssis.authentication.rejected");
            Assert.DoesNotContain(canary, result.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task Optional_no_catalog_principal_reports_catalog_inaccessible()
    {
        var settings = RealSsisSettings.Load("NO_CATALOG_ACCESS");
        Assert.SkipWhen(settings is null, RealSsisSettings.SkipMessage("NO_CATALOG_ACCESS"));

        var result = await ValidateAsync(settings!, TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Contains(result.Checks, check =>
            check.Code is "ssis.catalog.inaccessible" or "ssis.catalog.missing");
    }

    [Fact]
    public async Task Optional_untrusted_certificate_is_rejected_without_downgrade()
    {
        var settings = RealSsisSettings.Load("TLS_FAILURE");
        Assert.SkipWhen(settings is null, RealSsisSettings.SkipMessage("TLS_FAILURE"));

        var result = await ValidateAsync(
            settings! with { TrustServerCertificate = false },
            TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Contains(result.Checks, check => check.Code == "ssis.tls.certificate-rejected");
    }

    private static async Task<ProviderValidationResult> ValidateAsync(
        RealSsisSettings settings,
        CancellationToken cancellationToken)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISecretResolver, EnvironmentSecretResolver>();
        services.AddFlowPlaneSsisValidation(
            isDevelopment: true,
            new SsisValidationOptions(5, 10, 30));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var validator = Assert.Single(
            scope.ServiceProvider.GetServices<IProviderInstanceValidationCapability>());
        var codec = new SsisProviderConfigurationCodec();
        var configuration = codec.Encode(new SsisProviderConfiguration(
            settings.Host,
            settings.Port,
            settings.Username,
            settings.TrustServerCertificate));
        var instance = ProviderInstance.Create(
            Guid.NewGuid(),
            SsisProviderConfigurationCodec.SsisProviderType,
            "A-10 controlled SSIS test",
            isEnabled: true,
            configuration,
            SecretReference.Environment(settings.PasswordVariable),
            DateTimeOffset.UtcNow);

        return await validator.ValidateAsync(instance, cancellationToken);
    }

    private static ProviderCapabilityAvailability Capability(
        ProviderValidationResult result,
        string code) => Assert.Single(
            result.Capabilities,
            capability => capability.Code == code).Availability;

    private static void AssertRecognizedUnverified(ProviderValidationResult result)
    {
        Assert.Equal(ProviderValidationOutcome.Failed, result.Outcome);
        Assert.Contains(result.Checks, check =>
            check.Code == "ssis.server-version.unverified" &&
            check.Status == ProviderValidationCheckStatus.Failed);
        Assert.Contains(result.Checks, check =>
            check.Code == "ssis.catalog.surface-compatible" &&
            check.Status == ProviderValidationCheckStatus.Succeeded);
    }
}

internal sealed record RealSsisSettings(
    string Host,
    int Port,
    string Username,
    string PasswordVariable,
    bool TrustServerCertificate)
{
    public static RealSsisSettings? Load(string role)
    {
        var host = Environment.GetEnvironmentVariable("FLOWPLANE_TEST_SSIS_HOST");
        var usernameVariable = $"FLOWPLANE_TEST_SSIS_{role}_USERNAME";
        var passwordVariable = $"FLOWPLANE_TEST_SSIS_{role}_PASSWORD";
        var username = Environment.GetEnvironmentVariable(usernameVariable);
        var password = Environment.GetEnvironmentVariable(passwordVariable);
        if (string.IsNullOrWhiteSpace(host) ||
            string.IsNullOrWhiteSpace(username) ||
            password is null)
        {
            return null;
        }

        var portText = Environment.GetEnvironmentVariable("FLOWPLANE_TEST_SSIS_PORT");
        var port = int.TryParse(portText, out var parsedPort) ? parsedPort : 1433;
        var trust = bool.TryParse(
            Environment.GetEnvironmentVariable("FLOWPLANE_TEST_SSIS_TRUST_SERVER_CERTIFICATE"),
            out var parsedTrust) && parsedTrust;
        return new RealSsisSettings(host, port, username, passwordVariable, trust);
    }

    public static string SkipMessage(string role) =>
        $"Set FLOWPLANE_TEST_SSIS_HOST, FLOWPLANE_TEST_SSIS_{role}_USERNAME, and " +
        $"FLOWPLANE_TEST_SSIS_{role}_PASSWORD to run this controlled real-SSISDB test.";
}
