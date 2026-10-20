using System.Runtime.CompilerServices;

using FlowPlane.Application.Providers;
using FlowPlane.Providers.Ssis.Discovery;

namespace FlowPlane.Providers.Ssis.Tests;

public sealed class SsisDiscoveryScannerTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    public async Task Complete_scan_does_not_truncate_the_superseded_catalog_limits(int batchSize)
    {
        var session = new GeneratedSession(projects: 23, packages: 125, parameters: 225, references: 75);
        var counts = new Dictionary<string, long>();
        var sink = new CallbackSink((observation, _) =>
        {
            var schema = observation.Metadata.SchemaId;
            counts[schema] = counts.GetValueOrDefault(schema) + 1;
            return ValueTask.CompletedTask;
        });

        var result = await SsisDiscoveryScanner.ScanAsync(DiscoveryTestData.InstanceId, session, sink,
            new SsisDiscoveryOptions { MaxProjectsPerBatch = batchSize },
            TestContext.Current.CancellationToken);

        Assert.Equal(23 * 125, result.ExecutableCandidates);
        Assert.Equal(23 * 225, counts[SsisDiscoveryMetadataCodec.ParameterSchemaId]);
        Assert.Equal(23 * 75, counts[SsisDiscoveryMetadataCodec.EnvironmentSchemaId]);
        Assert.Equal(23, counts[SsisDiscoveryMetadataCodec.ProjectSchemaId]);
        Assert.Equal(1, counts[SsisDiscoveryMetadataCodec.FolderSchemaId]);
        Assert.All(session.BatchSizes, size => Assert.InRange(size, 1, batchSize));
        Assert.Equal(2 * (int)Math.Ceiling(23d / batchSize), session.BatchSizes.Count);
    }

    [Fact]
    public async Task One_large_project_streams_with_backpressure_before_reading_the_next_row()
    {
        var session = new GeneratedSession(projects: 1, packages: 20_000);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new CallbackSink(async (observation, token) =>
        {
            if (observation.ExecutableCandidate is not null && !entered.Task.IsCompleted)
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
            }
        });
        var scanning = SsisDiscoveryScanner.ScanAsync(DiscoveryTestData.InstanceId, session, sink,
            SsisDiscoveryOptions.Default, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, session.RowsRead);
        Assert.False(scanning.IsCompleted);
        release.SetResult();

        var result = await scanning;

        Assert.Equal(20_000, result.ExecutableCandidates);
        Assert.Equal(40_000, session.RowsRead); // read + verification, never a catalog collection
    }

    [Fact]
    public async Task Empty_folders_and_empty_catalogs_are_complete_observations()
    {
        var session = new GeneratedSession(0);
        session.ManifestOverride = [new(1, "Empty folder", null, null, null, null, null)];
        var sink = new CallbackSink((_, _) => ValueTask.CompletedTask);
        var result = await SsisDiscoveryScanner.ScanAsync(DiscoveryTestData.InstanceId, session,
            sink, SsisDiscoveryOptions.Default, TestContext.Current.CancellationToken);
        Assert.Equal(1, result.ObservationsDelivered);
        Assert.Equal(0, result.ExecutableCandidates);

        session.ManifestOverride = [];
        result = await SsisDiscoveryScanner.ScanAsync(DiscoveryTestData.InstanceId, session,
            sink, SsisDiscoveryOptions.Default, TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ObservationsDelivered);
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("metadata")]
    [InlineData("binding")]
    [InlineData("environment")]
    [InlineData("revision")]
    public async Task Source_changes_prevent_successful_scan_completion(string change)
    {
        var session = new GeneratedSession(1, parameters: 1, references: 1) { Change = change };
        await Assert.ThrowsAsync<SsisDiscoverySourceChangedException>(() =>
            SsisDiscoveryScanner.ScanAsync(DiscoveryTestData.InstanceId, session,
                new CallbackSink((_, _) => ValueTask.CompletedTask), SsisDiscoveryOptions.Default,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Incompatible_projects_and_non_entry_points_are_observed_without_hiding_candidates()
    {
        var session = new GeneratedSession(2, packages: 2)
        {
            ManifestOverride = [DiscoveryTestData.Project(1, 99), DiscoveryTestData.Project(2)],
            IncludeNonEntryPoints = true,
        };
        long packages = 0, nonEntryPoints = 0;
        var result = await SsisDiscoveryScanner.ScanAsync(DiscoveryTestData.InstanceId, session,
            new CallbackSink((observation, _) =>
            {
                if (observation.Metadata.SchemaId == SsisDiscoveryMetadataCodec.PackageSchemaId)
                {
                    packages++;
                    var metadata = (SsisPackageMetadata)new SsisDiscoveryMetadataCodec().Decode(observation.Metadata);
                    if (!metadata.IsEntryPoint) nonEntryPoints++;
                }
                return ValueTask.CompletedTask;
            }), new SsisDiscoveryOptions { MaxProjectsPerBatch = 1 },
            TestContext.Current.CancellationToken);
        Assert.Equal(4, packages);
        Assert.Equal(2, nonEntryPoints);
        Assert.Equal(1, result.ExecutableCandidates);
        Assert.Equal(3, result.IncompatibleDefinitions);
    }

    [Fact]
    public async Task Failing_sink_stops_reading_without_retries()
    {
        var session = new GeneratedSession(1, packages: 1000);
        await Assert.ThrowsAsync<IOException>(() => SsisDiscoveryScanner.ScanAsync(
            DiscoveryTestData.InstanceId, session, new CallbackSink((observation, _) =>
            {
                if (observation.ExecutableCandidate is not null)
                    throw new IOException("sink canary");
                return ValueTask.CompletedTask;
            }), SsisDiscoveryOptions.Default, TestContext.Current.CancellationToken));
        Assert.Equal(1, session.RowsRead);
        Assert.Single(session.BatchSizes);
    }

    internal sealed class CallbackSink(
        Func<ProviderDiscoveryObservation, CancellationToken, ValueTask> append) : IProviderDiscoverySink
    {
        public ValueTask AppendAsync(ProviderDiscoveryObservation observation, CancellationToken token) =>
            append(observation, token);
    }

    internal sealed class GeneratedSession(int projects, int packages = 1, int parameters = 0,
        int references = 0) : ISsisDiscoverySession
    {
        public List<int> BatchSizes { get; } = [];
        public long RowsRead { get; private set; }
        public string? Change { get; init; }
        public bool IncludeNonEntryPoints { get; init; }
        public SsisProjectManifestEntry[]? ManifestOverride { get; set; }
        private int manifestReads;
        private int batchReads;

        public async IAsyncEnumerable<SsisProjectManifestEntry> ReadManifestAsync(
            [EnumeratorCancellation] CancellationToken token)
        {
            manifestReads++;
            await Task.CompletedTask;
            var count = projects + (Change == "manifest" && manifestReads > 1 ? 1 : 0);
            foreach (var project in ManifestOverride ??
                     Enumerable.Range(1, count).Select(id => DiscoveryTestData.Project(id)).ToArray())
            {
                token.ThrowIfCancellationRequested();
                yield return project;
            }
        }

        public async IAsyncEnumerable<SsisDiscoveryRow> ReadProjectBatchAsync(
            IReadOnlyList<SsisProjectManifestEntry> batch,
            [EnumeratorCancellation] CancellationToken token)
        {
            BatchSizes.Add(batch.Count);
            batchReads++;
            await Task.CompletedTask;
            if (Change == "revision")
                throw new SsisDiscoverySourceChangedException();
            foreach (var project in batch)
            {
                for (var id = 1; id <= packages; id++)
                {
                    token.ThrowIfCancellationRequested();
                    RowsRead++;
                    yield return DiscoveryTestData.Package(project, id) with
                    {
                        IsEntryPoint = !IncludeNonEntryPoints || id == 1,
                        PackageFormatVersion = Change == "metadata" && batchReads % 2 == 0 ? 999 : 8,
                    };
                }
                for (var id = 1; id <= parameters; id++)
                    yield return new SsisDiscoveredParameter(project, SsisParameterScope.Project,
                        null, $"P{id}", "String", false, false, SsisParameterValueSource.Literal,
                        true, Change == "binding" && batchReads % 2 == 0 ? "ChangedReference" : null);
                for (var id = 1; id <= references; id++)
                    yield return new SsisDiscoveredEnvironmentReference(project, id,
                        SsisEnvironmentReferenceType.Relative, null,
                        Change == "environment" && batchReads % 2 == 0 ? $"Changed{id}" : $"E{id}");
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
