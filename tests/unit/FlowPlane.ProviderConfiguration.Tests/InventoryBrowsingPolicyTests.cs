using FlowPlane.Application.Providers.DiscoveryInventory;

namespace FlowPlane.ProviderConfiguration.Tests;

public sealed class InventoryBrowsingPolicyTests
{
    [Fact]
    public void Defaults_are_bounded_and_schema_filter_is_exact()
    {
        var query = new InventoryPageQuery();
        Assert.Equal(50, query.Limit);
        query.Validate(["schema/v1"]);
        new InventoryPageQuery("schema/v1", true, 100).Validate(["schema/v1"]);
        Assert.Throws<InventoryBrowseException>(() => new InventoryPageQuery("SCHEMA/v1").Validate(["schema/v1"]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    public void Invalid_limits_cannot_become_unbounded(int limit) =>
        Assert.Equal(InventoryBrowseError.InvalidRequest,
            Assert.Throws<InventoryBrowseException>(() => new InventoryPageQuery(Limit: limit).Validate(["schema/v1"])).Code);
}
