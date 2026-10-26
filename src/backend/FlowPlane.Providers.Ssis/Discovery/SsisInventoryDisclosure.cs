using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using FlowPlane.Application.Providers;
using FlowPlane.Application.Providers.DiscoveryInventory;
using FlowPlane.Application.Secrets;
using FlowPlane.Domain.Providers;
using FlowPlane.Providers.Ssis.Configuration;
using FlowPlane.Providers.Ssis.Validation;

using Microsoft.Data.SqlClient;

namespace FlowPlane.Providers.Ssis.Discovery;

internal interface ISsisInventoryQuery
{
    ValueTask<ISsisInventorySession> OpenAsync(SsisProviderConfiguration configuration,
        ReadOnlyMemory<char> password, SsisValidationOptions options, CancellationToken token);
}

internal interface ISsisInventorySession : IAsyncDisposable
{
    Task<ProviderDiscoveryObservation?> ReadAsync(Guid providerId, SsisDiscoveryMetadata expected, CancellationToken token);
}

internal sealed class SsisInventoryDisclosure(SsisProviderConfigurationCodec codec, ISecretResolver secrets,
    ISsisSqlExecutor server, ISsisInventoryQuery query, SsisValidationSecurityPolicy security,
    SsisValidationOptions options) : IProviderInventoryDisclosure
{
    private static readonly SsisDiscoveryMetadataCodec Metadata = new();
    private static readonly JsonSerializerOptions Output = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase), new DecimalInt64Converter() },
    };
    public ProviderType ProviderType => SsisProviderConfigurationCodec.SsisProviderType;
    public IReadOnlyList<string> SchemaIds { get; } = [SsisDiscoveryMetadataCodec.FolderSchemaId,
        SsisDiscoveryMetadataCodec.ProjectSchemaId,SsisDiscoveryMetadataCodec.PackageSchemaId,
        SsisDiscoveryMetadataCodec.ParameterSchemaId,SsisDiscoveryMetadataCodec.EnvironmentSchemaId];

    public IReadOnlyList<string> RequiredScopeKeys(IReadOnlyList<StoredDiscoveryObservation> observations)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in observations)
        {
            var metadata = Decode(row);
            var path = metadata switch
            {
                SsisFolderMetadata folder => (folder.FolderName, (string?)null),
                SsisProjectMetadata project => (project.FolderName, project.ProjectName),
                SsisPackageMetadata package => (package.FolderName, package.ProjectName),
                SsisParameterMetadata parameter => (parameter.FolderName, parameter.ProjectName),
                SsisEnvironmentReferenceMetadata environment => (environment.FolderName, environment.ProjectName),
                _ => throw Changed(),
            };
            if (metadata is not SsisFolderMetadata) keys.Add(JsonSerializer.Serialize(new[] { "folder", path.Item1 }));
            if (path.Item2 is not null && metadata is not SsisProjectMetadata)
                keys.Add(JsonSerializer.Serialize(new[] { "project", path.Item1, path.Item2 }));
            if (metadata is SsisParameterMetadata { Scope: SsisParameterScope.Package } parameterMetadata)
                keys.Add(JsonSerializer.Serialize(new[] { "package", path.Item1, path.Item2, parameterMetadata.PackageName }));
        }
        keys.ExceptWith(observations.Select(row => row.NativeKey));
        return keys.ToArray();
    }

    public async Task VerifyAsync(ProviderInstance provider, IReadOnlyList<StoredDiscoveryObservation> observations,
        Func<CancellationToken, Task<bool>> isCurrent, CancellationToken token)
    {
        if (!provider.IsEnabled || !provider.ProviderType.Equals(ProviderType)) throw new InventoryBrowseException(InventoryBrowseError.ProviderUnavailable);
        var configuration = codec.Decode(provider.Configuration);
        if (!security.Allows(configuration)) throw new InventoryBrowseException(InventoryBrowseError.NativeDenied);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.OverallTimeoutSeconds));
        try
        {
            await secrets.UseAsync(provider.SecretReference, async (password, ct) =>
            {
                if (!await isCurrent(ct)) throw new InventoryBrowseException(InventoryBrowseError.ContextChanged);
                var facts = await server.ReadMasterFactsAsync(configuration, password, options, ct);
                if (SsisCompatibilityPolicy.EvaluateServerVersion(facts.ProductVersion) != SsisServerVersionCompatibility.Supported || facts.HasSsisDbAccess != 1)
                    throw new InventoryBrowseException(InventoryBrowseError.NativeUnavailable);
                await using var session = await query.OpenAsync(configuration, password, options, ct);
                // Recheck the bounded selected scopes, not a project or catalog enumeration.
                // Repeated parent checks detect revision changes around child reads, but do
                // not claim a transactionally atomic native/FlowPlane snapshot.
                var parents = observations.Where(row => row.SchemaId is SsisDiscoveryMetadataCodec.ProjectSchemaId or SsisDiscoveryMetadataCodec.FolderSchemaId).ToArray();
                var children = observations.Where(row => row.SchemaId is not (SsisDiscoveryMetadataCodec.ProjectSchemaId or SsisDiscoveryMetadataCodec.FolderSchemaId));
                foreach (var row in parents.Concat(children).Concat(parents))
                {
                    var actual = await session.ReadAsync(provider.Id, Decode(row), ct);
                    if (actual is null || !Matches(row, actual)) throw Changed();
                }
                return true;
            }, timeout.Token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        catch (InventoryBrowseException) { throw; }
        catch (SsisProbeException error) when (error.Kind == SsisFailureKind.PermissionDenied)
        { throw new InventoryBrowseException(InventoryBrowseError.NativeDenied); }
        catch (SqlException error) when (error.Number == 229)
        { throw new InventoryBrowseException(InventoryBrowseError.NativeDenied); }
        catch (Exception) { throw new InventoryBrowseException(InventoryBrowseError.NativeUnavailable); }
    }

    public InventoryItem Project(Guid providerId, StoredDiscoveryObservation observation) => ProjectObservation(providerId, observation);

    internal static InventoryItem ProjectObservation(Guid providerId, StoredDiscoveryObservation row)
    {
        var metadata = Decode(row);
        InventoryNativeReference? reference = null;
        if (row.ExecutableCandidate is { } candidate)
        {
            if (candidate.ProviderInstanceId != providerId) throw Changed();
            var identity = new SsisNativeDefinitionReferenceCodec().Decode(candidate);
            if (metadata is not SsisPackageMetadata { IsEntryPoint: true, Compatibility: SsisDefinitionCompatibility.Recognized } package ||
                identity != new SsisNativeDefinitionIdentity(package.FolderName, package.ProjectName, package.PackageName)) throw Changed();
            reference = new(providerId, "ssis", candidate.SchemaId, JsonSerializer.SerializeToElement(identity, Output));
        }
        using var identityDocument = JsonDocument.Parse(row.NativeKey);
        return new(row.SchemaId, identityDocument.RootElement.Clone(),
            JsonSerializer.SerializeToElement(metadata, metadata.GetType(), Output), reference);
    }

    internal static bool Matches(StoredDiscoveryObservation expected, ProviderDiscoveryObservation actual) =>
        string.Equals(expected.NativeKey, actual.NativeKey, StringComparison.Ordinal) &&
        expected.Metadata?.SchemaId == actual.Metadata.SchemaId &&
        string.Equals(expected.Metadata?.CanonicalJson, actual.Metadata.CanonicalJson, StringComparison.Ordinal) &&
        Equals(expected.ExecutableCandidate, actual.ExecutableCandidate);

    private static SsisDiscoveryMetadata Decode(StoredDiscoveryObservation row)
    {
        if (row.Visibility != DiscoveryObservationVisibility.Observed || row.Metadata is null || row.SchemaId != row.Metadata.SchemaId) throw Changed();
        try { return Metadata.Decode(row.Metadata); }
        catch (Exception) { throw Changed(); }
    }

    private static InventoryBrowseException Changed() => new(InventoryBrowseError.NativeChanged);

    private sealed class DecimalInt64Converter : JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }
}
