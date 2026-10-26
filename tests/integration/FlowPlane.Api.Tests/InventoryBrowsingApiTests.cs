using System.Net;
using FlowPlane.Application.Providers.DiscoveryInventory;
using Microsoft.Extensions.DependencyInjection;

namespace FlowPlane.Api.Tests;

public sealed class InventoryBrowsingApiTests
{
    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void Cursor_expiry_is_exclusive_at_the_encoded_boundary(long ticksFromExpiry, bool accepted)
    {
        var issued = DateTimeOffset.Parse("2030-01-01T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var clock = new Clock { UtcNow = issued };
        using var api = new ProviderApiFactory(configureServices: services => services.AddSingleton<TimeProvider>(clock));
        var codec = api.Services.GetRequiredService<IInventoryCursorCodec>();
        var state = new InventoryCursor(Guid.NewGuid(), Guid.NewGuid(), null, false, 50, new string('A', 64));
        var cursor = codec.Encode(state);
        clock.UtcNow = issued.AddMinutes(30).AddTicks(ticksFromExpiry);
        if (accepted) Assert.Equal(state, codec.Decode(cursor));
        else Assert.Equal(InventoryBrowseError.InvalidCursor, Assert.Throws<InventoryBrowseException>(() => codec.Decode(cursor)).Code);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("reader", HttpStatusCode.Forbidden)]
    public async Task Inventory_route_requires_authentication_and_exact_operator_authorization(string? subject, HttpStatusCode status)
    {
        using var api = new ProviderApiFactory();
        using var client = api.Client(subject);
        using var response = await client.GetAsync($"/provider-instances/{Guid.NewGuid():D}/inventory", TestContext.Current.CancellationToken);
        Assert.Equal(status, response.StatusCode);
    }

    [Fact]
    public void Cursor_is_integrity_protected_bound_and_invalid_after_backend_restart()
    {
        using var first = new ProviderApiFactory();
        using var second = new ProviderApiFactory();
        var codec = first.Services.GetRequiredService<IInventoryCursorCodec>();
        var state = new InventoryCursor(Guid.NewGuid(), Guid.NewGuid(), "flowplane.ssis/discovery-package.v1", true, 50, new string('A', 64));
        var cursor = codec.Encode(state);
        Assert.Equal(state, codec.Decode(cursor));
        Assert.Equal(InventoryBrowseError.InvalidCursor, Assert.Throws<InventoryBrowseException>(() => codec.Decode(cursor[..^3] + "xxx")).Code);
        Assert.Throws<InventoryBrowseException>(() => second.Services.GetRequiredService<IInventoryCursorCodec>().Decode(cursor));
        foreach (var invalid in new[] { "", new string('x', 2049), "..", "not-a-cursor", "a.b.c" })
            Assert.Throws<InventoryBrowseException>(() => codec.Decode(invalid));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; }
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
