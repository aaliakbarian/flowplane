namespace FlowPlane.Providers.Ssis.Validation;

internal enum SsisServerVersionCompatibility
{
    Supported,
    Unverified,
    Unsupported,
}

internal static class SsisCompatibilityPolicy
{
    private static readonly HashSet<int> SupportedMajorVersions = [];
    private static readonly HashSet<int> RecognizedTargetMajorVersions = [15, 16, 17];
    private static readonly HashSet<int> SupportedDefinitionFormats = [15, 16, 17];

    public static SsisServerVersionCompatibility EvaluateServerVersion(string productVersion)
    {
        if (!Version.TryParse(productVersion, out var version))
        {
            return SsisServerVersionCompatibility.Unsupported;
        }

        if (SupportedMajorVersions.Contains(version.Major))
        {
            return SsisServerVersionCompatibility.Supported;
        }

        return RecognizedTargetMajorVersions.Contains(version.Major)
            ? SsisServerVersionCompatibility.Unverified
            : SsisServerVersionCompatibility.Unsupported;
    }

    public static bool HasUnknownDefinitionFormat(IEnumerable<int> formats)
    {
        ArgumentNullException.ThrowIfNull(formats);
        return formats.Any(format => !SupportedDefinitionFormats.Contains(format));
    }
}

internal enum SsisCatalogAccess
{
    Missing,
    InaccessibleOrUnavailable,
    Available,
    Unexpected,
}

internal static class SsisCatalogAccessMapper
{
    public static SsisCatalogAccess Map(int? hasDbAccess) => hasDbAccess switch
    {
        null => SsisCatalogAccess.Missing,
        0 => SsisCatalogAccess.InaccessibleOrUnavailable,
        1 => SsisCatalogAccess.Available,
        _ => SsisCatalogAccess.Unexpected,
    };
}
