using FlowPlane.Providers.Ssis.Configuration;

namespace FlowPlane.Providers.Ssis.Discovery;

internal interface ISsisDiscoveryQuery
{
    ValueTask<ISsisDiscoverySession> OpenSessionAsync(
        SsisProviderConfiguration configuration,
        ReadOnlyMemory<char> password,
        SsisDiscoveryOptions options,
        CancellationToken cancellationToken);
}

internal interface ISsisDiscoverySession : IAsyncDisposable
{
    IAsyncEnumerable<SsisProjectManifestEntry> ReadManifestAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<SsisDiscoveryRow> ReadProjectBatchAsync(
        IReadOnlyList<SsisProjectManifestEntry> projects,
        CancellationToken cancellationToken);
}

internal sealed record SsisProjectManifestEntry(
    long FolderId,
    string FolderName,
    long? ProjectId,
    string? ProjectName,
    int? ProjectFormatVersion,
    long? ObjectVersionLsn,
    DateTimeOffset? LastDeployedAt);

internal abstract record SsisDiscoveryRow(SsisProjectManifestEntry Project);

internal sealed record SsisDiscoveredPackage(
    SsisProjectManifestEntry Project,
    long PackageId,
    string PackageName,
    bool IsEntryPoint,
    int PackageFormatVersion,
    Guid PackageGuid,
    int PackageVersionMajor,
    int PackageVersionMinor,
    int PackageVersionBuild,
    Guid PackageVersionGuid) : SsisDiscoveryRow(Project);

internal sealed record SsisDiscoveredParameter(
    SsisProjectManifestEntry Project,
    SsisParameterScope Scope,
    string? PackageName,
    string Name,
    string DataType,
    bool IsRequired,
    bool IsSensitive,
    SsisParameterValueSource ValueSource,
    bool IsValueSet,
    string? ReferencedVariableName) : SsisDiscoveryRow(Project);

internal sealed record SsisDiscoveredEnvironmentReference(
    SsisProjectManifestEntry Project,
    long ReferenceId,
    SsisEnvironmentReferenceType ReferenceType,
    string? EnvironmentFolderName,
    string EnvironmentName) : SsisDiscoveryRow(Project);

internal sealed class SsisDiscoverySourceChangedException()
    : Exception("The observed SSIS catalog changed during discovery.");
