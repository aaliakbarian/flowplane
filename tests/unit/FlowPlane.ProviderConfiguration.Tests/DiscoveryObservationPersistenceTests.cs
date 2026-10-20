using FlowPlane.Application.Providers;
using FlowPlane.Application.Providers.DiscoveryInventory;
using FlowPlane.Infrastructure.Persistence.Discovery;
using FlowPlane.Domain.Providers;

namespace FlowPlane.ProviderConfiguration.Tests;

public sealed class DiscoveryObservationPersistenceTests
{
    [Fact]
    public void Native_keys_preserve_ordinal_Unicode_identity_and_unambiguous_fingerprints()
    {
        var first = Make("[\"项目\",\"A\"]", "{\"revision\":1}");
        var other = Make("[\"项目\",\"a\"]", "{\"revision\":1}");
        Assert.NotEqual(first.NativeKeyDigest, other.NativeKeyDigest);
        Assert.Equal("[\"项目\",\"A\"]", first.NativeKey);
        Assert.Equal(32, first.ContentFingerprint!.Length);
        Assert.NotEqual(first.ContentFingerprint, Make(first.NativeKey, "{\"revision\":2}").ContentFingerprint);
    }

    [Fact]
    public void Digest_collisions_and_duplicate_native_keys_are_rejected_without_disclosing_keys()
    {
        var record = Make("confidential-native-name", "{}");
        var duplicate = Assert.Throws<DiscoveryInventoryException>(() => DiscoveryObservationPersistence.RejectExistingKey(record, record.NativeKey));
        Assert.Equal(DiscoveryInventoryError.DuplicateNativeKey, duplicate.Code);
        var collision = Assert.Throws<DiscoveryInventoryException>(() => DiscoveryObservationPersistence.RejectExistingKey(record, "different-key"));
        Assert.Equal(DiscoveryInventoryError.NativeKeyCollision, collision.Code);
        Assert.DoesNotContain(record.NativeKey, collision.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Jsonb_key_order_round_trip_preserves_reference_equality_and_content_fingerprint()
    {
        var id = Guid.NewGuid();
        var generation = new DiscoveryGeneration
        {
            ProviderInstanceId = id,
            RefreshId = Guid.NewGuid(),
            ProviderVersion = 1,
            AccessContextId = Guid.NewGuid(),
            ProviderType = "fixture"
        };
        var reference = new ReferenceCodec().Encode(id);
        var before = DiscoveryObservationPersistence.Encode(new(id, "native", new Codec().Encode("{\"z\":2,\"a\":\"名称\"}"), reference), generation, DateTimeOffset.UtcNow);
        before.MetadataJson = "{\"z\": 2, \"a\": \"名称\"}";
        before.CandidateJson = "{\"z\": 1, \"name\": \"包\"}";
        var restored = DiscoveryObservationPersistence.Decode(before, "fixture");
        Assert.Equal(reference, restored.ExecutableCandidate);
        var after = DiscoveryObservationPersistence.Encode(new(id, restored.NativeKey, restored.Metadata!, restored.ExecutableCandidate), generation, DateTimeOffset.UtcNow);
        Assert.Equal(before.ContentFingerprint, after.ContentFingerprint);
    }

    private sealed class ReferenceCodec : NativeDefinitionReferenceCodec
    {
        public NativeDefinitionReference Encode(Guid id) => CreateValidatedReference(id, ProviderType.Parse("fixture"), "fixture/native.v1", "{\"name\":\"包\",\"z\":1}");
    }

    private static DiscoveryObservationRecord Make(string key, string json)
    {
        var generation = new DiscoveryGeneration
        {
            ProviderInstanceId = Guid.NewGuid(),
            RefreshId = Guid.NewGuid(),
            ProviderVersion = 1,
            AccessContextId = Guid.NewGuid(),
            ProviderType = "fixture"
        };
        return DiscoveryObservationPersistence.Encode(new(generation.ProviderInstanceId, key, new Codec().Encode(json), null), generation, DateTimeOffset.UtcNow);
    }

    private sealed class Codec : ProviderDiscoveryMetadataCodec
    {
        public ProviderDiscoveryMetadataDocument Encode(string json) => CreateValidatedDocument("fixture/metadata.v1", json);
    }
}
