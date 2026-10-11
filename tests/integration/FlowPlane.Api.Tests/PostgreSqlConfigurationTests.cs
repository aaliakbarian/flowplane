using FlowPlane.Infrastructure;
using FlowPlane.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace FlowPlane.Api.Tests;

public sealed class PostgreSqlConfigurationTests
{
    [Fact]
    public void ComponentConfigurationPreservesDelimiterContainingCredentials()
    {
        const string password = "local;quote\"secret";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FlowPlane:PostgreSql:Host"] = "postgres",
                ["FlowPlane:PostgreSql:Port"] = "5432",
                ["FlowPlane:PostgreSql:Database"] = "flowplane",
                ["FlowPlane:PostgreSql:Username"] = "flowplane",
                ["FlowPlane:PostgreSql:Password"] = password
            })
            .Build();
        var services = new ServiceCollection();

        services.AddFlowPlanePostgreSql(configuration);

        using var provider = services.BuildServiceProvider();
        using var context = provider.GetRequiredService<FlowPlaneDbContext>();
        var connectionString = context.Database.GetConnectionString();
        var parsedConnectionString = new NpgsqlConnectionStringBuilder(connectionString);

        Assert.Equal(password, parsedConnectionString.Password);
    }
}
