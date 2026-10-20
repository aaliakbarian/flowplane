using System.Text;
using System.Text.Json;

namespace FlowPlane.Domain.Providers;

/// <summary>
/// FlowPlane identity preserves exact canonical names with ordinal equality. A provider's live
/// catalog lookup still follows that catalog's collation; ordinal equality does not change it.
/// </summary>
public sealed class NativeDefinitionReference : IEquatable<NativeDefinitionReference>
{
    private NativeDefinitionReference(
        Guid providerInstanceId,
        ProviderType providerType,
        string schemaId,
        string canonicalJson)
    {
        ProviderInstanceId = providerInstanceId;
        ProviderType = providerType;
        SchemaId = schemaId;
        CanonicalJson = canonicalJson;
    }

    public Guid ProviderInstanceId { get; }

    public ProviderType ProviderType { get; }

    public string SchemaId { get; }

    public string CanonicalJson { get; }

    internal static NativeDefinitionReference CreateValidated(
        Guid providerInstanceId,
        ProviderType providerType,
        string schemaId,
        string json)
    {
        if (providerInstanceId == Guid.Empty)
        {
            throw new ArgumentException(
                "Provider instance identifier must not be empty.",
                nameof(providerInstanceId));
        }

        ArgumentNullException.ThrowIfNull(providerType);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaId);
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return new NativeDefinitionReference(
            providerInstanceId,
            providerType,
            schemaId,
            Canonicalize(json));
    }

    public bool Equals(NativeDefinitionReference? other) =>
        other is not null &&
        ProviderInstanceId == other.ProviderInstanceId &&
        ProviderType.Equals(other.ProviderType) &&
        string.Equals(SchemaId, other.SchemaId, StringComparison.Ordinal) &&
        string.Equals(CanonicalJson, other.CanonicalJson, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as NativeDefinitionReference);

    public override int GetHashCode() => HashCode.Combine(
        ProviderInstanceId,
        ProviderType,
        StringComparer.Ordinal.GetHashCode(SchemaId),
        StringComparer.Ordinal.GetHashCode(CanonicalJson));

    public override string ToString() =>
        $"NativeDefinitionReference({ProviderType.Value}, {SchemaId})";

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
