using FlowPlane.Providers.Ssis.Configuration;

namespace FlowPlane.Providers.Ssis.Validation;

internal sealed record SsisMasterFacts(
    string ProductVersion,
    string ProductLevel,
    string Edition,
    int? HasSsisDbAccess);

internal sealed record SsisCatalogEvidence(
    SsisPermissionEvidence Permissions,
    IReadOnlyCollection<int> ProjectFormatVersions,
    IReadOnlyCollection<int> PackageFormatVersions);

internal interface ISsisSqlExecutor
{
    Task<SsisMasterFacts> ReadMasterFactsAsync(
        SsisProviderConfiguration configuration,
        ReadOnlyMemory<char> password,
        SsisValidationOptions options,
        CancellationToken cancellationToken);

    Task<SsisCatalogEvidence> ReadCatalogEvidenceAsync(
        SsisProviderConfiguration configuration,
        ReadOnlyMemory<char> password,
        SsisValidationOptions options,
        CancellationToken cancellationToken);
}

internal sealed class SsisProbeException(
    SsisFailureKind kind,
    SsisValidationStage stage) : Exception("The SSIS validation probe failed.")
{
    public SsisFailureKind Kind { get; } = kind;

    public SsisValidationStage Stage { get; } = stage;
}
