namespace FlowPlane.Api.Authentication;

internal sealed class FlowPlaneAuthenticationOptions
{
    public const string SectionName = "FlowPlane:Authentication";

    public string Issuer { get; init; } = string.Empty;

    public string MetadataAddress { get; init; } = string.Empty;

    public string Audience { get; init; } = string.Empty;

    public bool RequireHttpsMetadata { get; init; } = true;

    public void Validate(IHostEnvironment environment)
    {
        ValidateAbsoluteUri(Issuer, nameof(Issuer));
        ValidateAbsoluteUri(MetadataAddress, nameof(MetadataAddress));

        if (string.IsNullOrWhiteSpace(Audience))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(Audience)} must be configured.");
        }

        if (!RequireHttpsMetadata && !environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(RequireHttpsMetadata)} may be false only in Development.");
        }
    }

    private static void ValidateAbsoluteUri(string value, string propertyName)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{propertyName} must be an absolute URI.");
        }
    }
}
