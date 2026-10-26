using FlowPlane.Infrastructure.Persistence;
using FlowPlane.Application.Providers.DiscoveryHosting;
using FlowPlane.Application.Providers.DiscoveryInventory;
using System.Data.Common;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace FlowPlane.Persistence.Tests;

public sealed class DiscoveryHostingMigrationPostgreSqlTests(ITestOutputHelper output)
{
    private const string A11b = "20261020082907_CurrentDiscoveryInventory";
    private const string GateB = "20261026084710_DurableDiscoveryHosting";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Rollback_serializes_its_guard_with_concurrent_admission_before_dropping_executor_metadata()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync(Token);
        Assert.SkipWhen(database is null, "Configure isolated PostgreSQL 18.");
        var boundary = new RollbackGuardBoundary();
        await using var services = database!.CreateServices(null, registrations =>
        {
            registrations.AddSingleton(new DiscoveryHostingOptions { Enabled = true });
            registrations.ConfigureDbContext<FlowPlaneDbContext>(options => options.AddInterceptors(boundary));
        });
        await database.MigrateAsync(services, Token);
        var id = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, Token);
        await using var rollbackScope = services.CreateAsyncScope();
        var db = rollbackScope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        var rollback = db.GetService<IMigrator>().MigrateAsync(A11b, Token);
        Task<HostedDiscoveryWork?>? admission = null;
        try
        {
            await boundary.Checked.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            admission = AdmitAndAcquire();
            // Wait until PostgreSQL shows the new request blocked by the guard's table lock,
            // or it wrongfully creates a live executor while the rollback guard has finished.
            await using var outside = services.CreateAsyncScope();
            var observer = outside.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
            var limit = System.Diagnostics.Stopwatch.StartNew();
            var blocked = false;
            while (!admission.IsCompleted && limit.Elapsed < TimeSpan.FromSeconds(5))
            {
                blocked = await observer.Database.SqlQueryRaw<bool>("""
                    SELECT EXISTS (SELECT 1 FROM pg_locks WHERE relation='"ProviderDiscoveryGenerations"'::regclass
                      AND NOT granted AND mode='AccessShareLock') AS "Value"
                    """).SingleAsync(Token);
                if (blocked) break;
                await Task.Delay(20, Token);
            }
            Assert.False(admission.IsCompleted, "An executor was admitted after the rollback guard but before ownership columns were dropped.");
            Assert.True(blocked, "The rollback guard must hold its table lock through the ownership DDL transaction.");
        }
        finally
        {
            boundary.Release.TrySetResult();
            // Old unsafe code may have admitted Accepted work; observe any rollback failure without hiding the assertion.
            try { await rollback; } catch (PostgresException) { }
            if (admission is not null) try { await admission; } catch (PostgresException) { }
        }
        Assert.Equal(3, (await db.Database.GetAppliedMigrationsAsync(Token)).Count());
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM \"ProviderDiscoveryGenerations\"").SingleAsync(Token));

        async Task<HostedDiscoveryWork?> AdmitAndAcquire()
        {
            await using var scope = services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IHostedDiscoveryStore>();
            await store.AdmitAsync(new(Guid.CreateVersion7(), id, 1, new("https://fixture.test", "operator")), Token);
            return await store.TryAcquireAsync(Guid.NewGuid(), Token);
        }
    }

    private sealed class RollbackGuardBoundary : DbCommandInterceptor
    {
        public TaskCompletionSource Checked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("DO $rollback$", StringComparison.Ordinal))
            {
                Checked.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A11b_published_and_terminal_data_survive_SQL_and_EF_upgrade_repeat_and_quiescent_rollback(bool generatedSql)
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync(Token);
        Assert.SkipWhen(database is null, "Configure isolated PostgreSQL 18.");
        await using var services = database!.CreateServices(null);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(A11b, Token);
        var id = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, Token);
        var published = Guid.CreateVersion7();
        var terminal = Guid.CreateVersion7();
        foreach (var refresh in new[] { published, terminal })
        {
            var status = refresh == published ? "Published" : "Failed";
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "ProviderDiscoveryGenerations"
                 ("RefreshId", "ProviderInstanceId", "FenceToken", "ExpectedPublicationVersion", "ProviderVersion",
                  "ProviderType", "CredentialKind", "AccessContextId", "StartedAtUtc", "ExpiresAtUtc", "Status", "Version",
                  "ScanCompleted", "ScanOutcome", "SourceCompatibility", "ScanFinishedAtUtc", "FinishedAtUtc", "PublishedAtUtc",
                  "ObservationCount", "CandidateCount", "IncompatibleCount", "AddedCount", "ChangedCount", "UnchangedCount", "NotObservedCount")
                VALUES ({refresh}, {id}, 1, 0, 1, 'ssis', 'Environment', {Guid.NewGuid()}, clock_timestamp(),
                  clock_timestamp() + interval '1 hour', {status}, 1, true, 'Succeeded', 'Compatible', clock_timestamp(),
                  clock_timestamp(), CASE WHEN {status} = 'Published' THEN clock_timestamp() ELSE NULL END,
                  1, 0, 0, 1, 0, 0, 0);
                INSERT INTO "ProviderDiscoveryObservations"
                 ("ProviderInstanceId", "RefreshId", "NativeKeyDigest", "NativeKey", "SchemaId", "Visibility", "MetadataJson",
                  "ContentFingerprint", "LastObservedAtUtc", "ObservedAccessContextId", "ObservedProviderVersion")
                VALUES ({id}, {refresh}, sha256(convert_to('exact-native-项目', 'UTF8')), 'exact-native-项目',
                  'fixture/observation.v1', 'Observed', CAST({"{\"revision\":1}"} AS jsonb), sha256(convert_to('fingerprint', 'UTF8')),
                  clock_timestamp(), {Guid.NewGuid()}, 1);
                """, Token);
        }
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ProviderDiscoveryStates" ("ProviderInstanceId", "CurrentGenerationId", "LastAttemptGenerationId",
              "PublicationVersion", "FenceToken", "Lifecycle") VALUES ({id}, {published}, {terminal}, 1, 1, 'Active')
            """, Token);
        var before = await ExistingRows();
        if (generatedSql) await db.Database.ExecuteSqlRawAsync(migrator.GenerateScript(A11b, GateB), Token);
        else await migrator.MigrateAsync(GateB, Token);
        Assert.Equal(before, await ExistingRows());
        Assert.All(await db.DiscoveryGenerations.AsNoTracking().ToListAsync(Token), row =>
        {
            Assert.Null(row.ExecutionSlot); Assert.Null(row.ExecutorId); Assert.Null(row.RequestingIssuer);
            Assert.Null(row.RequestingSubject); Assert.Null(row.LeaseExpiresAtUtc); Assert.Null(row.ReceiptExpiresAtUtc);
            Assert.Null(row.PublicationAttemptedAtUtc); Assert.False(row.PublicationUncertain);
        });
        await migrator.MigrateAsync(GateB, Token);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Token));
        Assert.Equal(4, (await db.Database.GetAppliedMigrationsAsync(Token)).Count());
        Assert.Equal(7, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM pg_tables WHERE schemaname='public'").SingleAsync(Token));
        var columns = await db.Database.SqlQueryRaw<string>("""
            SELECT column_name || ':' || data_type || ':' || is_nullable || ':' || coalesce(column_default,'<none>') AS "Value"
            FROM information_schema.columns WHERE table_schema='public' AND table_name='ProviderDiscoveryGenerations'
              AND column_name IN ('ExecutionSlot','ExecutorId','LeaseExpiresAtUtc','PublicationAttemptedAtUtc',
                'PublicationUncertain','ReceiptExpiresAtUtc','RequestingIssuer','RequestingSubject') ORDER BY column_name COLLATE "C"
            """).ToListAsync(Token);
        Assert.Equal(new[] { "ExecutionSlot:integer:YES:<none>", "ExecutorId:uuid:YES:<none>",
            "LeaseExpiresAtUtc:timestamp with time zone:YES:<none>", "PublicationAttemptedAtUtc:timestamp with time zone:YES:<none>",
            "PublicationUncertain:boolean:NO:false", "ReceiptExpiresAtUtc:timestamp with time zone:YES:<none>",
            "RequestingIssuer:character varying:YES:<none>", "RequestingSubject:character varying:YES:<none>" }, columns);
        foreach (var column in columns) output.WriteLine(column);
        if (generatedSql) await db.Database.ExecuteSqlRawAsync(migrator.GenerateScript(GateB, A11b), Token);
        else await migrator.MigrateAsync(A11b, Token);
        Assert.Equal(3, (await db.Database.GetAppliedMigrationsAsync(Token)).Count());
        Assert.Equal(before, await ExistingRows());
        Assert.Equal(7, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM pg_tables WHERE schemaname='public'").SingleAsync(Token));

        async Task<List<string>> ExistingRows() => await db.Database.SqlQueryRaw<string>("""
            SELECT value AS "Value" FROM (
              SELECT 'provider:' || row_to_json(t)::text AS value FROM "ProviderInstances" t
              UNION ALL SELECT 'state:' || row_to_json(t)::text FROM "ProviderDiscoveryStates" t
              UNION ALL SELECT 'observation:' || row_to_json(t)::text FROM "ProviderDiscoveryObservations" t
              UNION ALL SELECT 'generation:' || "RefreshId"::text || ':' || "Status" || ':' || "ProviderInstanceId"::text
                || ':' || "ObservationCount"::text FROM "ProviderDiscoveryGenerations"
            ) rows ORDER BY value COLLATE "C"
            """).ToListAsync(Token);
    }

    [Theory]
    [InlineData("Accepted")]
    [InlineData("Staging")]
    [InlineData("Ready")]
    [InlineData("UncertainWithoutSlot")]
    public async Task Full_rollback_preserves_receipt_and_fence_when_hosted_work_is_not_quiescent(string phase)
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync(Token);
        Assert.SkipWhen(database is null, "Configure isolated PostgreSQL 18.");
        await using var services = database!.CreateServices(null, registrations => registrations.AddSingleton(new DiscoveryHostingOptions { Enabled = true }));
        await database.MigrateAsync(services, Token);
        var id = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, Token);
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IHostedDiscoveryStore>();
        var receipt = await store.AdmitAsync(new(Guid.CreateVersion7(), id, 1, new("https://fixture.test", "operator")), Token);
        if (phase != "Accepted")
        {
            var work = (await store.TryAcquireAsync(Guid.NewGuid(), Token))!;
            // Do not dispose the session: this models a stopped executor without graceful cleanup.
            Assert.Equal(receipt.RefreshId, work.Session.RefreshId);
        }
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        if (phase == "Ready")
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"ProviderDiscoveryGenerations\" SET \"Status\"='Ready' WHERE \"RefreshId\"={receipt.RefreshId}", Token);
        if (phase == "UncertainWithoutSlot")
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "ProviderDiscoveryGenerations" SET "Status"='Failed', "ExecutionSlot"=NULL,
                  "PublicationAttemptedAtUtc"=clock_timestamp(), "PublicationUncertain"=true WHERE "RefreshId"={receipt.RefreshId}
                """, Token);
        var before = await db.Database.SqlQueryRaw<string>("SELECT row_to_json(t)::text AS \"Value\" FROM \"ProviderDiscoveryGenerations\" t").SingleAsync(Token);
        var fence = (await db.DiscoveryStates.AsNoTracking().SingleAsync(Token)).FenceToken;
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(A11b, Token));
        Assert.Equal(PostgresErrorCodes.RaiseException, error.SqlState);
        Assert.Equal(4, (await db.Database.GetAppliedMigrationsAsync(Token)).Count());
        Assert.Equal(before, await db.Database.SqlQueryRaw<string>("SELECT row_to_json(t)::text AS \"Value\" FROM \"ProviderDiscoveryGenerations\" t").SingleAsync(Token));
        Assert.Equal(fence, (await db.DiscoveryStates.AsNoTracking().SingleAsync(Token)).FenceToken);
    }

    [Fact]
    public async Task PostgreSQL_rejects_invalid_hosting_ownership_and_enforces_global_slot_without_application_locks()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync(Token);
        Assert.SkipWhen(database is null, "Configure isolated PostgreSQL 18.");
        await using var services = database!.CreateServices(null, registrations => registrations.AddSingleton(new DiscoveryHostingOptions { Enabled = true }));
        await database.MigrateAsync(services, Token);
        var first = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, Token);
        var second = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, Token);
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IHostedDiscoveryStore>();
        var receipt = await store.AdmitAsync(new(Guid.CreateVersion7(), first, 1, new("https://fixture.test", "operator")), Token);
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        await Reject($"UPDATE \"ProviderDiscoveryGenerations\" SET \"ExecutionSlot\"=NULL WHERE \"RefreshId\"={receipt.RefreshId}", PostgresErrorCodes.CheckViolation);
        await Reject($"UPDATE \"ProviderDiscoveryGenerations\" SET \"LeaseExpiresAtUtc\"=NULL WHERE \"RefreshId\"={receipt.RefreshId}", PostgresErrorCodes.CheckViolation);
        await Reject($"UPDATE \"ProviderDiscoveryGenerations\" SET \"ExecutorId\"={Guid.NewGuid()} WHERE \"RefreshId\"={receipt.RefreshId}", PostgresErrorCodes.CheckViolation);
        await Reject($"UPDATE \"ProviderDiscoveryGenerations\" SET \"Status\"='Staging' WHERE \"RefreshId\"={receipt.RefreshId}", PostgresErrorCodes.CheckViolation);
        await Reject($"UPDATE \"ProviderDiscoveryGenerations\" SET \"RequestingSubject\"=NULL WHERE \"RefreshId\"={receipt.RefreshId}", PostgresErrorCodes.CheckViolation);
        await Reject($"UPDATE \"ProviderDiscoveryGenerations\" SET \"PublicationUncertain\"=true WHERE \"RefreshId\"={receipt.RefreshId}", PostgresErrorCodes.CheckViolation);
        await Reject($"UPDATE \"ProviderDiscoveryGenerations\" SET \"ExecutionSlot\"=2 WHERE \"RefreshId\"={receipt.RefreshId}", PostgresErrorCodes.CheckViolation);
        var row = await db.DiscoveryGenerations.AsNoTracking().SingleAsync(Token);
        row.RefreshId = Guid.CreateVersion7(); row.ProviderInstanceId = second;
        db.DiscoveryGenerations.Add(row); // Bypass the application's advisory lock and admission policy deliberately.
        var conflict = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Token));
        var postgres = Assert.IsType<PostgresException>(conflict.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal("UX_DiscoveryGeneration_HostedSlot", postgres.ConstraintName);
        async Task Reject(FormattableString sql, string code) =>
            Assert.Equal(code, (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(sql, Token))).SqlState);
    }

    [Theory]
    [InlineData("Failed", false, true)]
    [InlineData("Accepted", true, false)]
    [InlineData("Staging", true, false)]
    [InlineData("Ready", true, false)]
    public async Task Rollback_guard_rejects_unresolved_publication_even_without_a_slot_and_active_executors(
        string status, bool slot, bool uncertain)
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync(Token);
        Assert.SkipWhen(database is null, "Configure isolated PostgreSQL 18.");
        await using var services = database!.CreateServices(null);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        await db.Database.OpenConnectionAsync(Token);
        // Execute the actual generated rollback guard, without applying the uncorrected Gate B.
        // A temporary relation shadows the public table only on this private connection.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TEMP TABLE "ProviderDiscoveryGenerations" (
              "Status" text NOT NULL, "ExecutionSlot" integer, "PublicationUncertain" boolean NOT NULL);
            """, Token);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"ProviderDiscoveryGenerations\" VALUES ({status}, {(slot ? 1 : (int?)null)}, {uncertain})", Token);
        var assembly = db.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(assembly.Migrations["20261026084710_DurableDiscoveryHosting"], db.Database.ProviderName!);
        var guard = Assert.IsType<SqlOperation>(migration.DownOperations[0]);
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(guard.Sql, Token));
        Assert.Equal(PostgresErrorCodes.RaiseException, error.SqlState);
        Assert.Equal(1, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM \"ProviderDiscoveryGenerations\"").SingleAsync(Token));
    }
}
