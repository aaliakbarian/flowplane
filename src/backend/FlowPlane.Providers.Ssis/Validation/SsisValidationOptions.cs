namespace FlowPlane.Providers.Ssis.Validation;

public sealed record SsisValidationOptions
{
    public static SsisValidationOptions Default { get; } = new(5, 10, 30);

    public SsisValidationOptions(
        int connectionTimeoutSeconds,
        int commandTimeoutSeconds,
        int overallTimeoutSeconds)
    {
        ConnectionTimeoutSeconds = Positive(connectionTimeoutSeconds, nameof(connectionTimeoutSeconds));
        CommandTimeoutSeconds = Positive(commandTimeoutSeconds, nameof(commandTimeoutSeconds));
        OverallTimeoutSeconds = Positive(overallTimeoutSeconds, nameof(overallTimeoutSeconds));
    }

    public int ConnectionTimeoutSeconds { get; }

    public int CommandTimeoutSeconds { get; }

    public int OverallTimeoutSeconds { get; }

    private static int Positive(int value, string parameterName) => value > 0
        ? value
        : throw new ArgumentOutOfRangeException(parameterName, "Timeouts must be positive.");
}
