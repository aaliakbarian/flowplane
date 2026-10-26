using FlowPlane.Api.Authentication;
using FlowPlane.Application.Providers.DiscoveryHosting;
using FlowPlane.Application.Providers.DiscoveryInventory;
using FlowPlane.Providers.Ssis.Configuration;

namespace FlowPlane.Api.Providers;

/// <summary>One awaited discovery slot. Durable ownership, not shutdown hooks, provides correctness.</summary>
internal sealed class DiscoveryHostedRunner(IServiceScopeFactory scopes, DiscoveryHostingOptions options,
    ILogger<DiscoveryHostedRunner> logger) : BackgroundService
{
    private readonly Guid executorId = Guid.NewGuid();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Discovery hosting is temporarily unavailable."); }
            try { await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    internal async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IHostedDiscoveryStore>();
        var work = await store.TryAcquireAsync(executorId, stoppingToken);
        if (work is null) return;
        using var run = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        run.CancelAfter(work.RemainingBudget);
        using var stopRenewal = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var renewal = RenewAsync(work.Receipt.RefreshId, run, stopRenewal.Token);
        ProviderDiscoveryRefreshResult result;
        try
        {
            var operators = scope.ServiceProvider.GetRequiredService<OperatorOptions>();
            var endpoints = scope.ServiceProvider.GetRequiredService<SsisEndpointPolicy>();
            var environment = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();
            var codec = scope.ServiceProvider.GetRequiredService<SsisProviderConfigurationCodec>();
            var configuration = codec.Decode(work.Session.Instance.Configuration);
            if (!operators.Allowed.Any(actor => actor.Issuer == work.Actor.Issuer && actor.Subject == work.Actor.Subject) ||
                !endpoints.Allows(configuration) || (configuration.TrustServerCertificate && !environment.IsDevelopment()))
            {
                await work.Session.AbortAsync(DiscoveryInventoryError.ProviderUnavailable, run.Token);
                await work.Session.DisposeAsync();
                result = new(work.Receipt.RefreshId, DiscoveryPublicationStatus.NotPublished, DiscoveryInventoryError.ProviderUnavailable);
            }
            else result = await scope.ServiceProvider.GetRequiredService<ProviderDiscoveryRefreshService>()
                .ExecuteAcceptedAsync(work.Session, run.Token);
        }
        catch (OperationCanceledException)
        {
            result = new(work.Receipt.RefreshId, DiscoveryPublicationStatus.Uncertain, DiscoveryInventoryError.Cancelled);
        }
        catch (Exception)
        {
            // Reconcile by ID; do not translate an exception near commit into assumed failure.
            result = new(work.Receipt.RefreshId, DiscoveryPublicationStatus.Uncertain, DiscoveryInventoryError.StorageUnavailable);
        }
        finally
        {
            await stopRenewal.CancelAsync();
            await renewal;
            try { await work.Session.DisposeAsync(); }
            catch (Exception) { /* Durable lease recovery remains authoritative. */ }
        }
        using var acknowledgement = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await store.CompleteAsync(work.Receipt.RefreshId, executorId, result, acknowledgement.Token);
    }

    private async Task RenewAsync(Guid refreshId, CancellationTokenSource run, CancellationToken token)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(options.RenewalSeconds), token);
                await using var scope = scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IHostedDiscoveryStore>();
                if (!await store.RenewAsync(refreshId, executorId, token))
                {
                    await run.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { await run.CancelAsync(); }
    }
}
