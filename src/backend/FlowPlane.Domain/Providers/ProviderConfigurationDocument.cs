using System.Text;
using System.Text.Json;

namespace FlowPlane.Domain.Providers;

public sealed class ProviderConfigurationDocument : IEquatable<ProviderConfigurationDocument>
{
    private ProviderConfigurationDocument(string schemaId, string canonicalJson)
    {
        SchemaId = schemaId;
        CanonicalJson = canonicalJson;
    }

    public string SchemaId { get; }

    public string CanonicalJson { get; }

    internal static ProviderConfigurationDocument CreateValidated(
        string schemaId,
        string canonicalJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaId);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalJson);
        return new ProviderConfigurationDocument(schemaId, Canonicalize(canonicalJson));
    }

    public bool Equals(ProviderConfigurationDocument? other) =>
        other is not null &&
        string.Equals(SchemaId, other.SchemaId, StringComparison.Ordinal) &&
        string.Equals(CanonicalJson, other.CanonicalJson, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as ProviderConfigurationDocument);

    public override int GetHashCode() => HashCode.Combine(
        StringComparer.Ordinal.GetHashCode(SchemaId),
        StringComparer.Ordinal.GetHashCode(CanonicalJson));

    public override string ToString() => $"ProviderConfigurationDocument({SchemaId})";

    private static string Canonicalize(string json)
    {
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(document.RootElement, writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

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
