using FlowPlane.Application.Providers;
using FlowPlane.Providers.Ssis.Configuration;

namespace FlowPlane.Providers.Ssis.Validation;

internal sealed class SsisSqlValidationProbe(
    ISsisSqlExecutor sqlExecutor,
    SsisValidationOptions options) : ISsisValidationProbe
{
    public async Task<ProviderValidationResult> ValidateAsync(
        SsisProviderConfiguration configuration,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken)
    {
        SsisMasterFacts masterFacts;
        try
        {
            masterFacts = await sqlExecutor.ReadMasterFactsAsync(
                configuration,
                password,
                options,
                cancellationToken);
        }
        catch (SsisProbeException exception)
        {
            return MasterFailure(exception.Kind);
        }

        var checks = new List<ProviderValidationCheck>
        {
            Success("ssis.endpoint.reachable", "The SQL Server endpoint is reachable."),
            Success("ssis.tls.valid", "The encrypted SQL Server connection is trusted."),
            Success("ssis.authentication.accepted", "SQL Server accepted the configured login."),
        };
        var facts = CreateServerFacts(masterFacts);

        var versionCompatibility = SsisCompatibilityPolicy.EvaluateServerVersion(
            masterFacts.ProductVersion);
        checks.Add(versionCompatibility switch
        {
            SsisServerVersionCompatibility.Supported => Success(
                "ssis.server-version.supported",
                "The SQL Server major version has accepted compatibility evidence."),
            SsisServerVersionCompatibility.Unverified => Failed(
                "ssis.server-version.unverified",
                "The SQL Server major version is recognized, but compatibility evidence has not been accepted."),
            _ => Failed(
                "ssis.server-version.unsupported",
                "The SQL Server major version is unsupported."),
        });

        var catalogAccess = SsisCatalogAccessMapper.Map(masterFacts.HasSsisDbAccess);
        checks.Add(catalogAccess switch
        {
            SsisCatalogAccess.Available => Success(
                "ssis.catalog.available",
                "SSISDB exists and is accessible."),
            SsisCatalogAccess.Missing => Failed(
                "ssis.catalog.missing",
                "SSISDB does not exist on the configured SQL Server."),
            SsisCatalogAccess.InaccessibleOrUnavailable => Failed(
                "ssis.catalog.inaccessible",
                "SSISDB is inaccessible or unavailable."),
            _ => Failed(
                "ssis.catalog.unavailable",
                "SSISDB availability could not be determined."),
        });

        if (versionCompatibility == SsisServerVersionCompatibility.Unsupported ||
            catalogAccess != SsisCatalogAccess.Available)
        {
            checks.AddRange(BlockedAfterMasterFacts());
            return new ProviderValidationResult(
                ProviderValidationOutcome.Failed,
                checks,
                UnknownCapabilities("ssis.validation.prerequisite-failed"),
                facts);
        }

        SsisCatalogEvidence evidence;
        try
        {
            evidence = await sqlExecutor.ReadCatalogEvidenceAsync(
                configuration,
                password,
                options,
                cancellationToken);
        }
        catch (SsisProbeException exception)
        {
            return CatalogFailure(checks, facts, exception);
        }

        checks.Add(Success(
            "ssis.catalog.surface-compatible",
            "The required public SSISDB catalog surface is compatible."));

        var hasUnknownProjectFormat = SsisCompatibilityPolicy.HasUnknownProjectFormat(
            evidence.ProjectFormatVersions);
        var hasUnknownPackageFormat = SsisCompatibilityPolicy.HasUnknownPackageFormat(
            evidence.PackageFormatVersions);
        var unknownFormat = hasUnknownProjectFormat || hasUnknownPackageFormat;
        var permissionEvidence = evidence.Permissions with
        {
            HasUnknownDefinitionFormat = unknownFormat,
        };

        checks.Add(unknownFormat
            ? Failed(
                "ssis.definition-format.unsupported",
                "At least one visible definition uses an unrecognized format version.")
            : Success(
                "ssis.definition-format.supported",
                "No unsupported visible definition format was observed."));
        checks.Add(PermissionCheck(
            "discovery",
            permissionEvidence.HasReadableScope,
            permissionEvidence.IsReadExplicitlyDenied));
        checks.Add(ExecutionPermissionCheck(permissionEvidence));

        facts.Add(new ProviderValidationFact(
            "ssis.project-format.observed",
            FormatVersions(evidence.ProjectFormatVersions)));
        facts.Add(new ProviderValidationFact(
            "ssis.package-format.observed",
            FormatVersions(evidence.PackageFormatVersions)));

        return new ProviderValidationResult(
            versionCompatibility == SsisServerVersionCompatibility.Supported
                ? ProviderValidationOutcome.Succeeded
                : ProviderValidationOutcome.Failed,
            checks,
            SsisCapabilityMapper.Map(permissionEvidence),
            facts);
    }

    private static ProviderValidationResult MasterFailure(SsisFailureKind kind)
    {
        var checks = new List<ProviderValidationCheck>();
        switch (kind)
        {
            case SsisFailureKind.DnsResolutionFailed:
                checks.Add(Failed("ssis.endpoint.dns-failed", "The SQL Server host could not be resolved."));
                checks.AddRange(BlockedConnectionChecks(includeEndpoint: false));
                break;
            case SsisFailureKind.EndpointUnreachable:
                checks.Add(Failed("ssis.endpoint.unreachable", "The SQL Server endpoint is unreachable."));
                checks.AddRange(BlockedConnectionChecks(includeEndpoint: false));
                break;
            case SsisFailureKind.Timeout:
                checks.Add(Failed("ssis.endpoint.timeout", "The SQL Server connection timed out."));
                checks.AddRange(BlockedConnectionChecks(includeEndpoint: false));
                break;
            case SsisFailureKind.TlsCertificateRejected:
                checks.Add(Success("ssis.endpoint.reachable", "The SQL Server endpoint is reachable."));
                checks.Add(Failed("ssis.tls.certificate-rejected", "The SQL Server certificate was rejected."));
                checks.Add(Unavailable("ssis.authentication.accepted"));
                checks.AddRange(BlockedCatalogChecks());
                break;
            case SsisFailureKind.AuthenticationRejected:
                checks.Add(Success("ssis.endpoint.reachable", "The SQL Server endpoint is reachable."));
                checks.Add(Success("ssis.tls.valid", "The encrypted SQL Server connection is trusted."));
                checks.Add(Failed("ssis.authentication.rejected", "SQL Server rejected the configured login."));
                checks.AddRange(BlockedCatalogChecks());
                break;
            default:
                checks.Add(Unavailable("ssis.endpoint.reachable"));
                checks.Add(Failed("ssis.validation.unexpected-failure", "SQL Server validation failed unexpectedly."));
                checks.AddRange(BlockedConnectionChecks(includeEndpoint: false));
                break;
        }

        var outcome = kind is SsisFailureKind.Timeout or SsisFailureKind.Unexpected
            ? ProviderValidationOutcome.Unavailable
            : ProviderValidationOutcome.Failed;
        return new ProviderValidationResult(
            outcome,
            checks,
            UnknownCapabilities(FailureReason(kind)),
            []);
    }

    private static ProviderValidationResult CatalogFailure(
        List<ProviderValidationCheck> successfulChecks,
        List<ProviderValidationFact> facts,
        SsisProbeException exception)
    {
        if (exception.Kind == SsisFailureKind.PermissionDenied)
        {
            successfulChecks.Add(Failed(
                "ssis.catalog.permission-denied",
                "The login cannot query the required public SSISDB catalog surface."));
            successfulChecks.Add(Unavailable("ssis.permission.discovery"));
            successfulChecks.Add(Unavailable("ssis.permission.execution"));
            return new ProviderValidationResult(
                ProviderValidationOutcome.Failed,
                successfulChecks,
                [
                    new("discovery", ProviderCapabilityAvailability.Unavailable,
                        "ssis.catalog.permission-denied"),
                    new("execution", ProviderCapabilityAvailability.Unavailable,
                        "ssis.catalog.permission-denied"),
                ],
                facts);
        }

        successfulChecks.Add(Failed(
            exception.Kind == SsisFailureKind.CatalogInaccessible
                ? "ssis.catalog.inaccessible"
                : "ssis.catalog.unavailable",
            exception.Kind == SsisFailureKind.CatalogInaccessible
                ? "SSISDB is inaccessible to the configured login."
                : "SSISDB validation failed unexpectedly."));
        successfulChecks.AddRange(BlockedAfterMasterFacts());
        return new ProviderValidationResult(
            exception.Kind == SsisFailureKind.Unexpected
                ? ProviderValidationOutcome.Unavailable
                : ProviderValidationOutcome.Failed,
            successfulChecks,
            UnknownCapabilities(FailureReason(exception.Kind)),
            facts);
    }

    private static ProviderValidationCheck PermissionCheck(
        string capability,
        bool available,
        bool denied) => denied
        ? Failed($"ssis.permission.{capability}.denied", "Catalog permission is explicitly unavailable.")
        : available
            ? Success($"ssis.permission.{capability}.available", "Sufficient catalog permission evidence exists.")
            : Unavailable($"ssis.permission.{capability}.indeterminate");

    private static ProviderValidationCheck ExecutionPermissionCheck(SsisPermissionEvidence evidence)
    {
        if (evidence.HasExecutableReadableScope)
        {
            return Success(
                "ssis.permission.execution.available",
                "Sufficient catalog execution permission evidence exists.");
        }

        if (evidence.HasReadableScope && evidence.HasVisibleDefinitions)
        {
            return Failed(
                "ssis.permission.execution.absent",
                "Visible definitions do not have matching catalog execution permission evidence.");
        }

        return Unavailable("ssis.permission.execution.indeterminate");
    }

    private static List<ProviderValidationFact> CreateServerFacts(SsisMasterFacts facts)
    {
        var major = Version.TryParse(facts.ProductVersion, out var version)
            ? version.Major.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "unavailable";
        return
        [
            new ProviderValidationFact("ssis.sql-server.major-version", major),
            new ProviderValidationFact("ssis.sql-server.product-level", SafeFact(facts.ProductLevel)),
            new ProviderValidationFact("ssis.sql-server.edition", SafeFact(facts.Edition)),
        ];
    }

    private static string SafeFact(string value) =>
        value.Length is > 0 and <= 128 && value.All(character =>
            char.IsLetterOrDigit(character) || char.IsWhiteSpace(character) ||
            character is '.' or '-' or '_' or '(' or ')')
                ? value
                : "unavailable";

    private static string FormatVersions(IEnumerable<int> versions)
    {
        var values = versions.Distinct().Order().ToArray();
        return values.Length == 0
            ? "none"
            : string.Join(',', values);
    }

    private static ProviderValidationCheck[] BlockedConnectionChecks(bool includeEndpoint)
    {
        var codes = includeEndpoint
            ? new[] { "ssis.endpoint.reachable", "ssis.tls.valid", "ssis.authentication.accepted" }
            : new[] { "ssis.tls.valid", "ssis.authentication.accepted" };
        return [.. codes.Select(Unavailable), .. BlockedCatalogChecks()];
    }

    private static ProviderValidationCheck[] BlockedCatalogChecks() =>
    [
        Unavailable("ssis.catalog.available"),
        Unavailable("ssis.server-version.status"),
        Unavailable("ssis.catalog.surface-compatible"),
        Unavailable("ssis.permission.discovery"),
        Unavailable("ssis.permission.execution"),
    ];

    private static ProviderValidationCheck[] BlockedAfterMasterFacts() =>
    [
        Unavailable("ssis.catalog.surface-compatible"),
        Unavailable("ssis.permission.discovery"),
        Unavailable("ssis.permission.execution"),
    ];

    private static ProviderValidationCheck Success(string code, string message) =>
        new(code, ProviderValidationCheckStatus.Succeeded, message);

    private static ProviderValidationCheck Failed(string code, string message) =>
        new(code, ProviderValidationCheckStatus.Failed, message);

    private static ProviderValidationCheck Unavailable(string code) => new(
        code,
        ProviderValidationCheckStatus.Unavailable,
        "The check was not executed or could not be determined from available evidence.");

    private static ProviderCapabilityResult[] UnknownCapabilities(string reasonCode) =>
    [
        new("discovery", ProviderCapabilityAvailability.Unknown, reasonCode),
        new("execution", ProviderCapabilityAvailability.Unknown, reasonCode),
    ];

    private static string FailureReason(SsisFailureKind kind) => kind switch
    {
        SsisFailureKind.DnsResolutionFailed => "ssis.endpoint.dns-failed",
        SsisFailureKind.EndpointUnreachable => "ssis.endpoint.unreachable",
        SsisFailureKind.Timeout => "ssis.endpoint.timeout",
        SsisFailureKind.TlsCertificateRejected => "ssis.tls.certificate-rejected",
        SsisFailureKind.AuthenticationRejected => "ssis.authentication.rejected",
        SsisFailureKind.CatalogInaccessible => "ssis.catalog.inaccessible",
        SsisFailureKind.PermissionDenied => "ssis.catalog.permission-denied",
        _ => "ssis.validation.unexpected-failure",
    };
}
