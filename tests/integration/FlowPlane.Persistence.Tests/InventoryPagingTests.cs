using FlowPlane.Application.Providers.DiscoveryInventory;

using Microsoft.Extensions.DependencyInjection;

namespace FlowPlane.Persistence.Tests;

public sealed class InventoryPagingTests
{
    [Fact]
    public async Task Keyset_pages_are_bounded_unique_and_generation_bound_under_real_postgresql()
    {
        ProviderDiscoveryInventoryTests.RequireMigrationApproval();
        var token = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        Assert.SkipWhen(database is null, "Configure isolated PostgreSQL 18.");
        await using var services = database!.CreateServices(null);
        await database.MigrateAsync(services, token);
        var id = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, token);
        await using var scope = services.CreateAsyncScope();
        var inventory = scope.ServiceProvider.GetRequiredService<IProviderDiscoveryInventoryStore>();
        var pages = scope.ServiceProvider.GetRequiredService<IProviderInventoryPageStore>();
        await using var refresh = await inventory.BeginRefreshAsync(id, token);
        for (var i = 0; i < 135; i++) await refresh.Sink.AppendAsync(ProviderDiscoveryInventoryTests.Observation(id, $"native-名称-{i}"), token);
        await refresh.PublishAsync(ProviderDiscoveryInventoryTests.Summary(135), token);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? after = null;
        InventoryReadContext? context = null;
        do
        {
            await using var reader = await pages.OpenPageReaderAsync(id, 1, refresh.RefreshId, token);
            context = reader.Context;
            var page = await reader.ReadPageAsync(new(Limit: 50), ["fixture/observation.v1"], after, token);
            Assert.InRange(page.Observations.Count, 1, 50);
            foreach (var row in page.Observations) Assert.True(seen.Add(row.NativeKey));
            after = page.NextDigest;
        } while (after is not null);
        Assert.Equal(135, seen.Count);
        Assert.True(await pages.IsCurrentAsync(context!, token));
        await using var next = await inventory.BeginRefreshAsync(id, token);
        Assert.False(await pages.IsCurrentAsync(context!, token));
        await next.PublishAsync(ProviderDiscoveryInventoryTests.Summary(0), token);
        var obsolete = await Assert.ThrowsAsync<InventoryBrowseException>(() => pages.OpenPageReaderAsync(id, 1, refresh.RefreshId, token));
        Assert.Equal(InventoryBrowseError.GenerationChanged, obsolete.Code);
        await using var empty = await pages.OpenPageReaderAsync(id, 1, next.RefreshId, token);
        var result = await empty.ReadPageAsync(new(), ["fixture/observation.v1"], null, token);
        Assert.Empty(result.Observations); // NotObserved markers are never caller-visible.
        Assert.Null(result.NextDigest);
    }

    [Fact]
    public async Task Reader_keeps_one_generation_across_queries_while_publication_advances()
    {
        ProviderDiscoveryInventoryTests.RequireMigrationApproval();
        var token = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        Assert.SkipWhen(database is null, "Configure isolated PostgreSQL 18.");
        await using var services = database!.CreateServices(null); await database.MigrateAsync(services, token);
        var id = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, token);
        await using var scope = services.CreateAsyncScope();
        var inventory = scope.ServiceProvider.GetRequiredService<IProviderDiscoveryInventoryStore>();
        var pages = scope.ServiceProvider.GetRequiredService<IProviderInventoryPageStore>();
        await using var first = await inventory.BeginRefreshAsync(id, token);
        await first.Sink.AppendAsync(ProviderDiscoveryInventoryTests.Observation(id, "old"), token);
        await first.PublishAsync(ProviderDiscoveryInventoryTests.Summary(1), token);
        await using var reader = await pages.OpenPageReaderAsync(id, 1, null, token);
        await using var next = await inventory.BeginRefreshAsync(id, token);
        await next.Sink.AppendAsync(ProviderDiscoveryInventoryTests.Observation(id, "new"), token);
        await next.PublishAsync(ProviderDiscoveryInventoryTests.Summary(1), token);
        var page = await reader.ReadPageAsync(new(), ["fixture/observation.v1"], null, token);
        Assert.Equal("old", Assert.Single(page.Observations).NativeKey);
        Assert.Equal("old", Assert.Single(await reader.ReadScopeAsync(["old"], token)).NativeKey);
        Assert.False(await pages.IsCurrentAsync(reader.Context, token));
    }

    [Fact]
    public async Task No_generation_and_failed_refresh_never_offer_cached_fallback()
    {
        ProviderDiscoveryInventoryTests.RequireMigrationApproval();
        var token = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        Assert.SkipWhen(database is null, "Configure isolated PostgreSQL 18.");
        await using var services = database!.CreateServices(null);
        await database.MigrateAsync(services, token);
        var id = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, token);
        await using var scope = services.CreateAsyncScope();
        var pages = scope.ServiceProvider.GetRequiredService<IProviderInventoryPageStore>();
        Assert.Equal(InventoryBrowseError.NotPublished,
            (await Assert.ThrowsAsync<InventoryBrowseException>(() => pages.OpenPageReaderAsync(id, 1, null, token))).Code);
        var inventory = scope.ServiceProvider.GetRequiredService<IProviderDiscoveryInventoryStore>();
        await using var first = await inventory.BeginRefreshAsync(id, token);
        await first.PublishAsync(ProviderDiscoveryInventoryTests.Summary(0), token);
        await using var failed = await inventory.BeginRefreshAsync(id, token);
        await failed.AbortAsync(DiscoveryInventoryError.ContextChanged, token);
        Assert.Equal(InventoryBrowseError.ContextChanged,
            (await Assert.ThrowsAsync<InventoryBrowseException>(() => pages.OpenPageReaderAsync(id, 1, first.RefreshId, token))).Code);
    }
}
