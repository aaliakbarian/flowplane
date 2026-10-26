using System.Net;
using System.Text.Json;

using FlowPlane.Application.Providers;
using FlowPlane.Application.Providers.DiscoveryHosting;
using FlowPlane.Application.Providers.DiscoveryInventory;
using FlowPlane.Domain.Providers;
using FlowPlane.Providers.Ssis.Configuration;

using Microsoft.Extensions.DependencyInjection;

namespace FlowPlane.Api.Tests;

public sealed class DiscoveryRefreshHttpContractTests
{
    [Fact]
    public async Task Admission_waits_for_the_durable_boundary_and_returns_only_a_safe_receipt()
    {
        var boundary = new Boundary();
        using var api = Factory(boundary);
        using var client = api.Client("operator");
        using var request = Request(boundary);
        var pending = client.SendAsync(request, TestContext.Current.CancellationToken);
        await boundary.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(pending.IsCompleted);
        boundary.Durable.TrySetResult();
        using var response = await pending;
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("/api/provider-instances/" + boundary.Instance.Id + "/discovery-refreshes/" + boundary.Key,
            response.Headers.Location?.OriginalString);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Accepted", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC_LOCATOR_NEVER_RETURNED", text, StringComparison.Ordinal);
        Assert.DoesNotContain("issuer", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("subject", text, StringComparison.OrdinalIgnoreCase);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(ProviderApiFactory.Issuer, boundary.Request?.Actor.Issuer);
        Assert.Equal("operator", boundary.Request?.Actor.Subject);
        Assert.Equal(1, boundary.Request?.ProviderVersion);
        using var status = await client.GetAsync(response.Headers.Location!.OriginalString.Replace("/api/", "/", StringComparison.Ordinal), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal(boundary.Key, JsonDocument.Parse(await status.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("refreshId").GetGuid());
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("GET")]
    public async Task Stored_unauthorized_destination_is_rejected_before_admission_or_status_storage(string method)
    {
        var boundary = new Boundary("blocked.example.test");
        using var api = Factory(boundary);
        using var client = api.Client("operator");
        using var request = Request(boundary);
        request.Method = new HttpMethod(method);
        if (method == "GET") request.RequestUri = new(request.RequestUri!.OriginalString + "/" + boundary.Key, UriKind.Relative);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(boundary.Entered.Task.IsCompleted);
        Assert.Equal(0, boundary.StatusReads);
    }

    private static ProviderApiFactory Factory(Boundary boundary) => new(settings: new Dictionary<string, string?>
    { ["FlowPlane:DiscoveryHosting:Enabled"] = "true" }, configureServices: services =>
    {
        services.AddSingleton<IHostedDiscoveryStore>(boundary);
        services.AddSingleton<IProviderInstanceStore>(boundary);
    });

    private static HttpRequestMessage Request(Boundary boundary)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/provider-instances/" + boundary.Instance.Id + "/discovery-refreshes");
        request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", boundary.Key.ToString("D"));
        return request;
    }

    private sealed class Boundary(string host = "sql.example.test") : IHostedDiscoveryStore, IProviderInstanceStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Durable { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HostedDiscoveryAdmission? Request { get; private set; }
        public int StatusReads { get; private set; }
        public Guid Key { get; } = Guid.CreateVersion7();
        public ProviderInstance Instance { get; } = ProviderInstance.Create(Guid.NewGuid(), SsisProviderConfigurationCodec.SsisProviderType,
            "Synthetic", true, new SsisProviderConfigurationCodec().Encode(new(host, 1433, "synthetic", false)),
            SecretReference.Environment("SYNTHETIC_LOCATOR_NEVER_RETURNED"), DateTimeOffset.UtcNow);
        private HostedDiscoveryReceipt Receipt() => new(Key, Instance.Id, HostedDiscoveryPhase.Accepted,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), 0, 0, false, null);
        public async Task<HostedDiscoveryReceipt> AdmitAsync(HostedDiscoveryAdmission request, CancellationToken token)
        { Request = request; Entered.TrySetResult(); await Durable.Task.WaitAsync(token); return Receipt(); }
        public Task<HostedDiscoveryReceipt?> FindAsync(Guid providerId, Guid refreshId, CancellationToken token)
        { StatusReads++; return Task.FromResult<HostedDiscoveryReceipt?>(providerId == Instance.Id && refreshId == Key ? Receipt() : null); }
        public Task<ProviderInstance?> FindAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(id == Instance.Id ? Instance : null);
        public Task<HostedDiscoveryWork?> TryAcquireAsync(Guid executorId, CancellationToken token) => Task.FromResult<HostedDiscoveryWork?>(null);
        public Task<bool> RenewAsync(Guid refreshId, Guid executorId, CancellationToken token) => throw new NotSupportedException();
        public Task CompleteAsync(Guid refreshId, Guid executorId, ProviderDiscoveryRefreshResult result, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProviderInstance>> ListAsync(int offset, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProviderInstance> UpdateAsync(Guid id, long expectedVersion, string displayName, bool isEnabled, ProviderConfigurationDocument configuration, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProviderInstance> CreateWithManagedSecretAsync(Guid id, ProviderType providerType, string displayName, bool isEnabled, ProviderConfigurationDocument configuration, ReadOnlyMemory<char> secret, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReplaceManagedSecretAsync(Guid providerInstanceId, long expectedVersion, ReadOnlyMemory<char> secret, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid providerInstanceId, long expectedVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
