using FlowPlane.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace FlowPlane.ProviderConfiguration.Tests;

public sealed class AgentControlMigrationTests
{
    [Fact]
    public void F3_is_exactly_five_additive_tables_with_a_locked_fail_closed_rollback_guard()
    {
        using var db = new FlowPlaneDbContext(new DbContextOptionsBuilder<FlowPlaneDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=design_only;Username=design_only").Options);
        var assembly = db.GetService<IMigrationsAssembly>();
        var entry = Assert.Single(assembly.Migrations, m => m.Key.EndsWith("_AgentControlFoundation", StringComparison.Ordinal));
        var migration = assembly.CreateMigration(entry.Value, db.Database.ProviderName!);
        var expected = new[] { "AgentCredentials", "AgentEnrollmentGrants", "AgentProviderAssignments", "AgentSecurityEvents", "Agents" };
        Assert.Equal(expected, migration.UpOperations.OfType<CreateTableOperation>().Select(t => t.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.All(migration.UpOperations, op => Assert.True(op is CreateTableOperation or CreateIndexOperation));
        Assert.All(migration.UpOperations.OfType<CreateTableOperation>().SelectMany(t => t.ForeignKeys), fk =>
            Assert.Equal(ReferentialAction.Restrict, fk.OnDelete));
        var guard = Assert.IsType<SqlOperation>(migration.DownOperations[0]).Sql;
        Assert.Contains("ACCESS EXCLUSIVE", guard, StringComparison.Ordinal);
        Assert.Contains("\"RecoveryRequired\"", guard, StringComparison.Ordinal);
        Assert.Contains("\"State\" <> 'Revoked'", guard, StringComparison.Ordinal);
        Assert.Contains("'Issued','Claimed','Proven'", guard, StringComparison.Ordinal);
        Assert.Contains("'PendingProof','Active','RetiringReceiptsOnly'", guard, StringComparison.Ordinal);
        Assert.Contains("\"RecordedAtUtc\" >= clock_timestamp() - interval '90 days'", guard, StringComparison.Ordinal);
        Assert.Equal(expected, migration.DownOperations.OfType<DropTableOperation>().Select(t => t.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.False(db.Database.HasPendingModelChanges());
        var script = db.GetService<IMigrator>().GenerateScript("20261026084710_DurableDiscoveryHosting", entry.Key);
        Assert.Contains("CREATE TABLE \"Agents\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER TABLE \"FlowPlaneSecrets\"", script, StringComparison.Ordinal);
    }
}
