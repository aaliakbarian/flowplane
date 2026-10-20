using System.Text;
using System.Text.Json;

namespace FlowPlane.Application.Providers;

public sealed class ProviderDiscoveryMetadataDocument
{
    private ProviderDiscoveryMetadataDocument(string schemaId, string canonicalJson)
    {
        SchemaId = schemaId;
        CanonicalJson = canonicalJson;
    }

    public string SchemaId { get; }

    public string CanonicalJson { get; }

    internal static ProviderDiscoveryMetadataDocument CreateValidated(
        string schemaId,
        string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaId);
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(document.RootElement, writer);
        }

        return new ProviderDiscoveryMetadataDocument(
            schemaId,
            Encoding.UTF8.GetString(stream.ToArray()));
    }

    public override string ToString() => $"ProviderDiscoveryMetadataDocument({SchemaId})";

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(
                         property => property.Name,
                         StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(property.Value, writer);
            }

            writer.WriteEndObject();
            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray())
            {
                WriteCanonical(item, writer);
            }

            writer.WriteEndArray();
            return;
        }

        element.WriteTo(writer);
    }
}

public abstract class ProviderDiscoveryMetadataCodec
{
    protected static ProviderDiscoveryMetadataDocument CreateValidatedDocument(
        string schemaId,
        string canonicalJson) =>
        ProviderDiscoveryMetadataDocument.CreateValidated(schemaId, canonicalJson);
}
