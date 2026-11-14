using FlowPlane.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace FlowPlane.ProviderConfiguration.Tests;

public sealed class AgentControlModelTests
{
    [Fact]
    public void Five_agent_tables_have_composite_ownership_and_bounded_public_material_without_database_access()
    {
        using var db = new FlowPlaneDbContext(new DbContextOptionsBuilder<FlowPlaneDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=design_only;Username=design_only").Options);
        var tables = db.Model.GetEntityTypes().Select(e => e.GetTableName()).ToHashSet();
        Assert.Contains("Agents", tables);
        Assert.Contains("AgentCredentials", tables);
        Assert.Contains("AgentProviderAssignments", tables);
        Assert.Contains("AgentEnrollmentGrants", tables);
        Assert.Contains("AgentSecurityEvents", tables);
        var sql = db.Database.GenerateCreateScript();
        Assert.Contains("CHECK (octet_length(\"Der\") BETWEEN 1 AND 16384)", sql, StringComparison.Ordinal);
        Assert.Contains("FOREIGN KEY (\"AgentId\", \"ClaimedCredentialId\")", sql, StringComparison.Ordinal);
        Assert.Contains("FOREIGN KEY (\"ProviderInstanceId\", \"AgentId\")", sql, StringComparison.Ordinal);
        Assert.Contains("UNIQUE INDEX \"UX_AgentCredential_Active\"", sql, StringComparison.Ordinal);
        Assert.Contains("UNIQUE INDEX \"UX_AgentGrant_LiveAgent\"", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ON DELETE CASCADE", string.Join('\n', sql.Split('\n').Where(l => l.Contains("Agent", StringComparison.Ordinal))), StringComparison.Ordinal);
    }
}
