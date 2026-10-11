using FlowPlane.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FlowPlane.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddFlowPlanePostgreSql(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddDbContext<FlowPlaneDbContext>(options =>
        {
            options.UseNpgsql(PostgreSqlConnectionString.Resolve(configuration));
        });

        services
            .AddHealthChecks()
            .AddDbContextCheck<FlowPlaneDbContext>(
                "postgresql",
                failureStatus: HealthStatus.Unhealthy,
                tags: ["ready"]);

        return services;
    }
}
