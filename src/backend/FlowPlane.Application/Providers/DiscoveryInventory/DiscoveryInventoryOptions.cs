namespace FlowPlane.Application.Providers.DiscoveryInventory;

public sealed record DiscoveryInventoryOptions
{
    public const string SectionName = "FlowPlane:DiscoveryInventory";
    public int ClaimTimeoutSeconds { get; init; } = 30;
    // Supplied by composition from the same provider options snapshot used by the scanner.
    public int ScanTimeoutSeconds { get; init; } = 1800;
    public int PreparationTimeoutSeconds { get; init; } = 300;
    public int PublicationTimeoutSeconds { get; init; } = 30;
    public int ReaderTimeoutSeconds { get; init; } = 30;
    public int WriteBatchSize { get; init; } = 128;
    public int WriteBufferBytes { get; init; } = 262144;
    public int CleanupBatchSize { get; init; } = 1000;

    public TimeSpan RefreshTimeout
    {
        get
        {
            Validate();
            return TimeSpan.FromSeconds(Math.Max(3600, checked(ClaimTimeoutSeconds + ScanTimeoutSeconds
                + PreparationTimeoutSeconds + PublicationTimeoutSeconds)));
        }
    }

    public void Validate()
    {
        Range(ClaimTimeoutSeconds, 120, nameof(ClaimTimeoutSeconds));
        Range(ScanTimeoutSeconds, 86400, nameof(ScanTimeoutSeconds));
        Range(PreparationTimeoutSeconds, 7200, nameof(PreparationTimeoutSeconds));
        Range(PublicationTimeoutSeconds, 120, nameof(PublicationTimeoutSeconds));
        Range(ReaderTimeoutSeconds, 120, nameof(ReaderTimeoutSeconds));
        Range(WriteBatchSize, 1000, nameof(WriteBatchSize));
        Range(WriteBufferBytes, 4194304, nameof(WriteBufferBytes));
        Range(CleanupBatchSize, 10000, nameof(CleanupBatchSize));
    }

    private static void Range(int value, int maximum, string name)
    {
        if (value < 1 || value > maximum) throw new ArgumentOutOfRangeException(name);
    }
}
