using FlowPlane.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace FlowPlane.ProviderConfiguration.Tests;

public sealed class DiscoveryHostingMigrationTests
{
    [Fact]
    public void Gate_B_is_additive_generation_only_and_snapshot_matches_without_opening_a_database()
    {
        using var db = new FlowPlaneDbContext(new DbContextOptionsBuilder<FlowPlaneDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=design_only;Username=design_only").Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var assembly = db.GetService<IMigrationsAssembly>();
        Assert.Equal(4, assembly.Migrations.Count);
        var migration = assembly.CreateMigration(assembly.Migrations.Single(pair => pair.Key.EndsWith("_DurableDiscoveryHosting", StringComparison.Ordinal)).Value,
            db.Database.ProviderName!);
        Assert.Empty(migration.UpOperations.OfType<CreateTableOperation>());
        Assert.Empty(migration.UpOperations.OfType<DropTableOperation>());
        Assert.All(migration.UpOperations, operation => Assert.Equal("ProviderDiscoveryGenerations",
            operation.GetType().GetProperty("Table")?.GetValue(operation)));
        var slot = Assert.Single(migration.UpOperations.OfType<CreateIndexOperation>(), operation => operation.Name == "UX_DiscoveryGeneration_HostedSlot");
        Assert.True(slot.IsUnique);
        Assert.Equal(new[] { "ExecutionSlot" }, slot.Columns);
        Assert.Equal("\"ExecutionSlot\" IS NOT NULL", slot.Filter);
        var claim = Assert.Single(migration.UpOperations.OfType<CreateIndexOperation>(), operation => operation.Name == "UX_DiscoveryGeneration_ActiveClaim");
        Assert.True(claim.IsUnique);
        Assert.Contains("'Accepted'", claim.Filter, StringComparison.Ordinal);
        Assert.IsType<SqlOperation>(migration.DownOperations[0]); // Quiescence must be checked before dropping ownership.
    }
}
