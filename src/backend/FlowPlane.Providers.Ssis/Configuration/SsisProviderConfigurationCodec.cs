using System.Text.Json;
using System.Text.Json.Serialization;

using FlowPlane.Application.Providers;
using FlowPlane.Domain.Providers;

namespace FlowPlane.Providers.Ssis.Configuration;

public sealed class SsisProviderConfigurationCodec :
    ProviderConfigurationCodec,
    IProviderConfigurationCodec<SsisProviderConfiguration>
{
    public const string SchemaId = "ssis.provider-instance/v1";

    public static readonly ProviderType SsisProviderType = ProviderType.Parse("ssis");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
    };

    ProviderType IProviderConfigurationCodec<SsisProviderConfiguration>.ProviderType => SsisProviderType;

    string IProviderConfigurationCodec<SsisProviderConfiguration>.SchemaId => SchemaId;

    public ProviderConfigurationDocument DecodeInput(string schemaId, string json)
    {
        if (!string.Equals(schemaId, SchemaId, StringComparison.Ordinal))
        {
            throw new ProviderConfigurationException("The SSIS provider configuration schema is unsupported.");
        }

        SsisProviderConfigurationInput? input;
        try
        {
            input = JsonSerializer.Deserialize<SsisProviderConfigurationInput>(json, SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new ProviderConfigurationException("The SSIS provider configuration is invalid.", exception);
        }

        if (input is null)
        {
            throw new ProviderConfigurationException("The SSIS provider configuration is invalid.");
        }

        return Encode(new SsisProviderConfiguration(
            input.Host ?? string.Empty,
            input.Port,
            input.Username ?? string.Empty,
            input.TrustServerCertificate));
    }

    public ProviderConfigurationDocument Encode(SsisProviderConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Validate(configuration);

        var canonicalJson = JsonSerializer.Serialize(configuration, SerializerOptions);
        return CreateValidatedDocument(SchemaId, canonicalJson);
    }

    public SsisProviderConfiguration Decode(ProviderConfigurationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!string.Equals(document.SchemaId, SchemaId, StringComparison.Ordinal))
        {
            throw new ProviderConfigurationException("The SSIS provider configuration schema is unsupported.");
        }

        try
        {
            var configuration = JsonSerializer.Deserialize<SsisProviderConfiguration>(
                document.CanonicalJson,
                SerializerOptions) ?? throw new ProviderConfigurationException(
                    "The SSIS provider configuration is invalid.");
            Validate(configuration);
            return configuration;
        }
        catch (JsonException exception)
        {
            throw new ProviderConfigurationException("The SSIS provider configuration is invalid.", exception);
        }
    }

    private static void Validate(SsisProviderConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.Host) ||
            configuration.Host.Length > 255 ||
            !string.Equals(configuration.Host, configuration.Host.Trim(), StringComparison.Ordinal) ||
            configuration.Host.Any(character => char.IsControl(character) || char.IsWhiteSpace(character) || character == ';'))
        {
            throw new ProviderConfigurationException("The SSIS host is invalid.");
        }

        if (configuration.Port is < 1 or > 65535)
        {
            throw new ProviderConfigurationException("The SSIS port is invalid.");
        }

        if (string.IsNullOrWhiteSpace(configuration.Username) ||
            configuration.Username.Length > 128 ||
            !string.Equals(configuration.Username, configuration.Username.Trim(), StringComparison.Ordinal) ||
            configuration.Username.Any(char.IsControl))
        {
            throw new ProviderConfigurationException("The SSIS username is invalid.");
        }
    }

    private sealed record SsisProviderConfigurationInput(
        string? Host,
        int Port = 1433,
        string? Username = null,
        bool TrustServerCertificate = false);
}
