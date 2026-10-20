using System.Text.Json;

using FlowPlane.Application.Providers;
using FlowPlane.Providers.Ssis.Validation;

namespace FlowPlane.Providers.Ssis.Discovery;

internal static class SsisDiscoveryMapper
{
    private static readonly SsisDiscoveryMetadataCodec MetadataCodec = new();
    private static readonly SsisNativeDefinitionReferenceCodec ReferenceCodec = new();

    public static ProviderDiscoveryObservation MapFolder(Guid instanceId, SsisProjectManifestEntry entry) =>
        Create(instanceId, ["folder", entry.FolderName],
            new SsisFolderMetadata(entry.FolderId, entry.FolderName));

    public static ProviderDiscoveryObservation MapProject(Guid instanceId, SsisProjectManifestEntry entry) =>
        Create(instanceId, ["project", entry.FolderName, entry.ProjectName],
            new SsisProjectMetadata(entry.ProjectId!.Value, entry.FolderName, entry.ProjectName!,
                entry.ProjectFormatVersion!.Value, entry.ObjectVersionLsn!.Value,
                entry.LastDeployedAt!.Value,
                SsisCompatibilityPolicy.IsRecognizedProjectFormat(entry.ProjectFormatVersion.Value)
                    ? SsisDefinitionCompatibility.Recognized
                    : SsisDefinitionCompatibility.UnknownProjectFormat));

    public static ProviderDiscoveryObservation Map(Guid instanceId, SsisDiscoveryRow row)
    {
        var project = row.Project;
        return row switch
        {
            SsisDiscoveredPackage package => MapPackage(instanceId, package),
            SsisDiscoveredParameter parameter => Create(instanceId,
                ["parameter", project.FolderName, project.ProjectName,
                    parameter.Scope.ToString(), parameter.PackageName, parameter.Name],
                new SsisParameterMetadata(project.FolderName, project.ProjectName!,
                    parameter.Scope, parameter.PackageName, parameter.Name, parameter.DataType,
                    parameter.IsRequired, parameter.IsSensitive, parameter.ValueSource,
                    parameter.IsValueSet, parameter.ReferencedVariableName)),
            SsisDiscoveredEnvironmentReference environment => Create(instanceId,
                ["environment-reference", project.FolderName, project.ProjectName,
                    environment.ReferenceType.ToString(), environment.EnvironmentFolderName,
                    environment.EnvironmentName],
                new SsisEnvironmentReferenceMetadata(project.FolderName, project.ProjectName!,
                    environment.ReferenceId, environment.ReferenceType,
                    environment.EnvironmentFolderName, environment.EnvironmentName)),
            _ => throw new ArgumentException("Unrecognized SSIS discovery row.", nameof(row)),
        };
    }

    public static bool IsIncompatible(ProviderDiscoveryObservation observation) =>
        MetadataCodec.Decode(observation.Metadata) switch
        {
            SsisProjectMetadata project => project.Compatibility != SsisDefinitionCompatibility.Recognized,
            SsisPackageMetadata package => package.Compatibility != SsisDefinitionCompatibility.Recognized,
            _ => false,
        };

    private static ProviderDiscoveryObservation MapPackage(Guid instanceId, SsisDiscoveredPackage package)
    {
        var project = package.Project;
        var recognizedProject = SsisCompatibilityPolicy.IsRecognizedProjectFormat(
            project.ProjectFormatVersion!.Value);
        var recognizedPackage = SsisCompatibilityPolicy.IsRecognizedPackageFormat(
            package.PackageFormatVersion);
        var compatibility = (recognizedProject, recognizedPackage) switch
        {
            (true, true) => SsisDefinitionCompatibility.Recognized,
            (false, true) => SsisDefinitionCompatibility.UnknownProjectFormat,
            (true, false) => SsisDefinitionCompatibility.UnknownPackageFormat,
            _ => SsisDefinitionCompatibility.UnknownProjectAndPackageFormats,
        };
        var candidate = package.IsEntryPoint && recognizedProject && recognizedPackage
            ? ReferenceCodec.Encode(instanceId, new SsisNativeDefinitionIdentity(
                project.FolderName, project.ProjectName!, package.PackageName))
            : null;
        return Create(instanceId, ["package", project.FolderName, project.ProjectName, package.PackageName],
            new SsisPackageMetadata(package.PackageId, project.FolderName, project.ProjectName!,
                package.PackageName, package.IsEntryPoint, project.ProjectFormatVersion.Value,
                package.PackageFormatVersion, project.ObjectVersionLsn!.Value,
                project.LastDeployedAt!.Value, package.PackageGuid, package.PackageVersionMajor,
                package.PackageVersionMinor, package.PackageVersionBuild, package.PackageVersionGuid,
                compatibility), candidate);
    }

    private static ProviderDiscoveryObservation Create(Guid instanceId, string?[] key,
        SsisDiscoveryMetadata metadata, FlowPlane.Domain.Providers.NativeDefinitionReference? candidate = null) =>
        new(instanceId, JsonSerializer.Serialize(key), MetadataCodec.Encode(metadata), candidate);
}
