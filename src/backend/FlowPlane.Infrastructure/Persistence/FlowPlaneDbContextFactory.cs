using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FlowPlane.Infrastructure.Persistence;

public sealed class FlowPlaneDbContextFactory : IDesignTimeDbContextFactory<FlowPlaneDbContext>
{
    public FlowPlaneDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<FlowPlaneDbContext>()
            .UseNpgsql(PostgreSqlConnectionString.ResolveFromEnvironment())
            .Options;

        return new FlowPlaneDbContext(options);
    }
}
