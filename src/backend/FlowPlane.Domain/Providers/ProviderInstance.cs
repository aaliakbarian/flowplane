namespace FlowPlane.Domain.Providers;

public sealed class ProviderInstance
{
    private ProviderInstance()
    {
        ProviderType = null!;
        DisplayName = null!;
        Configuration = null!;
        SecretReference = null!;
    }

    private ProviderInstance(
        Guid id,
        ProviderType providerType,
        string displayName,
        bool isEnabled,
        ProviderConfigurationDocument configuration,
        SecretReference secretReference,
        DateTimeOffset now)
    {
        Id = id;
        ProviderType = providerType;
        DisplayName = displayName;
        IsEnabled = isEnabled;
        Configuration = configuration;
        SecretReference = secretReference;
        Version = 1;
        CreatedAtUtc = now;
        UpdatedAtUtc = now;
    }

    public Guid Id { get; private set; }

    public ProviderType ProviderType { get; private set; }

    public string DisplayName { get; private set; }

    public bool IsEnabled { get; private set; }

    public ProviderConfigurationDocument Configuration { get; private set; }

    public SecretReference SecretReference { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public static ProviderInstance Create(
        Guid id,
        ProviderType providerType,
        string displayName,
        bool isEnabled,
        ProviderConfigurationDocument configuration,
        SecretReference secretReference,
        DateTimeOffset now)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Provider instance identifier must not be empty.", nameof(id));
        }

        ArgumentNullException.ThrowIfNull(providerType);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(secretReference);
        displayName = ValidateDisplayName(displayName);

        if (now == default)
        {
            throw new ArgumentException("Provider instance timestamp must be specified.", nameof(now));
        }

        return new ProviderInstance(
            id,
            providerType,
            displayName,
            isEnabled,
            configuration,
            secretReference,
            now);
    }

    public void UpdateConfiguration(
        string displayName,
        bool isEnabled,
        ProviderConfigurationDocument configuration,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        DisplayName = ValidateDisplayName(displayName);
        IsEnabled = isEnabled;
        Configuration = configuration;
        Version++;
        UpdatedAtUtc = now;
    }

    public void MarkCredentialChanged(long expectedVersion, DateTimeOffset now)
    {
        if (Version != expectedVersion)
        {
            throw new InvalidOperationException("The provider instance was modified concurrently.");
        }

        Version++;
        UpdatedAtUtc = now;
    }

    private static string ValidateDisplayName(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        var trimmed = displayName.Trim();
        if (trimmed.Length > 200 || !string.Equals(trimmed, displayName, StringComparison.Ordinal))
        {
            throw new ArgumentException("Provider instance display name is invalid.", nameof(displayName));
        }

        return displayName;
    }
}
