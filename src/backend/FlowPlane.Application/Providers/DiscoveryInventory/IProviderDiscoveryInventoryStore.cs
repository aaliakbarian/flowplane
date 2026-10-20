using FlowPlane.Domain.Providers;

namespace FlowPlane.Application.Providers.DiscoveryInventory;

public enum DiscoveryInventoryError
{
    RefreshInProgress, ProviderUnavailable, ContextChanged, Superseded, Expired,
    IncompleteScan, StorageUnavailable, InvalidObservation, DuplicateNativeKey, NativeKeyCollision,
    Cancelled, TimedOut, ReaderContextUnavailable,
}

public sealed class DiscoveryInventoryException(DiscoveryInventoryError code)
    : Exception($"Discovery inventory operation could not complete ({code}).")
{
    public DiscoveryInventoryError Code { get; } = code;
}

public enum DiscoveryPublicationStatus { Published, NotPublished, Uncertain }

public sealed record ProviderDiscoveryRefreshResult(
    Guid? RefreshId, DiscoveryPublicationStatus Status, DiscoveryInventoryError? Error);

public interface IProviderDiscoveryInventoryStore
{
    Task<IProviderDiscoveryRefreshSession> BeginRefreshAsync(Guid providerInstanceId, CancellationToken cancellationToken);
    Task<ProviderDiscoveryRefreshResult> ReconcileAsync(Guid refreshId, CancellationToken cancellationToken);
    Task CleanupAsync(Guid providerInstanceId, CancellationToken cancellationToken);

    /// <summary>
    /// Trusted internal use only: the caller must independently establish FlowPlane authorization
    /// and fresh native visibility. A refresh ID is provenance, never an authorization credential.
    /// There is no cached fallback after denial or failed verification.
    /// </summary>
    Task<IProviderDiscoveryInventoryReader> OpenReaderAsync(
        Guid providerInstanceId, Guid verifiedRefreshId, CancellationToken cancellationToken);
}

public interface IProviderDiscoveryRefreshSession : IAsyncDisposable
{
    Guid RefreshId { get; }
    ProviderInstance Instance { get; }
    IProviderDiscoverySink Sink { get; }
    DiscoveryInventoryError? SinkFailure { get; }
    Task<ProviderDiscoveryRefreshResult> PublishAsync(ProviderDiscoveryScanResult summary, CancellationToken cancellationToken);
    Task AbortAsync(DiscoveryInventoryError reason, CancellationToken cancellationToken);
}

public enum DiscoveryObservationVisibility { Observed, NotObserved }

public sealed record StoredDiscoveryObservation(
    string NativeKey, string SchemaId, DiscoveryObservationVisibility Visibility,
    ProviderDiscoveryMetadataDocument? Metadata, NativeDefinitionReference? ExecutableCandidate,
    DateTimeOffset LastObservedAtUtc)
{
    public override string ToString() => $"StoredDiscoveryObservation({SchemaId}, {Visibility})";
}

/// <summary>One short read-only repeatable-read snapshot. Do not reuse across requests or await external calls.</summary>
public interface IProviderDiscoveryInventoryReader : IAsyncDisposable
{
    Guid GenerationId { get; }
    IAsyncEnumerable<StoredDiscoveryObservation> ReadAsync(string? schemaId, CancellationToken cancellationToken);
}
