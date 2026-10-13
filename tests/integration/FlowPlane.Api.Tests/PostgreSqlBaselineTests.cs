using FlowPlane.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace FlowPlane.Api.Tests;

public sealed class PostgreSqlBaselineTests
{
    [Fact]
    public async Task BaselineMigrationCanBeAppliedRepeatedlyToFreshDatabase()
    {
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
            Assert.Equal(4, tableCount);

            await using var countMigrations = databaseConnection.CreateCommand();
            countMigrations.CommandText = "SELECT count(*) FROM \"__EFMigrationsHistory\";";

            var migrationCount =
                (long)(await countMigrations.ExecuteScalarAsync(cancellationToken))!;
            Assert.Equal(2, migrationCount);
        }
        finally
        {
            await using var dropDatabase = adminConnection.CreateCommand();
            dropDatabase.CommandText = $"DROP DATABASE {quotedDatabaseName} WITH (FORCE)";
            await dropDatabase.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
