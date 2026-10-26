using FlowPlane.Domain.Providers;

namespace FlowPlane.Application.Providers;

public interface IProviderDiscoveryCapability
{
    ProviderType ProviderType { get; }

    Task<ProviderDiscoveryScanResult> ScanAsync(
        ProviderInstance providerInstance,
        IProviderDiscoverySink sink,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Receives provisional observations with backpressure. A consumer must discard an incomplete
/// scan; receiving a row does not authorize publication or disclosure of an inventory.
/// Use a dedicated invocation-scoped sink for each scan; do not mix concurrent refreshes.
/// A-11b may publish only after durable sink success and a verified terminal result with
/// ScanCompleted=true, Outcome Succeeded or Partial, and SourceCompatibility Compatible.
/// Cancellation, exceptions, timeouts or process termination never authorize publication.
/// </summary>
public interface IProviderDiscoverySink
{
    ValueTask AppendAsync(
        ProviderDiscoveryObservation observation,
        CancellationToken cancellationToken);
}

/// <summary>Recheck persisted configuration/credential ownership after resolving a secret and before native IO.</summary>
public interface IProviderDiscoveryContextGuard
{
    Task<bool> IsCurrentAsync(CancellationToken cancellationToken);
}

public enum ProviderDiscoveryOutcome
{
    Succeeded,
    Partial,
    Failed,
    Unavailable,
}

public enum ProviderDiscoverySourceCompatibility
{
    Unknown,
    Compatible,
    Unverified,
    Unsupported,
    Incompatible,
}

/// <summary>
/// ScanCompleted describes verified coverage, not per-definition compatibility. Partial may
/// describe incompatible definitions within complete coverage; it must never hide an incomplete scan.
/// </summary>
public sealed record ProviderDiscoveryScanResult(
    ProviderDiscoveryOutcome Outcome,
    bool ScanCompleted,
    ProviderDiscoverySourceCompatibility SourceCompatibility,
    long ObservationsDelivered,
    long ExecutableCandidates,
    long IncompatibleDefinitions,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    IReadOnlyList<ProviderDiscoveryIssue> Issues);

public sealed record ProviderDiscoveryIssue(string Code, string Message);
