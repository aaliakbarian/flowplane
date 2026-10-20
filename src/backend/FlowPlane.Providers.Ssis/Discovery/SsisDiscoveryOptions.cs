namespace FlowPlane.Providers.Ssis.Discovery;

public sealed record SsisDiscoveryOptions
{
    public const string SectionName = "FlowPlane:Ssis";
    public const int MaxAllowedProjectsPerBatch = 100;
    public const int MaxAllowedConnectionTimeoutSeconds = 60;
    public const int MaxAllowedCommandTimeoutSeconds = 600;
    public const int MaxAllowedOverallTimeoutSeconds = 86_400;

    public static SsisDiscoveryOptions Default { get; } = new();

    public int MaxProjectsPerBatch { get; init; } = 20;
    public int ConnectionTimeoutSeconds { get; init; } = 5;
    public int CommandTimeoutSeconds { get; init; } = 15;
    public int OverallTimeoutSeconds { get; init; } = 1_800;

    public static void RejectObsoleteKeys(IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            if (key.Equals("MaxCandidates", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("MaxParametersPerCandidate", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("MaxEnvironmentReferencesPerProject", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{SectionName}:{key} is obsolete; use MaxProjectsPerBatch for internal batching.");
            }
        }
    }

    public void Validate()
    {
        ValidateRange(MaxProjectsPerBatch, MaxAllowedProjectsPerBatch, nameof(MaxProjectsPerBatch));
        ValidateRange(ConnectionTimeoutSeconds, MaxAllowedConnectionTimeoutSeconds,
            nameof(ConnectionTimeoutSeconds));
        ValidateRange(CommandTimeoutSeconds, MaxAllowedCommandTimeoutSeconds,
            nameof(CommandTimeoutSeconds));
        ValidateRange(OverallTimeoutSeconds, MaxAllowedOverallTimeoutSeconds,
            nameof(OverallTimeoutSeconds));
        if (OverallTimeoutSeconds < ConnectionTimeoutSeconds ||
            OverallTimeoutSeconds < CommandTimeoutSeconds)
        {
            throw new InvalidOperationException(
                $"{SectionName}:OverallTimeoutSeconds must cover connection and command timeouts.");
        }
    }

    private static void ValidateRange(int value, int maximum, string name)
    {
        if (value < 1 || value > maximum)
        {
            throw new InvalidOperationException($"{SectionName}:{name} must be between 1 and {maximum}.");
        }
    }
}
