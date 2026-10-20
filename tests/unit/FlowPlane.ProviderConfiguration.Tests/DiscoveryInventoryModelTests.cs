using FlowPlane.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

namespace FlowPlane.ProviderConfiguration.Tests;

public sealed class DiscoveryInventoryModelTests
{
    [Fact]
    public void Discovery_model_has_exactly_three_inventory_tables_and_same_instance_pointer_constraints()
    {
        using var context = new FlowPlaneDbContext(new DbContextOptionsBuilder<FlowPlaneDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only").Options);
        var tables = context.Model.GetEntityTypes().Where(type => type.GetTableName()!.StartsWith("ProviderDiscovery", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, tables.Length);
        var state = Assert.Single(tables, type => type.GetTableName() == "ProviderDiscoveryStates");
        Assert.Equal(3, state.GetForeignKeys().Count(key => key.Properties.Count == 2));
        Assert.All(state.GetForeignKeys().Where(key => key.Properties.Count == 2), key =>
            Assert.Equal("ProviderInstanceId", key.Properties[0].Name));
        var generation = Assert.Single(tables, type => type.GetTableName() == "ProviderDiscoveryGenerations");
        Assert.Contains(generation.GetIndexes(), index => index.IsUnique && index.GetFilter() is not null);
        var observations = Assert.Single(tables, type => type.GetTableName() == "ProviderDiscoveryObservations");
        Assert.True(observations.FindProperty("MetadataJson")!.IsNullable);
        Assert.Equal("jsonb", observations.FindProperty("MetadataJson")!.GetColumnType());
        Assert.All(observations.GetForeignKeys(), key => Assert.Equal(DeleteBehavior.Restrict, key.DeleteBehavior));
    }
}
