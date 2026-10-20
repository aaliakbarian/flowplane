using FlowPlane.Application.Providers.DiscoveryInventory;
using FlowPlane.Infrastructure.Persistence;
using FlowPlane.Application.Providers;
using FlowPlane.Application.Secrets;
using FlowPlane.Domain.Providers;
using FlowPlane.Providers.Ssis.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FlowPlane.Persistence.Tests;

public sealed class DiscoveryInventoryMigrationTests(ITestOutputHelper output)
{
    private const string A09 = "20261013120748_ProviderConfigurationAndSecretStorage";

    [Fact]
    public async Task Generated_upgrade_SQL_and_EF_application_produce_identical_PostgreSQL_catalog_definitions()
    {
        ProviderDiscoveryInventoryTests.RequireMigrationApproval();
        var token = TestContext.Current.CancellationToken;
        await using var efDatabase = await PostgreSqlTestDatabase.CreateAsync(token);
        Assert.SkipWhen(efDatabase is null, "Configure isolated PostgreSQL 18.");
        await using var sqlDatabase = await PostgreSqlTestDatabase.CreateAsync(token);
        await using var efServices = efDatabase!.CreateServices(null);
        await using var sqlServices = sqlDatabase!.CreateServices(null);
        await efDatabase.MigrateAsync(efServices, token);
        await using var efScope = efServices.CreateAsyncScope();
        await using var sqlScope = sqlServices.CreateAsyncScope();
        var ef = efScope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        var sql = sqlScope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        var migrator = sql.GetService<IMigrator>();
        await migrator.MigrateAsync(A09, token);
        var script = migrator.GenerateScript(A09, "20261020082907_CurrentDiscoveryInventory");
        await sql.Database.ExecuteSqlRawAsync(script, token);
        const string catalog = """
            SELECT description AS "Value" FROM (
              SELECT c.relname || '.' || a.attname || ':' || format_type(a.atttypid, a.atttypmod) || ':' || a.attnotnull::text || ':' || coalesce(coll.collname, '') AS description
              FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace JOIN pg_attribute a ON a.attrelid = c.oid
              LEFT JOIN pg_collation coll ON coll.oid = a.attcollation
              WHERE n.nspname = 'public' AND c.relname LIKE 'ProviderDiscovery%' AND c.relkind = 'r' AND a.attnum > 0 AND NOT a.attisdropped
              UNION ALL
              SELECT conname || ':' || pg_get_constraintdef(oid) FROM pg_constraint
              WHERE conrelid IN ('"ProviderDiscoveryStates"'::regclass, '"ProviderDiscoveryGenerations"'::regclass, '"ProviderDiscoveryObservations"'::regclass)
              UNION ALL
              SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename LIKE 'ProviderDiscovery%'
            ) catalog ORDER BY description COLLATE "C"
            """;
        var expected = await ef.Database.SqlQueryRaw<string>(catalog).ToListAsync(token);
        var actual = await sql.Database.SqlQueryRaw<string>(catalog).ToListAsync(token);
        Assert.Equal(expected, actual);
        Assert.Equal(3, (await sql.Database.GetAppliedMigrationsAsync(token)).Count());
        foreach (var line in actual) output.WriteLine(line); // Schema only: never rows or connection configuration.
    }

    [Fact]
    public async Task Fresh_upgrade_repeat_and_rollback_preserve_existing_provider_and_encrypted_secret()
    {
        ProviderDiscoveryInventoryTests.RequireMigrationApproval();
        var token = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        Assert.SkipWhen(database is null, "Configure isolated PostgreSQL 18.");
        var directory = Path.Combine(Path.GetTempPath(), $"flowplane-upgrade-{Guid.NewGuid():N}");
        var certificate = PostgreSqlTestDatabase.CreateRootCertificate(directory, "upgrade-root");
        try
        {
            await using var services = database!.CreateServices(certificate);
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(A09, token);
            var canary = $"upgrade-{Guid.NewGuid():N}";
            var instance = await scope.ServiceProvider.GetRequiredService<IProviderInstanceStore>().CreateWithManagedSecretAsync(
                Guid.NewGuid(), SsisProviderConfigurationCodec.SsisProviderType, "Upgrade fixture", true,
                new SsisProviderConfigurationCodec().Encode(new("fixture", 1433, "fixture-user", false)),
                canary.AsMemory(), DateTimeOffset.UtcNow, token);
            var ciphertext = (await db.SecretRecords.AsNoTracking().SingleAsync(token)).Ciphertext;
            await migrator.MigrateAsync(cancellationToken: token);
            await migrator.MigrateAsync(cancellationToken: token);
            Assert.Equal(3, (await db.Database.GetAppliedMigrationsAsync(token)).Count());
            var tables = await db.Database.SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public' ORDER BY table_name").ToListAsync(token);
            Assert.Equal(new[] { "DataProtectionKeys", "FlowPlaneSecrets", "ProviderDiscoveryGenerations", "ProviderDiscoveryObservations", "ProviderDiscoveryStates", "ProviderInstances", "__EFMigrationsHistory" }, tables);
            Assert.Equal(ciphertext, (await db.SecretRecords.AsNoTracking().SingleAsync(token)).Ciphertext);
            Assert.Equal(instance.SecretReference, (await db.ProviderInstances.AsNoTracking().SingleAsync(token)).SecretReference);
            Assert.True(await scope.ServiceProvider.GetRequiredService<ISecretResolver>().UseAsync(instance.SecretReference,
                (value, _) => ValueTask.FromResult(value.Span.SequenceEqual(canary.AsSpan())), token));
            await migrator.MigrateAsync(A09, token);
            Assert.Equal(2, (await db.Database.GetAppliedMigrationsAsync(token)).Count());
            Assert.Equal(0, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public' AND table_name LIKE 'ProviderDiscovery%'").SingleAsync(token));
            Assert.Equal(ciphertext, (await db.SecretRecords.AsNoTracking().SingleAsync(token)).Ciphertext);
            Assert.Equal(instance.Id, (await db.ProviderInstances.AsNoTracking().SingleAsync(token)).Id);
            await migrator.MigrateAsync(cancellationToken: token);
            Assert.Equal(3, (await db.Database.GetAppliedMigrationsAsync(token)).Count());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task PostgreSQL_enforces_composite_ownership_unique_claims_and_payload_checks()
    {
        ProviderDiscoveryInventoryTests.RequireMigrationApproval();
        var token = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        Assert.SkipWhen(database is null, "Configure isolated PostgreSQL 18.");
        await using var services = database!.CreateServices(null);
        await database.MigrateAsync(services, token);
        var first = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, token);
        var second = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, token);
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderDiscoveryInventoryStore>();
        await using var a = await store.BeginRefreshAsync(first, token);
        await a.Sink.AppendAsync(ProviderDiscoveryInventoryTests.Observation(first, "native"), token);
        await a.PublishAsync(ProviderDiscoveryInventoryTests.Summary(1), token);
        await using var b = await store.BeginRefreshAsync(second, token);
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        await Rejected($"UPDATE \"ProviderDiscoveryStates\" SET \"CurrentGenerationId\" = {a.RefreshId} WHERE \"ProviderInstanceId\" = {second}", PostgresErrorCodes.ForeignKeyViolation);
        await Rejected($"UPDATE \"ProviderDiscoveryObservations\" SET \"ProviderInstanceId\" = {second} WHERE \"RefreshId\" = {a.RefreshId}", PostgresErrorCodes.ForeignKeyViolation);
        await Rejected($"UPDATE \"ProviderDiscoveryObservations\" SET \"MetadataJson\" = NULL WHERE \"RefreshId\" = {a.RefreshId}", PostgresErrorCodes.CheckViolation);
        await Rejected($"UPDATE \"ProviderDiscoveryObservations\" SET \"Visibility\" = 'NotObserved', \"NotObservedAtUtc\" = clock_timestamp() WHERE \"RefreshId\" = {a.RefreshId}", PostgresErrorCodes.CheckViolation);
        await Rejected($"UPDATE \"ProviderDiscoveryObservations\" SET \"CandidateSchemaId\" = 'orphaned-schema' WHERE \"RefreshId\" = {a.RefreshId}", PostgresErrorCodes.CheckViolation);
        await Rejected($"UPDATE \"ProviderDiscoveryObservations\" SET \"NativeKeyDigest\" = decode('ff', 'hex') WHERE \"RefreshId\" = {a.RefreshId}", PostgresErrorCodes.CheckViolation);
        await Rejected($"UPDATE \"ProviderDiscoveryGenerations\" SET \"ObservationCount\" = -1 WHERE \"RefreshId\" = {a.RefreshId}", PostgresErrorCodes.CheckViolation);
        // An independent SQL client cannot bypass the coordinator's one-claim policy.
        await using var other = await store.BeginRefreshAsync(first, token);
        await Rejected($"UPDATE \"ProviderDiscoveryGenerations\" SET \"Status\" = 'Staging' WHERE \"RefreshId\" = {a.RefreshId}", PostgresErrorCodes.UniqueViolation);
        await Rejected($"DELETE FROM \"ProviderInstances\" WHERE \"Id\" = {first}", PostgresErrorCodes.RestrictViolation);

        var indexes = await db.Database.SqlQueryRaw<string>("SELECT indexdef AS \"Value\" FROM pg_indexes WHERE schemaname = 'public' AND tablename LIKE 'ProviderDiscovery%'").ToListAsync(token);
        Assert.Equal(12, indexes.Count);
        Assert.Contains(indexes, value => value.Contains("UNIQUE INDEX \"UX_DiscoveryGeneration_ActiveClaim\"", StringComparison.Ordinal) && value.Contains("Staging", StringComparison.Ordinal) && value.Contains("Preparing", StringComparison.Ordinal) && value.Contains("Ready", StringComparison.Ordinal));
        Assert.Contains(indexes, value => value.Contains("CandidateSchemaId", StringComparison.Ordinal) && value.Contains("WHERE", StringComparison.Ordinal) && value.Contains("Observed", StringComparison.Ordinal));
        var foreignKeys = await db.Database.SqlQueryRaw<string>("SELECT pg_get_constraintdef(oid) AS \"Value\" FROM pg_constraint WHERE contype = 'f' AND conrelid IN ('\"ProviderDiscoveryStates\"'::regclass, '\"ProviderDiscoveryGenerations\"'::regclass, '\"ProviderDiscoveryObservations\"'::regclass)").ToListAsync(token);
        Assert.Equal(6, foreignKeys.Count);
        Assert.Equal(4, foreignKeys.Count(value => value.StartsWith("FOREIGN KEY (\"ProviderInstanceId\",", StringComparison.Ordinal)));
        Assert.Equal(5, foreignKeys.Count(value => value.EndsWith("ON DELETE RESTRICT", StringComparison.Ordinal)));

        async Task Rejected(FormattableString sql, string code)
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(sql, token));
            Assert.Equal(code, exception.SqlState);
        }
    }

    [Fact]
    public async Task PostgreSQL_rejects_published_generation_without_scan_finish_timestamp()
    {
        ProviderDiscoveryInventoryTests.RequireMigrationApproval();
        var token = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        Assert.SkipWhen(database is null, "Configure isolated PostgreSQL 18.");
        await using var services = database!.CreateServices(null);
        await database.MigrateAsync(services, token);
        var id = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, token);
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderDiscoveryInventoryStore>();
        await using var refresh = await store.BeginRefreshAsync(id, token);
        await refresh.PublishAsync(ProviderDiscoveryInventoryTests.Summary(0), token);
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"ProviderDiscoveryGenerations\" SET \"ScanFinishedAtUtc\" = NULL WHERE \"RefreshId\" = {refresh.RefreshId}", token));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("CK_DiscoveryGeneration_Publication", exception.ConstraintName);
    }
}
