using FlowPlane.Application.Providers;
using FlowPlane.Application.Secrets;
using FlowPlane.Domain.Providers;
using FlowPlane.Providers.Ssis.Configuration;
using FlowPlane.Providers.Ssis.Validation;

using Microsoft.Data.SqlClient;

namespace FlowPlane.Providers.Ssis.Discovery;

internal sealed class SsisProviderDiscoveryCapability(
    SsisProviderConfigurationCodec configurationCodec,
    ISecretResolver secretResolver,
    ISsisSqlExecutor validationSqlExecutor,
    ISsisDiscoveryQuery discoveryQuery,
    SsisValidationSecurityPolicy securityPolicy,
    SsisDiscoveryOptions options) : IProviderDiscoveryCapability
{
    public ProviderType ProviderType => SsisProviderConfigurationCodec.SsisProviderType;

    public async Task<ProviderDiscoveryScanResult> ScanAsync(
        ProviderInstance providerInstance, IProviderDiscoverySink sink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providerInstance);
        ArgumentNullException.ThrowIfNull(sink);
        cancellationToken.ThrowIfCancellationRequested();
        var state = new ScanState(sink);
        ProviderDiscoveryScanResult Error(string code, string message,
            ProviderDiscoveryOutcome outcome = ProviderDiscoveryOutcome.Failed) =>
            state.Result(outcome, false, [new(code, message)]);
        if (providerInstance.ProviderType != ProviderType || !providerInstance.IsEnabled)
            return Error("ssis.discovery.configuration-invalid", "The SSIS provider configuration is invalid.");

        SsisProviderConfiguration configuration;
        try { configuration = configurationCodec.Decode(providerInstance.Configuration); }
        catch (ProviderConfigurationException)
        {
            return Error("ssis.discovery.configuration-invalid", "The SSIS provider configuration is invalid.");
        }
        if (!securityPolicy.Allows(configuration))
            return Error("ssis.discovery.tls-policy-rejected",
                "The SSIS provider TLS configuration is not allowed in this environment.");

        options.Validate();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.OverallTimeoutSeconds));
        try
        {
            return await secretResolver.UseAsync(providerInstance.SecretReference, async (password, token) =>
            {
                var facts = await validationSqlExecutor.ReadMasterFactsAsync(configuration, password,
                    new SsisValidationOptions(options.ConnectionTimeoutSeconds,
                        options.CommandTimeoutSeconds, options.OverallTimeoutSeconds), token);
                switch (SsisCompatibilityPolicy.EvaluateServerVersion(facts.ProductVersion))
                {
                    case SsisServerVersionCompatibility.Unverified:
                        state.SourceCompatibility = ProviderDiscoverySourceCompatibility.Unverified;
                        return Error("ssis.discovery.server-version-unverified",
                            "The SQL Server major version is recognized but unverified for this path.");
                    case SsisServerVersionCompatibility.Unsupported:
                        state.SourceCompatibility = ProviderDiscoverySourceCompatibility.Unsupported;
                        return Error("ssis.discovery.server-version-unsupported",
                            "The SQL Server major version is unsupported for this path.");
                }
                switch (SsisCatalogAccessMapper.Map(facts.HasSsisDbAccess))
                {
                    case SsisCatalogAccess.Missing:
                        return Error("ssis.discovery.catalog-missing", "SSISDB does not exist.");
                    case SsisCatalogAccess.Available:
                        break;
                    default:
                        return Error("ssis.discovery.catalog-inaccessible", "SSISDB is inaccessible or unavailable.");
                }

                await using var session = await discoveryQuery.OpenSessionAsync(
                    configuration, password, options, token);
                state.SourceCompatibility = ProviderDiscoverySourceCompatibility.Compatible;
                var counts = await SsisDiscoveryScanner.ScanAsync(
                    providerInstance.Id, session, state, options, token);
                return counts.IncompatibleDefinitions == 0
                    ? state.Result(ProviderDiscoveryOutcome.Succeeded, true, [])
                    : state.Result(ProviderDiscoveryOutcome.Partial, true,
                        [new("ssis.discovery.definition-format-unsupported",
                            "The complete scan includes definitions with unrecognized formats.")]);
            }, timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // A failing DisposeAsync must not replace caller cancellation.
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception) when (timeout.IsCancellationRequested)
        {
            return Error("ssis.discovery.timeout", "SSIS discovery exceeded its configured time limit.",
                ProviderDiscoveryOutcome.Unavailable);
        }
        catch (SecretResolutionException)
        {
            return Error("ssis.discovery.secret-unavailable", "The provider credential is unavailable.");
        }
        catch (SsisDiscoverySourceChangedException)
        {
            return Error("ssis.discovery.source-changed", "The observed SSIS catalog changed during discovery.",
                ProviderDiscoveryOutcome.Unavailable);
        }
        catch (SsisProbeException exception)
        {
            if (exception.Kind == SsisFailureKind.CatalogSurfaceIncompatible)
                state.SourceCompatibility = ProviderDiscoverySourceCompatibility.Incompatible;
            var issue = FromFailure(exception.Kind);
            return Error(issue.Code, issue.Message,
                exception.Kind is SsisFailureKind.Timeout or SsisFailureKind.Unexpected
                    ? ProviderDiscoveryOutcome.Unavailable : ProviderDiscoveryOutcome.Failed);
        }
        catch (SqlException exception)
        {
            var kind = SsisSqlFailureClassifier.Classify(SsisSqlFailure.FromException(exception),
                exception.Number == 18456 ? SsisValidationStage.MasterConnection : SsisValidationStage.DiscoveryQuery);
            if (kind == SsisFailureKind.CatalogSurfaceIncompatible)
                state.SourceCompatibility = ProviderDiscoverySourceCompatibility.Incompatible;
            var issue = FromFailure(kind);
            return Error(issue.Code, issue.Message,
                kind is SsisFailureKind.Timeout or SsisFailureKind.Unexpected
                    ? ProviderDiscoveryOutcome.Unavailable : ProviderDiscoveryOutcome.Failed);
        }
        catch (Exception)
        {
            return Error("ssis.discovery.unexpected-failure", "SSIS discovery failed unexpectedly.",
                ProviderDiscoveryOutcome.Unavailable);
        }
    }

    private static ProviderDiscoveryIssue FromFailure(SsisFailureKind kind) => kind switch
    {
        SsisFailureKind.DnsResolutionFailed => new(
            "ssis.discovery.endpoint-dns-failed", "The SQL Server host could not be resolved."),
        SsisFailureKind.EndpointUnreachable => new(
            "ssis.discovery.endpoint-unreachable", "The SQL Server endpoint is unreachable."),
        SsisFailureKind.Timeout => new(
            "ssis.discovery.timeout", "SSIS discovery exceeded its configured time limit."),
        SsisFailureKind.TlsCertificateRejected => new(
            "ssis.discovery.tls-certificate-rejected", "The SQL Server certificate was rejected."),
        SsisFailureKind.AuthenticationRejected => new(
            "ssis.discovery.authentication-rejected", "SQL Server rejected the configured login."),
        SsisFailureKind.CatalogInaccessible => new(
            "ssis.discovery.catalog-inaccessible", "SSISDB is inaccessible to the configured login."),
        SsisFailureKind.PermissionDenied => new(
            "ssis.discovery.catalog-permission-denied", "The required public catalog surface is not permitted."),
        SsisFailureKind.CatalogSurfaceIncompatible => new(
            "ssis.discovery.catalog-surface-incompatible", "The required public catalog surface is incompatible."),
        _ => new("ssis.discovery.unexpected-failure", "SSIS discovery failed unexpectedly."),
    };

    private sealed class ScanState(IProviderDiscoverySink sink) : IProviderDiscoverySink
    {
        private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
        private long observations, candidates, incompatible;
        public ProviderDiscoverySourceCompatibility SourceCompatibility { get; set; }

        public async ValueTask AppendAsync(ProviderDiscoveryObservation observation, CancellationToken token)
        {
            await sink.AppendAsync(observation, token);
            observations = checked(observations + 1);
            if (observation.ExecutableCandidate is not null) candidates = checked(candidates + 1);
            if (SsisDiscoveryMapper.IsIncompatible(observation)) incompatible = checked(incompatible + 1);
        }

        public ProviderDiscoveryScanResult Result(ProviderDiscoveryOutcome outcome, bool completed,
            IReadOnlyList<ProviderDiscoveryIssue> issues) =>
            new(outcome, completed, SourceCompatibility, observations, candidates, incompatible,
                started, DateTimeOffset.UtcNow, issues);
    }
}
