using FlowPlane.Infrastructure.Persistence;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace FlowPlane.Api.Tests;

public sealed class PostgreSqlBaselineTests
{
    [Fact]
    public async Task Isolated_database_readiness_fails_and_recovers_without_stopping_shared_PostgreSQL()
    {
        RequireGateB();
        var source = Environment.GetEnvironmentVariable("FLOWPLANE_TEST_POSTGRES_CONNECTION_STRING");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(source), "Configure isolated PostgreSQL 18.");
        var token = TestContext.Current.CancellationToken;
        var name = $"flowplane_a05_{Guid.NewGuid():N}";
        var quoted = new NpgsqlCommandBuilder().QuoteIdentifier(name);
        await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(source) { Database = "postgres", Pooling = false }.ConnectionString);
        await admin.OpenAsync(token);
        await using var command = admin.CreateCommand();
        command.CommandText = $"CREATE DATABASE {quoted}";
        await command.ExecuteNonQueryAsync(token);
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(source) { Database = name, Pooling = false }.ConnectionString;
            await using var context = new FlowPlaneDbContext(new DbContextOptionsBuilder<FlowPlaneDbContext>().UseNpgsql(connection).Options);
            await context.Database.MigrateAsync(token);
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["ConnectionStrings:FlowPlane"] = connection })));
            using var client = factory.CreateClient();
            await Health("/health/live", HttpStatusCode.OK, "Healthy");
            await Health("/health/ready", HttpStatusCode.OK, "Healthy");
            command.CommandText = $"ALTER DATABASE {quoted} ALLOW_CONNECTIONS false";
            await command.ExecuteNonQueryAsync(token);
            await Health("/health/live", HttpStatusCode.OK, "Healthy");
            await Health("/health/ready", HttpStatusCode.ServiceUnavailable, "Unhealthy");
            command.CommandText = $"ALTER DATABASE {quoted} ALLOW_CONNECTIONS true";
            await command.ExecuteNonQueryAsync(token);
            await Health("/health/ready", HttpStatusCode.OK, "Healthy");
            Assert.Equal(4, (await context.Database.GetAppliedMigrationsAsync(token)).Count());

            async Task Health(string path, HttpStatusCode status, string body)
            {
                using var response = await client.GetAsync(path, token);
                Assert.Equal(status, response.StatusCode);
                Assert.Equal(body, await response.Content.ReadAsStringAsync(token));
            }
        }
        finally
        {
            command.CommandText = $"DROP DATABASE {quoted} WITH (FORCE)";
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task BaselineMigrationCanBeAppliedRepeatedlyToFreshDatabase()
    {
        RequireGateB();
        var baseConnectionString = Environment.GetEnvironmentVariable(
            "FLOWPLANE_TEST_POSTGRES_CONNECTION_STRING");
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(baseConnectionString),
            "Set FLOWPLANE_TEST_POSTGRES_CONNECTION_STRING to run the PostgreSQL integration test.");

        var cancellationToken = TestContext.Current.CancellationToken;
        var databaseName = $"flowplane_a05_{Guid.NewGuid():N}";
        var quotedDatabaseName = new NpgsqlCommandBuilder().QuoteIdentifier(databaseName);
        var adminConnectionString = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Database = "postgres",
            Pooling = false
        }.ConnectionString;
        var databaseConnectionString = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Database = databaseName,
            Pooling = false
        }.ConnectionString;

        await using var adminConnection = new NpgsqlConnection(adminConnectionString);
        await adminConnection.OpenAsync(cancellationToken);

        await using (var createDatabase = adminConnection.CreateCommand())
        {
            createDatabase.CommandText = $"CREATE DATABASE {quotedDatabaseName}";
            await createDatabase.ExecuteNonQueryAsync(cancellationToken);
        }

        try
        {
            var options = new DbContextOptionsBuilder<FlowPlaneDbContext>()
                .UseNpgsql(databaseConnectionString)
                .Options;

            await using (var context = new FlowPlaneDbContext(options))
            {
                await context.Database.MigrateAsync(cancellationToken);
                await context.Database.MigrateAsync(cancellationToken);
            }

            await using var databaseConnection = new NpgsqlConnection(databaseConnectionString);
            await databaseConnection.OpenAsync(cancellationToken);

            await using var countTables = databaseConnection.CreateCommand();
            countTables.CommandText =
                """
                SELECT count(*)
                FROM information_schema.tables
                WHERE table_schema = 'public';
                """;

            var tableCount = (long)(await countTables.ExecuteScalarAsync(cancellationToken))!;
            Assert.Equal(7, tableCount);

            await using var countMigrations = databaseConnection.CreateCommand();
            countMigrations.CommandText = "SELECT count(*) FROM \"__EFMigrationsHistory\";";

            var migrationCount =
                (long)(await countMigrations.ExecuteScalarAsync(cancellationToken))!;
            Assert.Equal(4, migrationCount);
        }
        finally
        {
            await using var dropDatabase = adminConnection.CreateCommand();
            dropDatabase.CommandText = $"DROP DATABASE {quotedDatabaseName} WITH (FORCE)";
            await dropDatabase.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static void RequireGateB() => Assert.SkipUnless(
        Environment.GetEnvironmentVariable("FLOWPLANE_TEST_DISCOVERY_HOSTING_MIGRATION_APPROVED") == "true",
        "Explicit Gate B migration application approval is required.");
}
