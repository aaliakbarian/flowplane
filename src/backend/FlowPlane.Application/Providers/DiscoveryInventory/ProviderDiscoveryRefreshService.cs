namespace FlowPlane.Application.Providers.DiscoveryInventory;

public sealed class ProviderDiscoveryRefreshService(
    IProviderDiscoveryInventoryStore store,
    IEnumerable<IProviderDiscoveryCapability> capabilities,
    DiscoveryInventoryOptions options)
{
    public async Task<ProviderDiscoveryRefreshResult> RefreshAsync(Guid providerInstanceId, CancellationToken cancellationToken = default)
    {
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.CancelAfter(options.RefreshTimeout);
        IProviderDiscoveryRefreshSession? session = null;
        var publicationReturned = false;
        try
        {
            using (var claim = CancellationTokenSource.CreateLinkedTokenSource(operation.Token))
            {
                claim.CancelAfter(TimeSpan.FromSeconds(options.ClaimTimeoutSeconds));
                session = await store.BeginRefreshAsync(providerInstanceId, claim.Token);
            }
            var capability = capabilities.SingleOrDefault(item => item.ProviderType.Equals(session.Instance.ProviderType))
                ?? throw new DiscoveryInventoryException(DiscoveryInventoryError.ProviderUnavailable);
            ProviderDiscoveryScanResult scan;
            using (var source = CancellationTokenSource.CreateLinkedTokenSource(operation.Token))
            {
                source.CancelAfter(TimeSpan.FromSeconds(options.ScanTimeoutSeconds));
                scan = await capability.ScanAsync(session.Instance, session.Sink, source.Token);
            }
            if (!DiscoveryPublicationPolicy.CanPublish(scan, scan.ObservationsDelivered, scan.ExecutableCandidates))
                throw new DiscoveryInventoryException(session.SinkFailure ?? DiscoveryInventoryError.IncompleteScan);
            // Session applies its independent preparation/publication budgets and the durable deadline.
            var publication = await session.PublishAsync(scan, operation.Token);
            publicationReturned = true;
            cancellationToken.ThrowIfCancellationRequested();
            return publication;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!publicationReturned) await AbortSafelyAsync(session, DiscoveryInventoryError.Cancelled);
            throw new ProviderDiscoveryRefreshCanceledException(session?.RefreshId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await AbortSafelyAsync(session, DiscoveryInventoryError.TimedOut);
            return new(session?.RefreshId, DiscoveryPublicationStatus.NotPublished, DiscoveryInventoryError.TimedOut);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            await AbortSafelyAsync(session, DiscoveryInventoryError.Cancelled);
            throw new ProviderDiscoveryRefreshCanceledException(session?.RefreshId, cancellationToken);
        }
        catch (DiscoveryInventoryException exception)
        {
            await AbortSafelyAsync(session, exception.Code);
            return new(session?.RefreshId, DiscoveryPublicationStatus.NotPublished, exception.Code);
        }
        catch (Exception)
        {
            await AbortSafelyAsync(session, DiscoveryInventoryError.StorageUnavailable);
            return new(session?.RefreshId, DiscoveryPublicationStatus.NotPublished, DiscoveryInventoryError.StorageUnavailable);
        }
        finally
        {
            if (session is not null)
            {
                try { await session.DisposeAsync(); }
                catch (Exception) { /* Disposal cannot replace the outcome, especially caller cancellation. */ }
            }
        }
    }

    private static async Task AbortSafelyAsync(IProviderDiscoveryRefreshSession? session, DiscoveryInventoryError reason)
    {
        if (session is null) return;
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await session.AbortAsync(reason, cleanup.Token); }
        catch (Exception) { /* Durable expiry and fencing recover an unreachable store. */ }
    }
}
