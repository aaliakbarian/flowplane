using FlowPlane.Application.Providers;
using FlowPlane.Application.Providers.DiscoveryInventory;
using FlowPlane.Domain.Providers;
using FlowPlane.Providers.Ssis.Configuration;

namespace FlowPlane.ProviderConfiguration.Tests;

public sealed class ProviderDiscoveryRefreshTests
{
    [Fact]
    public async Task Rehydrated_provider_type_selects_capability_by_value_not_object_identity()
    {
        var store = new Store();
        var capability = new Capability(ProviderType.Parse("fixture"), (_, _, _) =>
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ProviderDiscoveryScanResult(ProviderDiscoveryOutcome.Succeeded, true,
                ProviderDiscoverySourceCompatibility.Compatible, 0, 0, 0, now, now, []));
        });
        var result = await new ProviderDiscoveryRefreshService(store, [capability], new()).RefreshAsync(
            store.Session.Instance.Id, TestContext.Current.CancellationToken);
        Assert.Equal(DiscoveryPublicationStatus.Published, result.Status);
    }

    [Theory]
    [InlineData(true, ProviderDiscoveryOutcome.Succeeded, 0, DiscoveryPublicationStatus.Published)]
    [InlineData(true, ProviderDiscoveryOutcome.Partial, 1, DiscoveryPublicationStatus.Published)]
    [InlineData(false, ProviderDiscoveryOutcome.Partial, 1, DiscoveryPublicationStatus.NotPublished)]
    public async Task Coordinator_publishes_only_verified_complete_scans(bool complete, ProviderDiscoveryOutcome outcome,
        long incompatible, DiscoveryPublicationStatus expected)
    {
        var store = new Store();
        var capability = new Capability(store.Session.Instance.ProviderType, (_, _, _) =>
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ProviderDiscoveryScanResult(outcome, complete,
                ProviderDiscoverySourceCompatibility.Compatible, incompatible, 0, incompatible, now, now, []));
        });
        var result = await new ProviderDiscoveryRefreshService(store, [capability], new()).RefreshAsync(
            store.Session.Instance.Id, TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public async Task Caller_cancellation_wins_over_source_or_cleanup_failure()
    {
        var store = new Store();
        using var caller = new CancellationTokenSource();
        var capability = new Capability(store.Session.Instance.ProviderType, (_, _, _) =>
        {
            caller.Cancel();
            throw new IOException("canary-never-forwarded");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProviderDiscoveryRefreshService(store, [capability], new()).RefreshAsync(store.Session.Instance.Id, caller.Token));
        Assert.True(store.Session.Aborted);
    }

    [Fact]
    public async Task Caller_cancellation_after_commit_reconciliation_propagates_without_aborting_publication()
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var store = new Store { Session = new Session { BeforePublishReturn = caller.Cancel } };
        var capability = new Capability(store.Session.Instance.ProviderType, (_, _, _) =>
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ProviderDiscoveryScanResult(ProviderDiscoveryOutcome.Succeeded, true,
                ProviderDiscoverySourceCompatibility.Compatible, 0, 0, 0, now, now, []));
        });
        var cancellation = await Assert.ThrowsAsync<ProviderDiscoveryRefreshCanceledException>(() =>
            new ProviderDiscoveryRefreshService(store, [capability], new()).RefreshAsync(store.Session.Instance.Id, caller.Token));
        Assert.Equal(store.Session.RefreshId, cancellation.RefreshId);
        Assert.Equal(caller.Token, cancellation.CancellationToken);
        Assert.False(store.Session.Aborted);
    }

    [Fact]
    public async Task Lost_publication_acknowledgement_is_not_overwritten_with_failed_or_aborted()
    {
        var store = new Store { Session = new Session { PublicationStatus = DiscoveryPublicationStatus.Uncertain } };
        var capability = new Capability(store.Session.Instance.ProviderType, (_, _, _) =>
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ProviderDiscoveryScanResult(ProviderDiscoveryOutcome.Succeeded, true,
                ProviderDiscoverySourceCompatibility.Compatible, 0, 0, 0, now, now, []));
        });
        var result = await new ProviderDiscoveryRefreshService(store, [capability], new()).RefreshAsync(store.Session.Instance.Id, TestContext.Current.CancellationToken);
        Assert.Equal(DiscoveryPublicationStatus.Uncertain, result.Status);
        Assert.False(store.Session.Aborted);
    }

    private sealed class Capability(ProviderType type,
        Func<ProviderInstance, IProviderDiscoverySink, CancellationToken, Task<ProviderDiscoveryScanResult>> scan) : IProviderDiscoveryCapability
    {
        public ProviderType ProviderType => type;
        public Task<ProviderDiscoveryScanResult> ScanAsync(ProviderInstance instance, IProviderDiscoverySink sink, CancellationToken cancellationToken = default) => scan(instance, sink, cancellationToken);
    }
    private sealed class Store : IProviderDiscoveryInventoryStore
    {
        public Session Session { get; init; } = new();
        public Task<IProviderDiscoveryRefreshSession> BeginRefreshAsync(Guid id, CancellationToken token) => Task.FromResult<IProviderDiscoveryRefreshSession>(Session);
        public Task<ProviderDiscoveryRefreshResult> ReconcileAsync(Guid id, CancellationToken token) => throw new NotSupportedException();
        public Task CleanupAsync(Guid id, CancellationToken token) => throw new NotSupportedException();
        public Task<IProviderDiscoveryInventoryReader> OpenReaderAsync(Guid id, Guid refresh, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class Session : IProviderDiscoveryRefreshSession, IProviderDiscoverySink
    {
        public Guid RefreshId { get; } = Guid.NewGuid();
        public ProviderInstance Instance { get; } = ProviderInstance.Create(Guid.NewGuid(), ProviderType.Parse("fixture"),
            "Fixture", true, new SsisProviderConfigurationCodec().Encode(new("host", 1433, "user", false)),
            SecretReference.Environment("FIXTURE_PASSWORD"), DateTimeOffset.UtcNow);
        public IProviderDiscoverySink Sink => this;
        public DiscoveryInventoryError? SinkFailure => null;
        public bool Aborted { get; private set; }
        public DiscoveryPublicationStatus PublicationStatus { get; init; } = DiscoveryPublicationStatus.Published;
        public Action? BeforePublishReturn { get; init; }
        public Task<ProviderDiscoveryRefreshResult> PublishAsync(ProviderDiscoveryScanResult summary, CancellationToken token)
        {
            BeforePublishReturn?.Invoke();
            return Task.FromResult(new ProviderDiscoveryRefreshResult(RefreshId, PublicationStatus, null));
        }
        public Task AbortAsync(DiscoveryInventoryError reason, CancellationToken token) { Aborted = true; return Task.CompletedTask; }
        public ValueTask AppendAsync(ProviderDiscoveryObservation observation, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
