using System.Text.Json;
using System.Text.Json.Serialization;

using FlowPlane.Application.Providers;

namespace FlowPlane.Providers.Ssis.Discovery;

internal enum SsisParameterScope { Project, Package }
internal enum SsisParameterValueSource { Literal, EnvironmentReference, Unknown }
internal enum SsisEnvironmentReferenceType { Relative, Absolute }
internal enum SsisDefinitionCompatibility
{
    Recognized,
    UnknownProjectFormat,
    UnknownPackageFormat,
    UnknownProjectAndPackageFormats,
}

internal abstract record SsisDiscoveryMetadata;

internal sealed record SsisFolderMetadata(long FolderId, string FolderName) : SsisDiscoveryMetadata;

internal sealed record SsisProjectMetadata(
    long ProjectId, string FolderName, string ProjectName, int ProjectFormatVersion,
    long ActiveObjectVersionLsn, DateTimeOffset LastDeployedAt,
    SsisDefinitionCompatibility Compatibility) : SsisDiscoveryMetadata;

internal sealed record SsisPackageMetadata(
    long PackageId, string FolderName, string ProjectName, string PackageName, bool IsEntryPoint,
    int ProjectFormatVersion, int PackageFormatVersion, long ProjectObjectVersionLsn,
    DateTimeOffset ProjectLastDeployedAt, Guid PackageGuid, int PackageVersionMajor,
    int PackageVersionMinor, int PackageVersionBuild, Guid PackageVersionGuid,
    SsisDefinitionCompatibility Compatibility) : SsisDiscoveryMetadata;

internal sealed record SsisParameterMetadata(
    string FolderName, string ProjectName, SsisParameterScope Scope, string? PackageName,
    string Name, string DataType, bool IsRequired, bool IsSensitive,
    SsisParameterValueSource ValueSource, bool IsValueSet,
    string? ReferencedVariableName) : SsisDiscoveryMetadata;

internal sealed record SsisEnvironmentReferenceMetadata(
    string FolderName, string ProjectName, long ReferenceId,
    SsisEnvironmentReferenceType ReferenceType, string? EnvironmentFolderName,
    string EnvironmentName) : SsisDiscoveryMetadata;

internal sealed class SsisDiscoveryMetadataCodec : ProviderDiscoveryMetadataCodec
{
    internal const string FolderSchemaId = "flowplane.ssis/discovery-folder.v1";
    internal const string ProjectSchemaId = "flowplane.ssis/discovery-project.v1";
    internal const string PackageSchemaId = "flowplane.ssis/discovery-package.v1";
    internal const string ParameterSchemaId = "flowplane.ssis/discovery-parameter.v1";
    internal const string EnvironmentSchemaId = "flowplane.ssis/discovery-environment-reference.v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public ProviderDiscoveryMetadataDocument Encode(SsisDiscoveryMetadata metadata)
    {
        var schema = metadata switch
        {
            SsisFolderMetadata => FolderSchemaId,
            SsisProjectMetadata => ProjectSchemaId,
            SsisPackageMetadata => PackageSchemaId,
            SsisParameterMetadata => ParameterSchemaId,
            SsisEnvironmentReferenceMetadata => EnvironmentSchemaId,
            _ => throw new ArgumentException("Unrecognized SSIS observation metadata.", nameof(metadata)),
        };
        return CreateValidatedDocument(schema,
            JsonSerializer.Serialize(metadata, metadata.GetType(), JsonOptions));
    }

    public SsisDiscoveryMetadata Decode(ProviderDiscoveryMetadataDocument document)
    {
        var type = document.SchemaId switch
        {
            FolderSchemaId => typeof(SsisFolderMetadata),
            ProjectSchemaId => typeof(SsisProjectMetadata),
            PackageSchemaId => typeof(SsisPackageMetadata),
            ParameterSchemaId => typeof(SsisParameterMetadata),
            EnvironmentSchemaId => typeof(SsisEnvironmentReferenceMetadata),
            _ => throw new ArgumentException("Unrecognized SSIS observation schema.", nameof(document)),
        };
        return (SsisDiscoveryMetadata)(JsonSerializer.Deserialize(
            document.CanonicalJson, type, JsonOptions) ??
            throw new ArgumentException("Invalid SSIS observation metadata.", nameof(document)));
    }
}
