namespace FlowPlane.Application.Providers.DiscoveryInventory;

/// <summary>Cancellation is not evidence that publication rolled back. Reconcile the optional durable refresh ID.</summary>
public sealed class ProviderDiscoveryRefreshCanceledException(Guid? refreshId, CancellationToken cancellationToken)
    : OperationCanceledException("The discovery refresh was cancelled; its durable receipt may require reconciliation.", cancellationToken)
{
    public Guid? RefreshId { get; } = refreshId;
}
