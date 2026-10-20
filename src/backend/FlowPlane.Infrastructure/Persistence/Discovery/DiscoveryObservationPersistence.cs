using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

using FlowPlane.Application.Providers;
using FlowPlane.Application.Providers.DiscoveryInventory;
using FlowPlane.Domain.Providers;

namespace FlowPlane.Infrastructure.Persistence.Discovery;

internal static class DiscoveryObservationPersistence
{
    public static DiscoveryObservationRecord Encode(ProviderDiscoveryObservation observation,
        DiscoveryGeneration generation, DateTimeOffset receivedAt)
    {
        if (observation.ProviderInstanceId != generation.ProviderInstanceId || string.IsNullOrWhiteSpace(observation.NativeKey)
            || observation.Metadata.SchemaId.Length > 256 || observation.ExecutableCandidate is { } reference &&
            (reference.ProviderInstanceId != generation.ProviderInstanceId || reference.ProviderType.Value != generation.ProviderType || reference.SchemaId.Length > 256))
            throw new DiscoveryInventoryException(DiscoveryInventoryError.InvalidObservation);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, observation.Metadata.SchemaId);
        Append(hash, observation.Metadata.CanonicalJson);
        Append(hash, observation.ExecutableCandidate?.SchemaId ?? "");
        Append(hash, observation.ExecutableCandidate?.CanonicalJson ?? "");
        return new DiscoveryObservationRecord
        {
            ProviderInstanceId = generation.ProviderInstanceId,
            RefreshId = generation.RefreshId,
            NativeKey = observation.NativeKey,
            NativeKeyDigest = SHA256.HashData(Encoding.UTF8.GetBytes(observation.NativeKey)),
            SchemaId = observation.Metadata.SchemaId,
            MetadataJson = observation.Metadata.CanonicalJson,
            CandidateSchemaId = observation.ExecutableCandidate?.SchemaId,
            CandidateJson = observation.ExecutableCandidate?.CanonicalJson,
            ContentFingerprint = hash.GetHashAndReset(),
            Visibility = DiscoveryObservationVisibility.Observed,
            LastObservedAtUtc = receivedAt,
            ObservedAccessContextId = generation.AccessContextId,
            ObservedProviderVersion = generation.ProviderVersion,
            ObservedSecretVersion = generation.ManagedSecretVersion,
        };
    }

    public static void RejectExistingKey(DiscoveryObservationRecord row, string existingKey) =>
        throw new DiscoveryInventoryException(string.Equals(row.NativeKey, existingKey, StringComparison.Ordinal)
            ? DiscoveryInventoryError.DuplicateNativeKey : DiscoveryInventoryError.NativeKeyCollision);

    public static StoredDiscoveryObservation Decode(DiscoveryObservationRecord row, string providerType) => new(
        row.NativeKey, row.SchemaId, row.Visibility,
        row.MetadataJson is null ? null : ProviderDiscoveryMetadataDocument.CreateValidated(row.SchemaId, row.MetadataJson),
        row.CandidateJson is null ? null : NativeDefinitionReference.CreateValidated(row.ProviderInstanceId,
            ProviderType.Parse(providerType), row.CandidateSchemaId!, row.CandidateJson), row.LastObservedAtUtc);

    public static int Size(DiscoveryObservationRecord row) => checked(256 + Encoding.UTF8.GetByteCount(row.NativeKey)
        + Encoding.UTF8.GetByteCount(row.MetadataJson ?? "") + Encoding.UTF8.GetByteCount(row.CandidateJson ?? ""));

    private static void Append(IncrementalHash hash, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
