using System.Text.Json;

using FlowPlane.Domain.Providers;

namespace FlowPlane.Infrastructure.Persistence;

internal static class ProviderConfigurationPersistence
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Serialize(ProviderConfigurationDocument document)
    {
        using var configuration = JsonDocument.Parse(document.CanonicalJson);
        return JsonSerializer.Serialize(
            new PersistedDocument(document.SchemaId, configuration.RootElement),
            SerializerOptions);
    }

    public static ProviderConfigurationDocument Deserialize(string value)
    {
        var document = JsonSerializer.Deserialize<PersistedDocument>(value, SerializerOptions)
            ?? throw new InvalidOperationException("Persisted provider configuration is invalid.");
        return ProviderConfigurationDocument.CreateValidated(
            document.Schema,
            JsonSerializer.Serialize(document.Configuration, SerializerOptions));
    }

    private sealed record PersistedDocument(string Schema, JsonElement Configuration);
}
