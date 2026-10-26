using FlowPlane.Application.Providers.DiscoveryInventory;

namespace FlowPlane.Application.Providers.DiscoveryHosting;

public enum HostedDiscoveryPhase
{
    Accepted, Scanning, Preparing, Publishing, Published, Failed, Unavailable, Abandoned, PublicationUncertain,
}

public enum HostedDiscoveryError { InvalidKey, KeyConflict, SlotOccupied, VersionChanged, Unavailable, NotFound }

public sealed class HostedDiscoveryException(HostedDiscoveryError code) : Exception("Discovery hosting operation could not complete.")
{
    public HostedDiscoveryError Code { get; } = code;
}

public sealed record DiscoveryActor(string Issuer, string Subject)
{
    public override string ToString() => "DiscoveryActor(redacted)";
}

public sealed record HostedDiscoveryAdmission(Guid Key, Guid ProviderId, long ProviderVersion, DiscoveryActor Actor);

/// <summary>Safe receipt only; never contains actor, endpoint, credential, or metadata payloads.</summary>
public sealed record HostedDiscoveryReceipt(Guid RefreshId, Guid ProviderInstanceId, HostedDiscoveryPhase Phase,
    DateTimeOffset AcceptedAtUtc, DateTimeOffset ExpiresAtUtc, long Observations, long Candidates,
    bool ScanCompleted, DiscoveryInventoryError? Error);

public sealed record HostedDiscoveryWork(HostedDiscoveryReceipt Receipt, DiscoveryActor Actor,
    IProviderDiscoveryRefreshSession Session, TimeSpan RemainingBudget);

public interface IHostedDiscoveryStore
{
    Task<HostedDiscoveryReceipt> AdmitAsync(HostedDiscoveryAdmission request, CancellationToken token);
    Task<HostedDiscoveryReceipt?> FindAsync(Guid providerId, Guid refreshId, CancellationToken token);
    Task<HostedDiscoveryWork?> TryAcquireAsync(Guid executorId, CancellationToken token);
    Task<bool> RenewAsync(Guid refreshId, Guid executorId, CancellationToken token);
    Task CompleteAsync(Guid refreshId, Guid executorId, ProviderDiscoveryRefreshResult result, CancellationToken token);
}
