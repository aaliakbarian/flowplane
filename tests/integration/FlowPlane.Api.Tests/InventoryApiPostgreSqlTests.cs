using System.Net;
using System.Text.Json;

using FlowPlane.Application.Providers;
using FlowPlane.Application.Providers.DiscoveryInventory;
using FlowPlane.Application.Secrets;
using FlowPlane.Domain.Providers;
using FlowPlane.Persistence.Tests;
using FlowPlane.Providers.Ssis.Configuration;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

namespace FlowPlane.Api.Tests;

public sealed class InventoryApiPostgreSqlTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Pages_cursors_filters_and_empty_inventory_use_real_published_postgresql_rows()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var client = fixture.Api.Client("operator");
        using (var missing = await client.GetAsync(fixture.Path, Token)) Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        Assert.Equal(0, fixture.Disclosure.Calls);
        await fixture.PublishAsync(105);
        using var first = await client.GetAsync(fixture.Path, Token);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True(first.Headers.CacheControl?.NoStore);
        var doc = JsonDocument.Parse(await first.Content.ReadAsStringAsync(Token));
        Assert.Equal(50, doc.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(51, fixture.Disclosure.LastCount); // Authorize the lookahead before revealing nextCursor.
        var cursor = doc.RootElement.GetProperty("nextCursor").GetString()!;
        var codec = fixture.Api.Services.GetRequiredService<IInventoryCursorCodec>();
        var continuation = codec.Decode(cursor);
        foreach (var invalidBinding in new[] { continuation with { ProviderInstanceId = Guid.NewGuid() }, continuation with { SchemaId = "fixture/page.v1" }, continuation with { Ordering = "other-order" } })
        {
            using var rejected = await client.GetAsync(fixture.Path + "?cursor=" + Uri.EscapeDataString(codec.Encode(invalidBinding)), Token);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
        using var second = await client.GetAsync(fixture.Path + "?cursor=" + Uri.EscapeDataString(cursor), Token);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var wrongFilter = await client.GetAsync(fixture.Path + "?candidatesOnly=true&cursor=" + Uri.EscapeDataString(cursor), Token);
        Assert.Equal(HttpStatusCode.BadRequest, wrongFilter.StatusCode);
        using var wrongLimit = await client.GetAsync(fixture.Path + "?limit=100&cursor=" + Uri.EscapeDataString(cursor), Token);
        Assert.Equal(HttpStatusCode.BadRequest, wrongLimit.StatusCode);
        using var tampered = await client.GetAsync(fixture.Path + "?cursor=" + Uri.EscapeDataString(cursor[..^3] + "xxx"), Token);
        Assert.Equal(HttpStatusCode.BadRequest, tampered.StatusCode);
        using var candidates = await client.GetAsync(fixture.Path + "?candidatesOnly=true", Token);
        Assert.Equal(0, JsonDocument.Parse(await candidates.Content.ReadAsStringAsync(Token)).RootElement.GetProperty("items").GetArrayLength());
        using var maximum = await client.GetAsync(fixture.Path + "?limit=100", Token);
        Assert.Equal(100, JsonDocument.Parse(await maximum.Content.ReadAsStringAsync(Token)).RootElement.GetProperty("items").GetArrayLength());
        foreach (var invalid in new[] { "limit=0", "limit=-1", "limit=101", "limit=2147483647", "limit=abc", "schema=unapproved", "candidatesOnly=1", "cursor=", "unknown=x", "limit=1&limit=2" })
        {
            using var rejected = await client.GetAsync(fixture.Path + "?" + invalid, Token);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
        await fixture.PublishAsync(0);
        using var obsolete = await client.GetAsync(fixture.Path + "?cursor=" + Uri.EscapeDataString(cursor), Token);
        Assert.Equal(HttpStatusCode.Conflict, obsolete.StatusCode);
        using var empty = await client.GetAsync(fixture.Path, Token);
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        var json = await empty.Content.ReadAsStringAsync(Token);
        Assert.Equal(0, JsonDocument.Parse(json).RootElement.GetProperty("items").GetArrayLength());
        Assert.DoesNotContain(fixture.Password, json, StringComparison.Ordinal);
        Assert.DoesNotContain("flowplane:", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Denied_lookahead_rejects_the_whole_page_and_continuation()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.PublishAsync(51);
        fixture.Disclosure.BeforeReturn = () =>
        {
            Assert.Equal(51, fixture.Disclosure.LastCount);
            throw new InventoryBrowseException(InventoryBrowseError.NativeChanged);
        };
        using var client = fixture.Api.Client("operator");
        using var response = await client.GetAsync(fixture.Path, Token);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain("items", text, StringComparison.Ordinal);
        Assert.DoesNotContain("nextCursor", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Current_endpoint_policy_blocks_native_IO_even_for_a_previously_published_provider()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.PublishAsync(1);
        await fixture.Services.GetRequiredService<IProviderInstanceStore>().UpdateAsync(fixture.Instance.Id, 1, "Moved", true,
            new SsisProviderConfigurationCodec().Encode(new("not-allowed.example.test", 1433, "synthetic", false)), DateTimeOffset.UtcNow, Token);
        using var client = fixture.Api.Client("operator");
        using var response = await client.GetAsync(fixture.Path, Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, fixture.Disclosure.Calls);
    }

    [Theory]
    [InlineData("publish")]
    [InlineData("secret")]
    [InlineData("configuration")]
    public async Task Context_changes_during_native_IO_reject_the_entire_page_without_fallback(string change)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.PublishAsync(1);
        fixture.Disclosure.BeforeReturn = async () =>
        {
            await using var connection = new NpgsqlConnection(fixture.Database.ConnectionString);
            await connection.OpenAsync(Token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND state='idle in transaction'";
            Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync(Token)));
            if (change == "publish") await fixture.PublishAsync(2);
            else if (change == "secret")
                await fixture.Services.GetRequiredService<IManagedSecretStore>().ReplaceAsync(fixture.Instance.SecretReference, "synthetic-replacement".AsMemory(), Token);
            else await fixture.Services.GetRequiredService<IProviderInstanceStore>().UpdateAsync(fixture.Instance.Id, 1, "Changed", false, fixture.Instance.Configuration, DateTimeOffset.UtcNow, Token);
        };
        using var client = fixture.Api.Client("operator");
        using var response = await client.GetAsync(fixture.Path, Token);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain("native-fixture", text, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Password, text, StringComparison.Ordinal);
        Assert.DoesNotContain("items", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(InventoryBrowseError.NativeChanged, HttpStatusCode.Conflict)]
    [InlineData(InventoryBrowseError.NativeDenied, HttpStatusCode.Forbidden)]
    [InlineData(InventoryBrowseError.NativeUnavailable, HttpStatusCode.ServiceUnavailable)]
    public async Task Native_failures_never_fall_back_to_cached_rows(InventoryBrowseError error, HttpStatusCode expected)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.PublishAsync(1);
        fixture.Disclosure.BeforeReturn = () => throw new InventoryBrowseException(error);
        using var client = fixture.Api.Client("operator");
        using var response = await client.GetAsync(fixture.Path, Token);
        Assert.Equal(expected, response.StatusCode);
        Assert.DoesNotContain("items", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Password, fixture.Api.Logs.Text, StringComparison.Ordinal);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required PostgreSqlTestDatabase Database { get; init; }
        public required ServiceProvider Root { get; init; }
        public required AsyncServiceScope Scope { get; init; }
        public IServiceProvider Services => Scope.ServiceProvider;
        public required ProviderInstance Instance { get; init; }
        public required ProviderApiFactory Api { get; init; }
        public required Disclosure Disclosure { get; init; }
        public required string Directory { get; init; }
        public required string Password { get; init; }
        public string Path => $"/provider-instances/{Instance.Id:D}/inventory";
        public static async Task<Fixture> CreateAsync()
        {
            Assert.SkipUnless(Environment.GetEnvironmentVariable("FLOWPLANE_TEST_DISCOVERY_MIGRATION_APPROVED") == "true", "Enable isolated migrations.");
            var database = await PostgreSqlTestDatabase.CreateAsync(Token);
            Assert.SkipWhen(database is null, "Configure isolated PostgreSQL 18.");
            var directory = System.IO.Directory.CreateTempSubdirectory("flowplane-stage3-").FullName;
            var certificate = PostgreSqlTestDatabase.CreateRootCertificate(directory, "synthetic-root");
            var root = database!.CreateServices(certificate);
            await database.MigrateAsync(root, Token);
            var scope = root.CreateAsyncScope();
            var password = "synthetic-stage3-" + Guid.NewGuid().ToString("N");
            var instance = await scope.ServiceProvider.GetRequiredService<IProviderInstanceStore>().CreateWithManagedSecretAsync(Guid.CreateVersion7(), ProviderType.Parse("ssis"), "Fixture", true,
                new SsisProviderConfigurationCodec().Encode(new("sql.example.test", 1433, "synthetic", false)), password.AsMemory(), DateTimeOffset.UtcNow, Token);
            var disclosure = new Disclosure();
            var api = new ProviderApiFactory(settings: new Dictionary<string, string?> { ["ConnectionStrings:FlowPlane"] = database.ConnectionString }, configureServices: services =>
            { services.RemoveAll<IProviderInventoryDisclosure>(); services.AddSingleton<IProviderInventoryDisclosure>(disclosure); });
            return new() { Database = database, Root = root, Scope = scope, Instance = instance, Api = api, Disclosure = disclosure, Directory = directory, Password = password };
        }
        public async Task PublishAsync(int count)
        {
            var store = Services.GetRequiredService<IProviderDiscoveryInventoryStore>();
            await using var refresh = await store.BeginRefreshAsync(Instance.Id, Token);
            for (var i = 0; i < count; i++) await refresh.Sink.AppendAsync(new(Instance.Id, JsonSerializer.Serialize(new[] { "native-fixture", i.ToString(System.Globalization.CultureInfo.InvariantCulture) }), new Metadata().Encode(), null), Token);
            var now = DateTimeOffset.UtcNow;
            await refresh.PublishAsync(new(ProviderDiscoveryOutcome.Succeeded, true, ProviderDiscoverySourceCompatibility.Compatible, count, 0, 0, now.AddSeconds(-1), now, []), Token);
        }
        public async ValueTask DisposeAsync() { Api.Dispose(); await Scope.DisposeAsync(); await Root.DisposeAsync(); await Database.DisposeAsync(); System.IO.Directory.Delete(Directory, true); }
    }
    private sealed class Metadata : ProviderDiscoveryMetadataCodec { public ProviderDiscoveryMetadataDocument Encode() => CreateValidatedDocument("fixture/page.v1", "{\"safe\":true}"); }
    private sealed class Disclosure : IProviderInventoryDisclosure
    {
        public int Calls { get; private set; }
        public int LastCount { get; private set; }
        public Func<Task>? BeforeReturn { get; set; }
        public ProviderType ProviderType => ProviderType.Parse("ssis");
        public IReadOnlyList<string> SchemaIds => ["fixture/page.v1"];
        public IReadOnlyList<string> RequiredScopeKeys(IReadOnlyList<StoredDiscoveryObservation> observations) => [];
        public async Task VerifyAsync(ProviderInstance provider, IReadOnlyList<StoredDiscoveryObservation> observations, Func<CancellationToken, Task<bool>> current, CancellationToken token)
        { Calls++; LastCount = observations.Count; if (BeforeReturn is not null) await BeforeReturn(); }
        public InventoryItem Project(Guid id, StoredDiscoveryObservation observation) => new(observation.SchemaId, JsonDocument.Parse(observation.NativeKey).RootElement.Clone(), JsonSerializer.SerializeToElement(new { safe = true }), null);
    }
}
