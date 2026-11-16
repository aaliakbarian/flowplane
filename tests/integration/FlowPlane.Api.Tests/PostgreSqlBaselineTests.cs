using System.Net;

using FlowPlane.Infrastructure.Persistence;
using FlowPlane.Persistence.Tests;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

using Npgsql;

namespace FlowPlane.Api.Tests;

public sealed class PostgreSqlBaselineTests
{
    [Fact]
    public async Task Isolated_database_readiness_fails_and_recovers_without_stopping_shared_PostgreSQL()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await CreateAsync(token);
        await using var services = database.CreateServices(null);
        await database.MigrateAsync(services, token);
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:FlowPlane"] = database.ConnectionString })));
        using var client = factory.CreateClient();
        var quoted = new NpgsqlCommandBuilder().QuoteIdentifier(database.DatabaseName);
        var adminString = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = "postgres" }.ConnectionString;
        await using var admin = new NpgsqlConnection(adminString);
        await admin.OpenAsync(token);
        await Health("/health/live", HttpStatusCode.OK, "Healthy");
        await Health("/health/ready", HttpStatusCode.OK, "Healthy");
        try
        {
            await using var disable = new NpgsqlCommand($"ALTER DATABASE {quoted} ALLOW_CONNECTIONS false", admin);
            await disable.ExecuteNonQueryAsync(token);
            await Health("/health/live", HttpStatusCode.OK, "Healthy");
            await Health("/health/ready", HttpStatusCode.ServiceUnavailable, "Unhealthy");
        }
        finally
        {
            // Restore only this invocation's newly created database, even when a health assertion is cancelled.
            await using var enable = new NpgsqlCommand($"ALTER DATABASE {quoted} ALLOW_CONNECTIONS true", admin);
            await enable.ExecuteNonQueryAsync(CancellationToken.None);
        }
        await Health("/health/ready", HttpStatusCode.OK, "Healthy");
        await using var context = new FlowPlaneDbContext(new DbContextOptionsBuilder<FlowPlaneDbContext>()
            .UseNpgsql(database.ConnectionString).Options);
        Assert.Equal(5, (await context.Database.GetAppliedMigrationsAsync(token)).Count());

        async Task Health(string path, HttpStatusCode status, string body)
        {
            using var response = await client.GetAsync(path, token);
            Assert.Equal(status, response.StatusCode);
            Assert.Equal(body, await response.Content.ReadAsStringAsync(token));
        }
    }

    [Fact]
    public async Task BaselineMigrationCanBeAppliedRepeatedlyToFreshDatabase()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await CreateAsync(token);
        await using var services = database.CreateServices(null);
        await database.MigrateAsync(services, token);
        await database.MigrateAsync(services, token);
        await using var context = new FlowPlaneDbContext(new DbContextOptionsBuilder<FlowPlaneDbContext>()
            .UseNpgsql(database.ConnectionString).Options);
        Assert.Equal(12, await context.Database.SqlQueryRaw<long>(
            "SELECT count(*) AS \"Value\" FROM information_schema.tables WHERE table_schema='public'").SingleAsync(token));
        Assert.Equal(5, (await context.Database.GetAppliedMigrationsAsync(token)).Count());
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(token));
    }

    private static async Task<PostgreSqlTestDatabase> CreateAsync(CancellationToken token)
    {
        var database = await PostgreSqlTestDatabase.CreateAsync(token);
        Assert.SkipWhen(database is null, "Configure separately approved isolated PostgreSQL 18.");
        return database!;
    }
}
