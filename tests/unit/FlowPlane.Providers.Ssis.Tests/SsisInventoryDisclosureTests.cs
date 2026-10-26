using System.Text.Json;

using FlowPlane.Application.Providers.DiscoveryInventory;
using FlowPlane.Application.Providers;
using FlowPlane.Application.Secrets;
using FlowPlane.Domain.Providers;
using FlowPlane.Providers.Ssis.Configuration;
using FlowPlane.Providers.Ssis.Discovery;
using FlowPlane.Providers.Ssis.Validation;

using Microsoft.Data.SqlClient;

namespace FlowPlane.Providers.Ssis.Tests;

public sealed class SsisInventoryDisclosureTests
{
    [Fact]
    public void Projection_preserves_exact_identity_and_writes_int64_as_decimal_strings()
    {
        var project = DiscoveryTestData.Project(long.MaxValue) with { FolderName = "项目", ProjectName = "é/Project", ObjectVersionLsn = long.MaxValue };
        var observation = SsisDiscoveryMapper.Map(DiscoveryTestData.InstanceId, DiscoveryTestData.Package(project));
        var stored = new StoredDiscoveryObservation(observation.NativeKey, observation.Metadata.SchemaId,
            DiscoveryObservationVisibility.Observed, observation.Metadata, observation.ExecutableCandidate, DateTimeOffset.UtcNow);
        var item = SsisInventoryDisclosure.ProjectObservation(DiscoveryTestData.InstanceId, stored);
        Assert.Equal("9223372036854775807", item.Metadata.GetProperty("projectObjectVersionLsn").GetString());
        Assert.Equal("项目", item.Metadata.GetProperty("folderName").GetString());
        Assert.Equal("é/Project", item.ExecutableReference!.Data.GetProperty("projectName").GetString());
        Assert.Equal(JsonDocument.Parse(observation.NativeKey).RootElement.GetRawText(), item.NativeIdentity.GetRawText());
    }

    [Fact]
    public void Changed_metadata_or_native_identity_is_not_disclosed()
    {
        var row = SsisDiscoveryMapper.Map(DiscoveryTestData.InstanceId, DiscoveryTestData.Package(DiscoveryTestData.Project()));
        var stored = new StoredDiscoveryObservation(row.NativeKey, row.Metadata.SchemaId, DiscoveryObservationVisibility.Observed,
            row.Metadata, row.ExecutableCandidate, DateTimeOffset.UtcNow);
        Assert.True(SsisInventoryDisclosure.Matches(stored, row));
        var sameValues = SsisDiscoveryMapper.Map(DiscoveryTestData.InstanceId, DiscoveryTestData.Package(DiscoveryTestData.Project()));
        Assert.True(SsisInventoryDisclosure.Matches(stored, sameValues));
        var changed = SsisDiscoveryMapper.Map(DiscoveryTestData.InstanceId,
            DiscoveryTestData.Package(DiscoveryTestData.Project() with { ObjectVersionLsn = 999 }));
        Assert.False(SsisInventoryDisclosure.Matches(stored, changed));
        Assert.False(SsisInventoryDisclosure.Matches(stored with { NativeKey = "changed" }, row));
    }

    [Theory]
    [InlineData(1, 8, true, true)]
    [InlineData(99, 8, true, false)]
    [InlineData(1, 99, true, false)]
    [InlineData(1, 8, false, false)]
    public void Unknown_formats_and_non_entry_points_remain_observable_but_not_candidates(int projectFormat, int packageFormat, bool entry, bool candidate)
    {
        var row = SsisDiscoveryMapper.Map(DiscoveryTestData.InstanceId, DiscoveryTestData.Package(DiscoveryTestData.Project(format: projectFormat)) with { PackageFormatVersion = packageFormat, IsEntryPoint = entry });
        var result = SsisInventoryDisclosure.ProjectObservation(DiscoveryTestData.InstanceId, Stored(row));
        Assert.Equal(candidate, result.ExecutableReference is not null);
        Assert.Equal(packageFormat, result.Metadata.GetProperty("packageFormatVersion").GetInt32());
    }

    [Fact]
    public void Parameter_and_reference_metadata_changes_are_detected_without_revision_changes()
    {
        var project = DiscoveryTestData.Project();
        var parameter = new SsisDiscoveredParameter(project, SsisParameterScope.Project, null, "Sensitive", "String", true, true, SsisParameterValueSource.EnvironmentReference, true, "Variable");
        var first = SsisDiscoveryMapper.Map(DiscoveryTestData.InstanceId, parameter);
        foreach (var changed in new[] { parameter with { IsSensitive = false }, parameter with { IsRequired = false }, parameter with { ReferencedVariableName = "Other" }, parameter with { ValueSource = SsisParameterValueSource.Literal } })
            Assert.False(SsisInventoryDisclosure.Matches(Stored(first), SsisDiscoveryMapper.Map(DiscoveryTestData.InstanceId, changed)));
        var environment = new SsisDiscoveredEnvironmentReference(project, 1, SsisEnvironmentReferenceType.Relative, null, "Environment");
        var reference = SsisDiscoveryMapper.Map(DiscoveryTestData.InstanceId, environment);
        Assert.False(SsisInventoryDisclosure.Matches(Stored(reference), SsisDiscoveryMapper.Map(DiscoveryTestData.InstanceId, environment with { EnvironmentName = "environment" })));
    }

    [Theory]
    [InlineData("folder")]
    [InlineData("project")]
    [InlineData("package")]
    [InlineData("parameter")]
    [InlineData("environment")]
    public void Lookup_SQL_is_targeted_parameterized_and_never_selects_values(string kind)
    {
        SsisDiscoveryMetadata metadata = kind switch
        {
            "folder" => new SsisFolderMetadata(1, "项目'--"),
            "project" => new SsisProjectMetadata(1, "项目'--", "Project", 1, 1, DateTimeOffset.UtcNow, SsisDefinitionCompatibility.Recognized),
            "package" => new SsisPackageMetadata(1, "项目'--", "Project", "Package", true, 1, 8, 1, DateTimeOffset.UtcNow, Guid.NewGuid(), 1, 0, 0, Guid.NewGuid(), SsisDefinitionCompatibility.Recognized),
            "parameter" => new SsisParameterMetadata("项目'--", "Project", SsisParameterScope.Package, "Package", "Parameter", "String", true, true, SsisParameterValueSource.Literal, true, null),
            _ => new SsisEnvironmentReferenceMetadata("项目'--", "Project", 1, SsisEnvironmentReferenceType.Relative, null, "Environment"),
        };
        using var connection = new SqlConnection();
        using var command = SsisInventoryQuery.CreateCommand(connection, metadata, 10);
        Assert.DoesNotContain("项目", command.CommandText, StringComparison.Ordinal);
        Assert.Equal("项目'--", command.Parameters["@folder"].Value);
        Assert.Contains("TOP (2)", command.CommandText, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "default_value", "design_default_value", "environment_variables", "execution_parameter_values", "SELECT *", "object_versions" })
            Assert.DoesNotContain(forbidden, command.CommandText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Context_is_checked_after_password_resolution_before_any_native_IO()
    {
        var native = new Native();
        var resolver = new Resolver { BeforeUse = () => native.Current = false };
        var capability = Create(native, resolver);
        var error = await Assert.ThrowsAsync<InventoryBrowseException>(() => capability.VerifyAsync(Instance(), [], _ => Task.FromResult(native.Current), TestContext.Current.CancellationToken));
        Assert.Equal(InventoryBrowseError.ContextChanged, error.Code);
        Assert.Equal(0, native.MasterCalls);
        Assert.Equal(0, native.OpenCalls);
    }

    [Fact]
    public async Task Revoked_visibility_and_changed_revision_fail_closed()
    {
        var original = SsisDiscoveryMapper.Map(DiscoveryTestData.InstanceId, DiscoveryTestData.Package(DiscoveryTestData.Project()));
        foreach (var actual in new ProviderDiscoveryObservation?[] { null, SsisDiscoveryMapper.Map(DiscoveryTestData.InstanceId, DiscoveryTestData.Package(DiscoveryTestData.Project() with { ObjectVersionLsn = 999 })) })
        {
            var native = new Native { Actual = actual };
            var error = await Assert.ThrowsAsync<InventoryBrowseException>(() => Create(native).VerifyAsync(Instance(), [Stored(original)], _ => Task.FromResult(true), TestContext.Current.CancellationToken));
            Assert.Equal(InventoryBrowseError.NativeChanged, error.Code);
        }
    }

    [Theory]
    [InlineData("15.0.1.1")]
    [InlineData("16.0.1.1")]
    [InlineData("14.0.1.1")]
    public async Task Unverified_or_unsupported_server_never_discloses_inventory(string version)
    {
        var native = new Native { Version = version };
        var error = await Assert.ThrowsAsync<InventoryBrowseException>(() => Create(native).VerifyAsync(Instance(), [], _ => Task.FromResult(true), TestContext.Current.CancellationToken));
        Assert.Equal(InventoryBrowseError.NativeUnavailable, error.Code);
        Assert.Equal(0, native.OpenCalls);
    }

    [Theory]
    [InlineData("AuthenticationRejected")]
    [InlineData("TlsCertificateRejected")]
    [InlineData("EndpointUnreachable")]
    [InlineData("PermissionDenied")]
    public async Task Native_failures_are_bounded_and_sanitized(string kind)
    {
        var failure = Enum.Parse<SsisFailureKind>(kind);
        var native = new Native { Error = new SsisProbeException(failure, SsisValidationStage.MasterConnection) };
        var error = await Assert.ThrowsAsync<InventoryBrowseException>(() => Create(native).VerifyAsync(Instance(), [], _ => Task.FromResult(true), TestContext.Current.CancellationToken));
        Assert.Equal(failure == SsisFailureKind.PermissionDenied ? InventoryBrowseError.NativeDenied : InventoryBrowseError.NativeUnavailable, error.Code);
        Assert.DoesNotContain("synthetic-disclosure", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("LOCATOR", error.ToString(), StringComparison.Ordinal);
    }

    private static StoredDiscoveryObservation Stored(ProviderDiscoveryObservation row) => new(row.NativeKey, row.Metadata.SchemaId, DiscoveryObservationVisibility.Observed, row.Metadata, row.ExecutableCandidate, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Parent_checks_bracket_child_reads_even_when_children_are_selected_first()
    {
        var project = DiscoveryTestData.Project();
        var rows = new[] {
            SsisDiscoveryMapper.Map(DiscoveryTestData.InstanceId, DiscoveryTestData.Package(project)),
            SsisDiscoveryMapper.MapProject(DiscoveryTestData.InstanceId, project),
        };
        var native = new Native { ActualByKey = rows.ToDictionary(row => row.NativeKey, StringComparer.Ordinal) };
        await Create(native).VerifyAsync(Instance(), rows.Select(Stored).ToArray(), _ => Task.FromResult(true), TestContext.Current.CancellationToken);
        Assert.Equal(new[] { SsisDiscoveryMetadataCodec.ProjectSchemaId, SsisDiscoveryMetadataCodec.PackageSchemaId, SsisDiscoveryMetadataCodec.ProjectSchemaId }, native.ReadSchemas);
    }

    [Theory]
    [InlineData("invisible-before", InventoryBrowseError.NativeChanged)]
    [InlineData("revision-after", InventoryBrowseError.NativeChanged)]
    [InlineData("invisible-after", InventoryBrowseError.NativeChanged)]
    [InlineData("metadata-before", InventoryBrowseError.NativeChanged)]
    [InlineData("AuthenticationRejected", InventoryBrowseError.NativeUnavailable)]
    [InlineData("TlsCertificateRejected", InventoryBrowseError.NativeUnavailable)]
    [InlineData("EndpointUnreachable", InventoryBrowseError.NativeUnavailable)]
    [InlineData("PermissionDenied", InventoryBrowseError.NativeDenied)]
    public async Task Separately_fetched_parent_failure_rejects_the_entire_child_page(string scenario, InventoryBrowseError expected)
    {
        var project = DiscoveryTestData.Project();
        var child = SsisDiscoveryMapper.Map(DiscoveryTestData.InstanceId, DiscoveryTestData.Package(project));
        var parent = SsisDiscoveryMapper.MapProject(DiscoveryTestData.InstanceId, project);
        var folder = SsisDiscoveryMapper.MapFolder(DiscoveryTestData.InstanceId, project);
        var store = new ChildPageStore(Stored(child), [Stored(folder), Stored(parent)]);
        var parentReads = 0;
        var childRead = false;
        var native = new Native
        {
            OnRead = metadata =>
            {
                if (metadata is SsisFolderMetadata) return folder;
                if (metadata is SsisPackageMetadata) { childRead = true; return child; }
                Assert.IsType<SsisProjectMetadata>(metadata);
                parentReads++;
                if (Enum.TryParse<SsisFailureKind>(scenario, out var failure))
                    throw new SsisProbeException(failure, SsisValidationStage.CatalogConnection);
                return scenario switch
                {
                    "invisible-before" => null,
                    "invisible-after" when parentReads == 2 => null,
                    "revision-after" when parentReads == 2 => SsisDiscoveryMapper.MapProject(DiscoveryTestData.InstanceId, project with { ObjectVersionLsn = 999 }),
                    "metadata-before" => SsisDiscoveryMapper.MapProject(DiscoveryTestData.InstanceId, project with { LastDeployedAt = project.LastDeployedAt!.Value.AddSeconds(1) }),
                    _ => parent,
                };
            },
        };
        var browsing = new InventoryBrowsingService(store, new NoContinuation());
        InventoryBrowseResult? disclosed = null;
        var error = await Assert.ThrowsAsync<InventoryBrowseException>(async () =>
            disclosed = await browsing.ReadAsync(Instance(), new(SsisDiscoveryMetadataCodec.PackageSchemaId), null,
                Create(native), _ => Task.FromResult(true), TestContext.Current.CancellationToken));
        Assert.Equal(expected, error.Code);
        Assert.Null(disclosed); // No items or continuation escape, even after child retrieval.
        Assert.True(store.ReaderDisposed);
        Assert.Equal(scenario is "revision-after" or "invisible-after", childRead);
        Assert.DoesNotContain("synthetic-disclosure", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC_LOCATOR", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Native_disclosure_timeout_is_safe_and_caller_cancellation_propagates()
    {
        var native = new Native { Delay = true };
        var capability = new SsisInventoryDisclosure(new(), new Resolver(), native, native, new(false), new(1, 1, 1));
        var error = await Assert.ThrowsAsync<InventoryBrowseException>(() => capability.VerifyAsync(Instance(), [], _ => Task.FromResult(true), TestContext.Current.CancellationToken));
        Assert.Equal(InventoryBrowseError.NativeUnavailable, error.Code);
        Assert.Equal(1, native.OpenCalls);
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capability.VerifyAsync(Instance(), [], _ => Task.FromResult(true), caller.Token));
    }
    private static ProviderInstance Instance() => ProviderInstance.Create(DiscoveryTestData.InstanceId, ProviderType.Parse("ssis"), "Fixture", true,
        new SsisProviderConfigurationCodec().Encode(new("sql.example.test", 1433, "synthetic", false)), SecretReference.Environment("SYNTHETIC_LOCATOR"), DateTimeOffset.UtcNow);
    private static SsisInventoryDisclosure Create(Native native, Resolver? resolver = null) => new(new(), resolver ?? new(), native, native, new(false), SsisValidationOptions.Default);
    private sealed class Resolver : ISecretResolver
    {
        public Action? BeforeUse { get; init; }
        public async ValueTask<T> UseAsync<T>(SecretReference reference, Func<ReadOnlyMemory<char>, CancellationToken, ValueTask<T>> use, CancellationToken token = default)
        { BeforeUse?.Invoke(); return await use("synthetic-disclosure".AsMemory(), token); }
    }
    private sealed class ChildPageStore(StoredDiscoveryObservation child, IReadOnlyList<StoredDiscoveryObservation> parents)
        : IProviderInventoryPageStore, IProviderInventoryPageReader
    {
        public bool ReaderDisposed { get; private set; }
        public InventoryReadContext Context { get; } = new(DiscoveryTestData.InstanceId, Guid.NewGuid(), 1, 1, null, Guid.NewGuid());
        public Task<IProviderInventoryPageReader> OpenPageReaderAsync(Guid providerId, long version, Guid? generationId, CancellationToken token) => Task.FromResult<IProviderInventoryPageReader>(this);
        public Task<bool> IsCurrentAsync(InventoryReadContext context, CancellationToken token) => Task.FromResult(true);
        public Task<InventoryStoredPage> ReadPageAsync(InventoryPageQuery query, IReadOnlyList<string> schemas, string? after, CancellationToken token) => Task.FromResult(new InventoryStoredPage([child], null));
        public Task<IReadOnlyList<StoredDiscoveryObservation>> ReadScopeAsync(IReadOnlyList<string> keys, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<StoredDiscoveryObservation>>(parents.Where(row => keys.Contains(row.NativeKey, StringComparer.Ordinal)).ToArray());
        public ValueTask DisposeAsync() { ReaderDisposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class NoContinuation : IInventoryCursorCodec
    {
        public string Encode(InventoryCursor cursor) => throw new NotSupportedException();
        public InventoryCursor Decode(string cursor) => throw new NotSupportedException();
    }
    private sealed class Native : ISsisSqlExecutor, ISsisInventoryQuery, ISsisInventorySession
    {
        public bool Current { get; set; } = true;
        public int MasterCalls { get; private set; }
        public int OpenCalls { get; private set; }
        public string Version { get; init; } = "17.0.1000.7";
        public Exception? Error { get; init; }
        public ProviderDiscoveryObservation? Actual { get; init; }
        public Func<SsisDiscoveryMetadata, ProviderDiscoveryObservation?>? OnRead { get; init; }
        public IReadOnlyDictionary<string, ProviderDiscoveryObservation>? ActualByKey { get; init; }
        public List<string> ReadSchemas { get; } = [];
        public bool Delay { get; init; }
        public Task<SsisMasterFacts> ReadMasterFactsAsync(SsisProviderConfiguration configuration, ReadOnlyMemory<char> password, SsisValidationOptions options, CancellationToken token)
        { MasterCalls++; return Error is null ? Task.FromResult(new SsisMasterFacts(Version, "RTM", "Developer", 1)) : Task.FromException<SsisMasterFacts>(Error); }
        public Task<SsisCatalogEvidence> ReadCatalogEvidenceAsync(SsisProviderConfiguration configuration, ReadOnlyMemory<char> password, SsisValidationOptions options, CancellationToken token) => throw new NotSupportedException();
        public async ValueTask<ISsisInventorySession> OpenAsync(SsisProviderConfiguration configuration, ReadOnlyMemory<char> password, SsisValidationOptions options, CancellationToken token)
        { OpenCalls++; if (Delay) await Task.Delay(Timeout.InfiniteTimeSpan, token); return this; }
        public Task<ProviderDiscoveryObservation?> ReadAsync(Guid provider, SsisDiscoveryMetadata metadata, CancellationToken token)
        {
            var document = new SsisDiscoveryMetadataCodec().Encode(metadata);
            ReadSchemas.Add(document.SchemaId);
            if (OnRead is not null) return Task.FromResult(OnRead(metadata));
            return Task.FromResult(ActualByKey?.Values.Single(row => row.Metadata.CanonicalJson == document.CanonicalJson) ?? Actual);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
