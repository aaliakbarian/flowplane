using FlowPlane.Application.Providers;
using FlowPlane.Application.Providers.DiscoveryInventory;

namespace FlowPlane.Infrastructure.Persistence.Discovery;

internal enum DiscoveryStateLifecycle { Active, Deleting }
internal enum DiscoveryGenerationStatus { Staging, Preparing, Ready, Published, Failed, Cancelled, Superseded, Abandoned }

internal sealed class DiscoveryState
{
    public Guid ProviderInstanceId { get; set; }
    public Guid? CurrentGenerationId { get; set; }
    public Guid? InFlightGenerationId { get; set; }
    public Guid? LastAttemptGenerationId { get; set; }
    public long PublicationVersion { get; set; }
    public long FenceToken { get; set; }
    public DiscoveryStateLifecycle Lifecycle { get; set; }
}

internal sealed class DiscoveryGeneration
{
    public Guid RefreshId { get; set; }
    public Guid ProviderInstanceId { get; set; }
    public Guid? BaseGenerationId { get; set; }
    public long FenceToken { get; set; }
    public long ExpectedPublicationVersion { get; set; }
    public long ProviderVersion { get; set; }
    public string ProviderType { get; set; } = null!;
    public string CredentialKind { get; set; } = null!;
    public long? ManagedSecretVersion { get; set; }
    public Guid AccessContextId { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? ScanStartedAtUtc { get; set; }
    public DateTimeOffset? ScanFinishedAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
    public DiscoveryGenerationStatus Status { get; set; }
    public long Version { get; set; } = 1;
    public bool ScanCompleted { get; set; }
    public ProviderDiscoveryOutcome? ScanOutcome { get; set; }
    public ProviderDiscoverySourceCompatibility? SourceCompatibility { get; set; }
    public long ObservationCount { get; set; }
    public long CandidateCount { get; set; }
    public long IncompatibleCount { get; set; }
    public long AddedCount { get; set; }
    public long ChangedCount { get; set; }
    public long UnchangedCount { get; set; }
    public long NotObservedCount { get; set; }
    public DiscoveryInventoryError? Error { get; set; }
}

internal sealed class DiscoveryObservationRecord
{
    public Guid ProviderInstanceId { get; set; }
    public Guid RefreshId { get; set; }
    public byte[] NativeKeyDigest { get; set; } = null!;
    public string NativeKey { get; set; } = null!;
    public string SchemaId { get; set; } = null!;
    public DiscoveryObservationVisibility Visibility { get; set; }
    public string? MetadataJson { get; set; }
    public string? CandidateSchemaId { get; set; }
    public string? CandidateJson { get; set; }
    public byte[]? ContentFingerprint { get; set; }
    public DateTimeOffset LastObservedAtUtc { get; set; }
    public Guid ObservedAccessContextId { get; set; }
    public long ObservedProviderVersion { get; set; }
    public long? ObservedSecretVersion { get; set; }
    public DateTimeOffset? NotObservedAtUtc { get; set; }
}
