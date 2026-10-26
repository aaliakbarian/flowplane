using System.Reflection;

using FlowPlane.Application.Providers;
using FlowPlane.Application.Providers.DiscoveryHosting;
using FlowPlane.Application.Providers.DiscoveryInventory;
using FlowPlane.Domain.Providers;
using FlowPlane.Providers.Ssis.Configuration;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FlowPlane.Api.Tests;

public sealed class DiscoveryHostedRunnerTests
{
    [Fact]
    public async Task Lease_loss_cancels_the_awaited_native_scan_without_starting_it_again()
    {
        var fixture = new HostingFixture(DiscoveryPublicationStatus.Published) { HoldScan = true, Renewable = false };
        using var api = new ProviderApiFactory(settings: new Dictionary<string, string?>
        { ["FlowPlane:DiscoveryHosting:RenewalSeconds"] = "1" }, configureServices: services => Configure(services, fixture));
        using var client = api.Client("operator");
        await Cycle(api);
        Assert.Equal(1, fixture.Scans);
        Assert.True(fixture.Session.Aborted);
        await Cycle(api);
        Assert.Equal(1, fixture.Scans);
    }

    [Fact]
    public async Task Native_exception_text_and_secret_locator_are_not_exposed_by_hosted_results_or_logs()
    {
        var fixture = new HostingFixture(DiscoveryPublicationStatus.Published) { FailScan = true };
        using var api = Factory(fixture);
        using var client = api.Client("operator");
        await Cycle(api);
        Assert.Equal(DiscoveryPublicationStatus.NotPublished, fixture.Completion?.Status);
        Assert.DoesNotContain("synthetic-private-exception-canary", System.Text.Json.JsonSerializer.Serialize(fixture.Completion), StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-private-exception-canary", api.Logs.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC_NEVER_RESOLVED", api.Logs.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DiscoveryPublicationStatus.Published)]
    [InlineData(DiscoveryPublicationStatus.Uncertain)]
    public async Task Runner_executes_existing_claim_and_preserves_publication_uncertainty(DiscoveryPublicationStatus publication)
    {
        var fixture = new HostingFixture(publication);
        using var api = Factory(fixture);
        // Starting the HTTP host with disabled hosting performs no database or native IO.
        using var client = api.Client("operator");
        await Cycle(api);
        Assert.Equal(1, fixture.Scans);
        Assert.Equal(0, fixture.Claims);
        Assert.Equal(publication, fixture.Completion?.Status);
        Assert.Equal(fixture.Session.RefreshId, fixture.Completion?.RefreshId);
        await Cycle(api);
        Assert.Equal(1, fixture.Scans); // A second cycle cannot resume an already acquired generation.
    }

    [Theory]
    [InlineData("nonoperator", "sql.example.test")]
    [InlineData("operator", "blocked.example.test")]
    public async Task Revoked_actor_or_endpoint_prevents_native_scan(string subject, string host)
    {
        var fixture = new HostingFixture(DiscoveryPublicationStatus.Published, subject, host);
        using var api = Factory(fixture);
        using var client = api.Client("operator");
        await Cycle(api);
        Assert.Equal(0, fixture.Scans);
        Assert.True(fixture.Session.Aborted);
        Assert.Equal(DiscoveryPublicationStatus.NotPublished, fixture.Completion?.Status);
    }

    private static ProviderApiFactory Factory(HostingFixture fixture) => new(configureServices: services => Configure(services, fixture));

    private static void Configure(IServiceCollection services, HostingFixture fixture)
    {
        services.RemoveAll<IProviderDiscoveryCapability>();
        services.AddSingleton<IHostedDiscoveryStore>(fixture);
        services.AddSingleton<IProviderDiscoveryInventoryStore>(fixture);
        services.AddSingleton<IProviderDiscoveryCapability>(fixture);
    }

    private static async Task Cycle(ProviderApiFactory api)
    {
        var type = typeof(Program).Assembly.GetType("FlowPlane.Api.Providers.DiscoveryHostedRunner")!;
        var runner = ActivatorUtilities.CreateInstance(api.Services, type);
        try
        {
            var method = type.GetMethod("RunOnceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)method.Invoke(runner, [TestContext.Current.CancellationToken])!;
        }
        finally { ((IDisposable)runner).Dispose(); }
    }

    private sealed class HostingFixture(DiscoveryPublicationStatus publication, string subject = "operator", string host = "sql.example.test")
        : IHostedDiscoveryStore, IProviderDiscoveryInventoryStore, IProviderDiscoveryCapability
    {
        private bool acquired;
        public ScanSession Session { get; } = new(publication, host);
        public int Scans { get; private set; }
        public int Claims { get; private set; }
        public ProviderDiscoveryRefreshResult? Completion { get; private set; }
        public bool HoldScan { get; init; }
        public bool FailScan { get; init; }
        public bool Renewable { get; init; } = true;
        public ProviderType ProviderType => SsisProviderConfigurationCodec.SsisProviderType;
        public Task<HostedDiscoveryWork?> TryAcquireAsync(Guid executorId, CancellationToken token)
        {
            if (acquired) return Task.FromResult<HostedDiscoveryWork?>(null);
            acquired = true;
            return Task.FromResult<HostedDiscoveryWork?>(new(new(Session.RefreshId, Session.Instance.Id,
                HostedDiscoveryPhase.Scanning, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), 0, 0, false, null),
                new(ProviderApiFactory.Issuer, subject), Session, TimeSpan.FromHours(1)));
        }
        public Task<bool> RenewAsync(Guid refreshId, Guid executorId, CancellationToken token) => Task.FromResult(Renewable);
        public Task CompleteAsync(Guid refreshId, Guid executorId, ProviderDiscoveryRefreshResult result, CancellationToken token)
        { Completion = result; return Task.CompletedTask; }
        public async Task<ProviderDiscoveryScanResult> ScanAsync(ProviderInstance instance, IProviderDiscoverySink sink, CancellationToken cancellationToken = default)
        {
            Scans++;
            if (FailScan) throw new IOException("synthetic-private-exception-canary");
            if (HoldScan) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            return new ProviderDiscoveryScanResult(ProviderDiscoveryOutcome.Succeeded, true,
                ProviderDiscoverySourceCompatibility.Compatible, 0, 0, 0, now, now, []);
        }
        public Task<IProviderDiscoveryRefreshSession> BeginRefreshAsync(Guid providerInstanceId, CancellationToken token)
        { Claims++; throw new InvalidOperationException("A second claim is forbidden."); }
        public Task<HostedDiscoveryReceipt> AdmitAsync(HostedDiscoveryAdmission request, CancellationToken token) => throw new NotSupportedException();
        public Task<HostedDiscoveryReceipt?> FindAsync(Guid providerId, Guid refreshId, CancellationToken token) => throw new NotSupportedException();
        public Task<ProviderDiscoveryRefreshResult> ReconcileAsync(Guid refreshId, CancellationToken token) => throw new NotSupportedException();
        public Task CleanupAsync(Guid providerInstanceId, CancellationToken token) => throw new NotSupportedException();
        public Task<IProviderDiscoveryInventoryReader> OpenReaderAsync(Guid providerInstanceId, Guid verifiedRefreshId, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class ScanSession(DiscoveryPublicationStatus status, string host) : IProviderDiscoveryRefreshSession, IProviderDiscoverySink
    {
        public Guid RefreshId { get; } = Guid.CreateVersion7();
        public ProviderInstance Instance { get; } = ProviderInstance.Create(Guid.NewGuid(), SsisProviderConfigurationCodec.SsisProviderType,
            "Synthetic", true, new SsisProviderConfigurationCodec().Encode(new(host, 1433, "synthetic", false)),
            SecretReference.Environment("SYNTHETIC_NEVER_RESOLVED"), DateTimeOffset.UtcNow);
        public IProviderDiscoverySink Sink => this;
        public DiscoveryInventoryError? SinkFailure => null;
        public bool Aborted { get; private set; }
        public Task<ProviderDiscoveryRefreshResult> PublishAsync(ProviderDiscoveryScanResult summary, CancellationToken token) =>
            Task.FromResult(new ProviderDiscoveryRefreshResult(RefreshId, status, null));
        public Task AbortAsync(DiscoveryInventoryError reason, CancellationToken token) { Aborted = true; return Task.CompletedTask; }
        public ValueTask AppendAsync(ProviderDiscoveryObservation observation, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
