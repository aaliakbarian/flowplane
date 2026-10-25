using FlowPlane.Application.Providers;
using FlowPlane.Application.Secrets;
using FlowPlane.Domain.Providers;
using FlowPlane.Providers.Ssis.Configuration;

namespace FlowPlane.Providers.Ssis.Validation;

internal sealed class SsisProviderInstanceValidator(
    SsisProviderConfigurationCodec configurationCodec,
    ISecretResolver secretResolver,
    ISsisValidationProbe probe,
    SsisValidationSecurityPolicy securityPolicy,
    SsisValidationOptions options) : IProviderInstanceValidationCapability
{
    private static readonly string[] LaterCheckCodes =
    [
        "ssis.endpoint.reachable",
        "ssis.tls.valid",
        "ssis.authentication.accepted",
        "ssis.catalog.available",
        "ssis.server-version.status",
        "ssis.catalog.surface-compatible",
        "ssis.permission.discovery",
        "ssis.permission.execution",
    ];

    public ProviderType ProviderType => SsisProviderConfigurationCodec.SsisProviderType;

    public async Task<ProviderValidationResult> ValidateAsync(
        ProviderInstance providerInstance,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, ValueTask<bool>>? contextIsCurrent = null)
    {
        ArgumentNullException.ThrowIfNull(providerInstance);

        if (!providerInstance.ProviderType.Equals(ProviderType) || !providerInstance.IsEnabled)
        {
            return ConfigurationFailure("ssis.configuration.invalid");
        }

        SsisProviderConfiguration configuration;
        try
        {
            configuration = configurationCodec.Decode(providerInstance.Configuration);
        }
        catch (ProviderConfigurationException)
        {
            return ConfigurationFailure("ssis.configuration.invalid");
        }

        if (!securityPolicy.Allows(configuration))
        {
            return ConfigurationFailure("ssis.configuration.tls-policy-rejected");
        }

        using var overallTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overallTimeout.CancelAfter(TimeSpan.FromSeconds(options.OverallTimeoutSeconds));

        try
        {
            var result = await secretResolver.UseAsync(
                providerInstance.SecretReference,
                async (password, token) =>
                {
                    if (contextIsCurrent is not null && !await contextIsCurrent(token))
                        return new ProviderValidationResult(ProviderValidationOutcome.Failed,
                            [new("ssis.configuration.context-changed", ProviderValidationCheckStatus.Failed,
                                "The provider context changed during validation."), .. BlockedChecks()],
                            UnknownCapabilities("ssis.configuration.context-changed"), []);
                    return await probe.ValidateAsync(configuration, password, token);
                },
                overallTimeout.Token);

            return result with
            {
                Checks =
                [
                    Success("ssis.configuration.valid", "The SSIS provider configuration is valid."),
                    Success("ssis.secret.available", "The provider credential is available."),
                    .. result.Checks,
                ],
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new ProviderValidationResult(
                ProviderValidationOutcome.Unavailable,
                [
                    Success("ssis.configuration.valid", "The SSIS provider configuration is valid."),
                    Success("ssis.secret.available", "The provider credential is available."),
                    new ProviderValidationCheck(
                        "ssis.validation.timeout",
                        ProviderValidationCheckStatus.Failed,
                        "SSIS provider validation exceeded its time limit."),
                    .. BlockedChecks(),
                ],
                UnknownCapabilities("ssis.validation.timeout"),
                []);
        }
        catch (SecretResolutionException)
        {
            return new ProviderValidationResult(
                ProviderValidationOutcome.Failed,
                [
                    Success("ssis.configuration.valid", "The SSIS provider configuration is valid."),
                    new ProviderValidationCheck(
                        "ssis.secret.unavailable",
                        ProviderValidationCheckStatus.Failed,
                        "The provider credential is unavailable."),
                    .. BlockedChecks(),
                ],
                UnknownCapabilities("ssis.secret.unavailable"),
                []);
        }
        catch (Exception)
        {
            return new ProviderValidationResult(
                ProviderValidationOutcome.Unavailable,
                [
                    Success("ssis.configuration.valid", "The SSIS provider configuration is valid."),
                    new ProviderValidationCheck(
                        "ssis.validation.unexpected-failure",
                        ProviderValidationCheckStatus.Failed,
                        "SSIS provider validation failed unexpectedly."),
                    .. BlockedChecks(),
                ],
                UnknownCapabilities("ssis.validation.unexpected-failure"),
                []);
        }
    }

    private static ProviderValidationResult ConfigurationFailure(string code) => new(
        ProviderValidationOutcome.Failed,
        [
            new ProviderValidationCheck(
                code,
                ProviderValidationCheckStatus.Failed,
                "The SSIS provider configuration is invalid for the current environment."),
            new ProviderValidationCheck(
                "ssis.secret.available",
                ProviderValidationCheckStatus.Unavailable,
                "The check was not executed because configuration validation failed."),
            .. BlockedChecks(),
        ],
        UnknownCapabilities(code),
        []);

    private static ProviderValidationCheck Success(string code, string message) =>
        new(code, ProviderValidationCheckStatus.Succeeded, message);

    private static ProviderValidationCheck[] BlockedChecks() => LaterCheckCodes
        .Select(code => new ProviderValidationCheck(
            code,
            ProviderValidationCheckStatus.Unavailable,
            "The check was not executed because an earlier validation stage did not succeed."))
        .ToArray();

    private static ProviderCapabilityResult[] UnknownCapabilities(string reasonCode) =>
    [
        new("discovery", ProviderCapabilityAvailability.Unknown, reasonCode),
        new("execution", ProviderCapabilityAvailability.Unknown, reasonCode),
    ];
}
