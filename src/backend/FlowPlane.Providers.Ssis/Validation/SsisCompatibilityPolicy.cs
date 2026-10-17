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
    private static readonly HashSet<int> RecognizedProjectFormats = [1];
    private static readonly HashSet<int> RecognizedPackageFormats = [8];

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

    public static bool HasUnknownProjectFormat(IEnumerable<int> formats) =>
        HasUnknownFormat(formats, RecognizedProjectFormats);

    public static bool HasUnknownPackageFormat(IEnumerable<int> formats) =>
        HasUnknownFormat(formats, RecognizedPackageFormats);

    private static bool HasUnknownFormat(
        IEnumerable<int> formats,
        IReadOnlySet<int> recognizedFormats)
    {
        ArgumentNullException.ThrowIfNull(formats);
        return formats.Any(format => !recognizedFormats.Contains(format));
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
