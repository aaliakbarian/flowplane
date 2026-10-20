using System.Text.Json;
using FlowPlane.Application.Providers;
using FlowPlane.Application.Providers.DiscoveryInventory;
using FlowPlane.Domain.Providers;
using FlowPlane.Infrastructure.Persistence;
using FlowPlane.Infrastructure.Persistence.Discovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FlowPlane.Persistence.Tests;

public sealed class DiscoveryInventoryRoundTripTests
{
    [Fact]
    public async Task Real_jsonb_round_trips_preserve_exact_native_identity_and_do_not_manufacture_changes()
    {
        ProviderDiscoveryInventoryTests.RequireMigrationApproval();
        var token = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        Assert.SkipWhen(database is null, "Configure isolated PostgreSQL 18.");
        await using var services = database!.CreateServices(null);
        await database.MigrateAsync(services, token);
        var id = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, token);
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderDiscoveryInventoryStore>();
        var observations = new[] { "项目/Package", "项目/package", "é", "e\u0301" }.Select(name => new ProviderDiscoveryObservation(id,
            name, new Codec().Encode("{\"z\":9223372036854775807,\"a\":{\"format\":8,\"name\":\"名称\",\"flags\":[true,null,false]}}"),
            new ReferenceCodec().Encode(id, name))).ToArray();
        await using var first = await store.BeginRefreshAsync(id, token);
        foreach (var observation in observations) await first.Sink.AppendAsync(observation, token);
        await first.PublishAsync(ProviderDiscoveryInventoryTests.Summary(4) with { ExecutableCandidates = 4 }, token);
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        var generation = await db.DiscoveryGenerations.AsNoTracking().SingleAsync(row => row.RefreshId == first.RefreshId, token);
        var raw = await db.DiscoveryObservations.AsNoTracking().Where(row => row.RefreshId == first.RefreshId).ToListAsync(token);
        Assert.Equal(4, raw.Select(row => Convert.ToHexString(row.NativeKeyDigest)).Distinct().Count());
        Assert.Contains(raw, row => row.MetadataJson != observations[0].Metadata.CanonicalJson); // Actual JSONB text is not canonical text.
        await using var next = await store.BeginRefreshAsync(id, token);
        foreach (var row in raw)
        {
            var restored = DiscoveryObservationPersistence.Decode(row, "ssis");
            var original = Assert.Single(observations, item => item.NativeKey == row.NativeKey);
            Assert.Equal(original.Metadata.CanonicalJson, restored.Metadata!.CanonicalJson);
            Assert.Equal(original.ExecutableCandidate, restored.ExecutableCandidate);
            var encoded = DiscoveryObservationPersistence.Encode(new(id, restored.NativeKey, restored.Metadata, restored.ExecutableCandidate), generation, DateTimeOffset.UtcNow);
            Assert.Equal(row.ContentFingerprint, encoded.ContentFingerprint);
            await next.Sink.AppendAsync(new(id, restored.NativeKey, restored.Metadata, restored.ExecutableCandidate), token);
        }
        await next.PublishAsync(ProviderDiscoveryInventoryTests.Summary(4) with { ExecutableCandidates = 4 }, token);
        var counters = await db.DiscoveryGenerations.AsNoTracking().SingleAsync(row => row.RefreshId == next.RefreshId, token);
        Assert.Equal(4, counters.UnchangedCount);
        Assert.Equal(0, counters.AddedCount);
        Assert.Equal(0, counters.ChangedCount);
    }

    [Fact]
    public async Task Colliding_index_digest_cannot_alias_a_different_native_identity()
    {
        ProviderDiscoveryInventoryTests.RequireMigrationApproval();
        var token = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        Assert.SkipWhen(database is null, "Configure isolated PostgreSQL 18.");
        await using var services = database!.CreateServices(null);
        await database.MigrateAsync(services, token);
        var id = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, token);
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderDiscoveryInventoryStore>();
        await using var first = await store.BeginRefreshAsync(id, token);
        await first.Sink.AppendAsync(ProviderDiscoveryInventoryTests.Observation(id, "native-key"), token);
        await first.PublishAsync(ProviderDiscoveryInventoryTests.Summary(1), token);
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        // Controlled database corruption simulates an actual equal digest/different key, without weakening SHA-256.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"ProviderDiscoveryObservations\" SET \"NativeKey\" = 'different-native-key' WHERE \"RefreshId\" = {first.RefreshId}", token);
        await using var next = await store.BeginRefreshAsync(id, token);
        await next.Sink.AppendAsync(ProviderDiscoveryInventoryTests.Observation(id, "native-key"), token);
        var failure = await Assert.ThrowsAsync<DiscoveryInventoryException>(() => next.PublishAsync(ProviderDiscoveryInventoryTests.Summary(1), token));
        Assert.Equal(DiscoveryInventoryError.NativeKeyCollision, failure.Code);
        Assert.False(failure.ToString().Contains("native-key", StringComparison.Ordinal));
        await next.AbortAsync(failure.Code, token);
        Assert.Equal(first.RefreshId, (await db.DiscoveryStates.AsNoTracking().SingleAsync(token)).CurrentGenerationId);
    }

    private sealed class Codec : ProviderDiscoveryMetadataCodec
    {
        public ProviderDiscoveryMetadataDocument Encode(string json) => CreateValidatedDocument("fixture/roundtrip.v1", json);
    }
    private sealed class ReferenceCodec : NativeDefinitionReferenceCodec
    {
        public NativeDefinitionReference Encode(Guid id, string name) => CreateValidatedReference(id, ProviderType.Parse("ssis"),
            "fixture/native.v1", JsonSerializer.Serialize(new { folderName = "Folder", projectName = "Project", packageName = name }));
    }
}
