using System.Text.Json;

using FlowPlane.Application.Providers;
using FlowPlane.Application.Secrets;
using FlowPlane.Domain.Providers;
using FlowPlane.Providers.Ssis.Configuration;
using FlowPlane.Providers.Ssis.Discovery;
using FlowPlane.Providers.Ssis.Validation;

namespace FlowPlane.Providers.Ssis.Tests;

public sealed class SsisProviderDiscoveryCapabilityTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task Persisted_context_is_checked_after_secret_resolution_and_before_native_IO(bool current, int calls)
    {
        var resolver = new StubSecretResolver("synthetic-bounded-password");
        var sql = new StubSqlExecutor(new("17.0.1000.7", "RTM", "Developer", 1));
        var query = new StubDiscoveryQuery(new SsisDiscoveryScannerTests.GeneratedSession(1));
        var guard = new GuardedSink(() => { Assert.Equal(1, resolver.CallCount); return current; });
        var capability = new SsisProviderDiscoveryCapability(new(), resolver, sql, query, new(false), SsisDiscoveryOptions.Default);
        var result = await capability.ScanAsync(Instance(), guard, TestContext.Current.CancellationToken);
        Assert.Equal(calls, sql.MasterCalls);
        Assert.Equal(calls, query.CallCount);
        Assert.Equal(current, result.ScanCompleted);
        if (!current) Assert.Contains(result.Issues, issue => issue.Code == "ssis.discovery.context-changed");
        Assert.DoesNotContain("synthetic-bounded-password", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    private sealed class GuardedSink(Func<bool> current) : IProviderDiscoverySink, IProviderDiscoveryContextGuard
    {
        public Task<bool> IsCurrentAsync(CancellationToken cancellationToken) => Task.FromResult(current());
        public ValueTask AppendAsync(ProviderDiscoveryObservation observation, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Rehydrated_provider_type_is_discovered_by_its_canonical_value()
    {
        var original = Instance();
        var rehydrated = ProviderInstance.Create(original.Id, ProviderType.Parse("ssis"), original.DisplayName,
            true, original.Configuration, original.SecretReference, DateTimeOffset.UtcNow);
        var result = await Create(new StubDiscoveryQuery(new SsisDiscoveryScannerTests.GeneratedSession(1)))
            .ScanAsync(rehydrated, Sink(), TestContext.Current.CancellationToken);
        Assert.True(result.ScanCompleted);
        Assert.Equal(ProviderDiscoveryOutcome.Succeeded, result.Outcome);
    }

    [Fact]
    public async Task Supported_accessible_instance_returns_complete_streamed_observations()
    {
        var query = new StubDiscoveryQuery(new SsisDiscoveryScannerTests.GeneratedSession(2));
        var result = await Create(query).ScanAsync(Instance(), Sink(), TestContext.Current.CancellationToken);
        Assert.Equal(ProviderDiscoveryOutcome.Succeeded, result.Outcome);
        Assert.True(result.ScanCompleted);
        Assert.Equal(ProviderDiscoverySourceCompatibility.Compatible, result.SourceCompatibility);
        Assert.Equal(5, result.ObservationsDelivered);
        Assert.Equal(2, result.ExecutableCandidates);
        Assert.Equal(1, query.CallCount);
    }

    [Fact]
    public async Task Unknown_formats_remain_observed_in_a_complete_partial_compatibility_scan()
    {
        var session = new SsisDiscoveryScannerTests.GeneratedSession(1)
        {
            ManifestOverride = [DiscoveryTestData.Project(format: 99)],
        };
        var result = await Create(new StubDiscoveryQuery(session)).ScanAsync(
            Instance(), Sink(), TestContext.Current.CancellationToken);
        Assert.Equal(ProviderDiscoveryOutcome.Partial, result.Outcome);
        Assert.True(result.ScanCompleted);
        Assert.Equal(ProviderDiscoverySourceCompatibility.Compatible, result.SourceCompatibility);
        Assert.Equal(3, result.ObservationsDelivered);
        Assert.Equal(2, result.IncompatibleDefinitions);
        Assert.Equal(0, result.ExecutableCandidates);
    }

    [Theory]
    [InlineData("15.0.1.1")]
    [InlineData("16.0.1.1")]
    public async Task Unverified_server_fails_before_catalog_discovery(string version)
    {
        var query = new StubDiscoveryQuery(new SsisDiscoveryScannerTests.GeneratedSession(1));
        var result = await Create(query, version: version).ScanAsync(
            Instance(), Sink(), TestContext.Current.CancellationToken);
        Assert.False(result.ScanCompleted);
        Assert.Equal(ProviderDiscoverySourceCompatibility.Unverified, result.SourceCompatibility);
        Assert.Equal(ProviderDiscoveryOutcome.Failed, result.Outcome);
        Assert.Equal(0, query.CallCount);
    }

    [Fact]
    public async Task Certificate_bypass_is_rejected_before_secret_or_network_outside_development()
    {
        var query = new StubDiscoveryQuery(new SsisDiscoveryScannerTests.GeneratedSession(1));
        var resolver = new StubSecretResolver("canary");
        var result = await Create(query, resolver).ScanAsync(
            Instance(trust: true), Sink(), TestContext.Current.CancellationToken);
        Assert.False(result.ScanCompleted);
        Assert.Contains(result.Issues, issue => issue.Code == "ssis.discovery.tls-policy-rejected");
        Assert.Equal(0, resolver.CallCount);
        Assert.Equal(0, query.CallCount);
    }

    [Theory]
    [InlineData("PermissionDenied", "ssis.discovery.catalog-permission-denied")]
    [InlineData("CatalogSurfaceIncompatible", "ssis.discovery.catalog-surface-incompatible")]
    [InlineData("TlsCertificateRejected", "ssis.discovery.tls-certificate-rejected")]
    [InlineData("AuthenticationRejected", "ssis.discovery.authentication-rejected")]
    public async Task Native_errors_are_sanitized_and_incomplete(string kind, string code)
    {
        var query = new StubDiscoveryQuery(new SsisDiscoveryScannerTests.GeneratedSession(1),
            new SsisProbeException(Enum.Parse<SsisFailureKind>(kind), SsisValidationStage.DiscoveryQuery));
        var result = await Create(query).ScanAsync(
            Instance(), Sink(), TestContext.Current.CancellationToken);
        Assert.Equal(ProviderDiscoveryOutcome.Failed, result.Outcome);
        Assert.False(result.ScanCompleted);
        Assert.Contains(result.Issues, issue => issue.Code == code);
        Assert.Equal(0, result.ObservationsDelivered);
    }

    [Fact]
    public async Task Secret_locator_password_and_native_exception_are_absent_from_results()
    {
        const string canary = "sql-password-and-exception-canary";
        var resolver = new StubSecretResolver(new SecretResolutionException(
            SecretErrorCode.SecretNotFound, canary));
        var result = await Create(new StubDiscoveryQuery(
            new SsisDiscoveryScannerTests.GeneratedSession(1)), resolver).ScanAsync(
                Instance(), Sink(), TestContext.Current.CancellationToken);
        var output = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(canary, output, StringComparison.Ordinal);
        Assert.DoesNotContain("FLOWPLANE_A11_TEST_PASSWORD", output, StringComparison.Ordinal);
        Assert.Contains(result.Issues, issue => issue.Code == "ssis.discovery.secret-unavailable");
    }

    [Fact]
    public async Task Caller_cancellation_propagates_and_does_not_complete()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new SsisDiscoveryScannerTests.GeneratedSession(1, packages: 1000);
        var sink = new SsisDiscoveryScannerTests.CallbackSink((observation, _) =>
        {
            if (observation.ExecutableCandidate is not null) cancellation.Cancel();
            return ValueTask.CompletedTask;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create(new StubDiscoveryQuery(session)).ScanAsync(Instance(), sink, cancellation.Token));
        Assert.InRange(session.RowsRead, 1, 2);
    }

    [Fact]
    public async Task Provider_timeout_is_unavailable_and_not_retried()
    {
        var query = new StubDiscoveryQuery(new SsisDiscoveryScannerTests.GeneratedSession(1));
        var sink = new SsisDiscoveryScannerTests.CallbackSink(async (_, token) =>
            await Task.Delay(Timeout.InfiniteTimeSpan, token));
        var result = await Create(query, options: new SsisDiscoveryOptions
        {
            ConnectionTimeoutSeconds = 1,
            CommandTimeoutSeconds = 1,
            OverallTimeoutSeconds = 1,
        }).ScanAsync(Instance(), sink, TestContext.Current.CancellationToken);
        Assert.Equal(ProviderDiscoveryOutcome.Unavailable, result.Outcome);
        Assert.False(result.ScanCompleted);
        Assert.Contains(result.Issues, issue => issue.Code == "ssis.discovery.timeout");
        Assert.Equal(1, query.CallCount);
    }

    [Fact]
    public async Task Failure_after_rows_never_masquerades_as_partial_complete_coverage()
    {
        var session = new SsisDiscoveryScannerTests.GeneratedSession(1) { Change = "metadata" };
        var result = await Create(new StubDiscoveryQuery(session)).ScanAsync(
            Instance(), Sink(), TestContext.Current.CancellationToken);
        Assert.Equal(ProviderDiscoveryOutcome.Unavailable, result.Outcome);
        Assert.False(result.ScanCompleted);
        Assert.Equal(3, result.ObservationsDelivered);
        Assert.Contains(result.Issues, issue => issue.Code == "ssis.discovery.source-changed");
    }

    [Fact]
    public async Task Sink_failure_is_sanitized_and_never_completes_a_scan()
    {
        var result = await Create(new StubDiscoveryQuery(
            new SsisDiscoveryScannerTests.GeneratedSession(1))).ScanAsync(Instance(),
                new SsisDiscoveryScannerTests.CallbackSink((_, _) => throw new IOException("sink-secret")),
                TestContext.Current.CancellationToken);
        Assert.False(result.ScanCompleted);
        Assert.Equal(ProviderDiscoveryOutcome.Unavailable, result.Outcome);
        Assert.DoesNotContain("sink-secret", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    private static SsisProviderDiscoveryCapability Create(ISsisDiscoveryQuery query,
        ISecretResolver? resolver = null, string version = "17.0.1000.7",
        SsisDiscoveryOptions? options = null) =>
        new(new SsisProviderConfigurationCodec(), resolver ?? new StubSecretResolver("secret"),
            new StubSqlExecutor(new SsisMasterFacts(version, "RTM", "Developer", 1)),
            query, new SsisValidationSecurityPolicy(false), options ?? SsisDiscoveryOptions.Default);

    [Fact]
    public async Task Cleanup_failure_does_not_replace_caller_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var capability = Create(new StubDiscoveryQuery(new CleanupFailureSession()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capability.ScanAsync(Instance(),
            new SsisDiscoveryScannerTests.CallbackSink((_, _) =>
            {
                cancellation.Cancel();
                return ValueTask.CompletedTask;
            }), cancellation.Token));
    }

    [Fact]
    public async Task Cleanup_failure_does_not_hide_provider_timeout()
    {
        var capability = Create(new StubDiscoveryQuery(new CleanupFailureSession()),
            options: new SsisDiscoveryOptions
            {
                ConnectionTimeoutSeconds = 1,
                CommandTimeoutSeconds = 1,
                OverallTimeoutSeconds = 1,
            });
        var result = await capability.ScanAsync(Instance(),
            new SsisDiscoveryScannerTests.CallbackSink(async (_, token) =>
                await Task.Delay(Timeout.InfiniteTimeSpan, token)), TestContext.Current.CancellationToken);
        Assert.False(result.ScanCompleted);
        Assert.Contains(result.Issues, issue => issue.Code == "ssis.discovery.timeout");
        Assert.DoesNotContain("cleanup-canary", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    private sealed class CleanupFailureSession : ISsisDiscoverySession
    {
        private readonly SsisDiscoveryScannerTests.GeneratedSession inner = new(1);
        public IAsyncEnumerable<SsisProjectManifestEntry> ReadManifestAsync(CancellationToken token) =>
            inner.ReadManifestAsync(token);
        public IAsyncEnumerable<SsisDiscoveryRow> ReadProjectBatchAsync(
            IReadOnlyList<SsisProjectManifestEntry> projects, CancellationToken token) =>
            inner.ReadProjectBatchAsync(projects, token);
        public ValueTask DisposeAsync() => ValueTask.FromException(new IOException("cleanup-canary"));
    }

    private static IProviderDiscoverySink Sink() =>
        new SsisDiscoveryScannerTests.CallbackSink((_, _) => ValueTask.CompletedTask);

    private static ProviderInstance Instance(bool trust = false)
    {
        var codec = new SsisProviderConfigurationCodec();
        return ProviderInstance.Create(DiscoveryTestData.InstanceId,
            SsisProviderConfigurationCodec.SsisProviderType, "SSIS discovery test", true,
            codec.Encode(new SsisProviderConfiguration("sql.example.internal", 1433, "runtime", trust)),
            SecretReference.Environment("FLOWPLANE_A11_TEST_PASSWORD"), DateTimeOffset.UtcNow);
    }

    private sealed class StubSqlExecutor(SsisMasterFacts facts) : ISsisSqlExecutor
    {
        public int MasterCalls { get; private set; }
        public Task<SsisMasterFacts> ReadMasterFactsAsync(SsisProviderConfiguration configuration,
            ReadOnlyMemory<char> password, SsisValidationOptions options, CancellationToken token)
        {
            MasterCalls++;
            return Task.FromResult(facts);
        }
        public Task<SsisCatalogEvidence> ReadCatalogEvidenceAsync(SsisProviderConfiguration configuration,
            ReadOnlyMemory<char> password, SsisValidationOptions options, CancellationToken token) =>
            throw new InvalidOperationException("Execution capability evidence is outside discovery.");
    }

    private sealed class StubDiscoveryQuery(ISsisDiscoverySession session, Exception? error = null)
        : ISsisDiscoveryQuery
    {
        public int CallCount { get; private set; }
        public ValueTask<ISsisDiscoverySession> OpenSessionAsync(SsisProviderConfiguration configuration,
            ReadOnlyMemory<char> password, SsisDiscoveryOptions options, CancellationToken token)
        {
            CallCount++;
            return error is null ? ValueTask.FromResult(session) :
                ValueTask.FromException<ISsisDiscoverySession>(error);
        }
    }

    private sealed class StubSecretResolver : ISecretResolver
    {
        private readonly string? value;
        private readonly Exception? error;
        public int CallCount { get; private set; }
        public StubSecretResolver(string value) => this.value = value;
        public StubSecretResolver(Exception error) => this.error = error;
        public async ValueTask<TResult> UseAsync<TResult>(SecretReference reference,
            Func<ReadOnlyMemory<char>, CancellationToken, ValueTask<TResult>> use,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (error is not null) throw error;
            return await use(value!.AsMemory(), cancellationToken);
        }
    }
}
