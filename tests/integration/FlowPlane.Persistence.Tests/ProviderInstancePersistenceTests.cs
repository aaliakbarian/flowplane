using FlowPlane.Application.Providers;
using FlowPlane.Application.Secrets;
using FlowPlane.Domain.Providers;
using FlowPlane.Infrastructure.Persistence;
using FlowPlane.Providers.Ssis.Configuration;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace FlowPlane.Persistence.Tests;

public sealed class ProviderInstancePersistenceTests
{
    [Fact]
    public async Task Environment_reference_round_trips_with_optimistic_concurrency_without_persisting_value()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(cancellationToken);
        Assert.SkipWhen(database is null, "Set FLOWPLANE_TEST_POSTGRES_CONNECTION_STRING to run this test.");
        const string environmentName = "FLOWPLANE_A09_ENV_PROVIDER_SECRET";
        const string environmentCanary = "a09-environment-value-never-persisted";
        Environment.SetEnvironmentVariable(environmentName, environmentCanary);
        var document = new SsisProviderConfigurationCodec().Encode(
            new SsisProviderConfiguration("sql-env", 1433, "flowplane_env", false));
        var instance = ProviderInstance.Create(
            Guid.CreateVersion7(),
            SsisProviderConfigurationCodec.SsisProviderType,
            "Environment-backed SSIS",
            true,
            document,
            SecretReference.Environment(environmentName),
            DateTimeOffset.UtcNow);

        try
        {
            await using var services = database!.CreateServices(certificatePath: null);
            await database.MigrateAsync(services, cancellationToken);
            await using (var scope = services.CreateAsyncScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
                context.ProviderInstances.Add(instance);
                await context.SaveChangesAsync(cancellationToken);
            }

            await using var firstScope = services.CreateAsyncScope();
            await using var secondScope = services.CreateAsyncScope();
            var firstContext = firstScope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
            var secondContext = secondScope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
            var first = await firstContext.ProviderInstances.SingleAsync(cancellationToken);
            var second = await secondContext.ProviderInstances.SingleAsync(cancellationToken);
            var changedAt = DateTimeOffset.UtcNow.AddMinutes(1);
            first.UpdateConfiguration(
                "Environment-backed SSIS A",
                true,
                document,
                changedAt);
            await firstContext.SaveChangesAsync(cancellationToken);
            second.UpdateConfiguration(
                "Environment-backed SSIS B",
                true,
                document,
                changedAt);

            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(async () =>
                await secondContext.SaveChangesAsync(cancellationToken));

            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var scan = connection.CreateCommand();
            scan.CommandText =
                """
                SELECT
                    position(@canary in "SecretReference") > 0
                    OR position(@canary in "Configuration"::text) > 0
                FROM "ProviderInstances";
                """;
            scan.Parameters.AddWithValue("canary", environmentCanary);
            Assert.False((bool)(await scan.ExecuteScalarAsync(cancellationToken))!);
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentName, null);
        }
    }

    [Fact]
    public async Task Provider_instance_and_owned_secret_lifecycle_is_atomic_and_reference_preserving()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(cancellationToken);
        Assert.SkipWhen(database is null, "Set FLOWPLANE_TEST_POSTGRES_CONNECTION_STRING to run this test.");
        var directory = Path.Combine(Path.GetTempPath(), $"flowplane-a09-{Guid.NewGuid():N}");
        var certificatePath = PostgreSqlTestDatabase.CreateRootCertificate(directory, "provider-root");
        var codec = new SsisProviderConfigurationCodec();
        var document = codec.Encode(new SsisProviderConfiguration("sql-prod", 1433, "flowplane_prod", false));
        var id = Guid.Parse("018f8f55-82e7-734b-ab9a-a2d355bc9f6d");
        var createdAt = DateTimeOffset.Parse("2026-10-13T10:00:00Z");

        try
        {
            await using var services = database!.CreateServices(certificatePath);
            await database.MigrateAsync(services, cancellationToken);
            SecretReference originalReference;

            await using (var scope = services.CreateAsyncScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<IProviderInstanceStore>();
                var instance = await store.CreateWithManagedSecretAsync(
                    id,
                    SsisProviderConfigurationCodec.SsisProviderType,
                    "Production SSIS",
                    isEnabled: true,
                    document,
                    "first-password-canary".AsMemory(),
                    createdAt,
                    cancellationToken);
                originalReference = instance.SecretReference;

                await store.ReplaceManagedSecretAsync(
                    id,
                    expectedVersion: 1,
                    "replacement-password-canary".AsMemory(),
                    createdAt.AddMinutes(1),
                    cancellationToken);
            }

            await using (var scope = services.CreateAsyncScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
                var reloaded = await context.ProviderInstances.SingleAsync(
                    item => item.Id == id,
                    cancellationToken);
                Assert.Equal(originalReference, reloaded.SecretReference);
                Assert.Equal(2, reloaded.Version);
                Assert.Equal(document, reloaded.Configuration);

                var resolver = scope.ServiceProvider.GetRequiredService<ISecretResolver>();
                var resolved = await resolver.UseAsync(
                    reloaded.SecretReference,
                    static (secret, _) => ValueTask.FromResult(new string(secret.Span)),
                    cancellationToken);
                Assert.Equal("replacement-password-canary", resolved);
            }

            await AssertOnlyReferencesPersistedAsync(database.ConnectionString, cancellationToken);

            await using (var scope = services.CreateAsyncScope())
            {
                var managedSecretStore = scope.ServiceProvider.GetRequiredService<IManagedSecretStore>();
                var directDelete = await Assert.ThrowsAsync<SecretResolutionException>(async () =>
                    await managedSecretStore.DeleteAsync(originalReference, cancellationToken));
                Assert.Equal(SecretErrorCode.ConcurrencyConflict, directDelete.Code);

                var store = scope.ServiceProvider.GetRequiredService<IProviderInstanceStore>();
                await store.DeleteAsync(id, expectedVersion: 2, cancellationToken);
            }

            await using (var connection = new NpgsqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync(cancellationToken);
                await using var counts = connection.CreateCommand();
                counts.CommandText =
                    """
                    SELECT
                      (SELECT count(*) FROM "ProviderInstances"),
                      (SELECT count(*) FROM "FlowPlaneSecrets");
                    """;
                await using var reader = await counts.ExecuteReaderAsync(cancellationToken);
                Assert.True(await reader.ReadAsync(cancellationToken));
                Assert.Equal(0L, reader.GetInt64(0));
                Assert.Equal(0L, reader.GetInt64(1));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Failed_provider_creation_does_not_leave_orphaned_managed_secret()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(cancellationToken);
        Assert.SkipWhen(database is null, "Set FLOWPLANE_TEST_POSTGRES_CONNECTION_STRING to run this test.");
        var directory = Path.Combine(Path.GetTempPath(), $"flowplane-a09-{Guid.NewGuid():N}");
        var certificatePath = PostgreSqlTestDatabase.CreateRootCertificate(directory, "rollback-root");
        var document = new SsisProviderConfigurationCodec().Encode(
            new SsisProviderConfiguration("sql-test", 1433, "flowplane_test", false));
        var id = Guid.Parse("018f8f55-82e7-734b-ab9a-a2d355bc9f6d");

        try
        {
            await using var services = database!.CreateServices(certificatePath);
            await database.MigrateAsync(services, cancellationToken);
            await using var scope = services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IProviderInstanceStore>();
            await store.CreateWithManagedSecretAsync(
                id,
                SsisProviderConfigurationCodec.SsisProviderType,
                "Test SSIS",
                true,
                document,
                "first".AsMemory(),
                DateTimeOffset.UtcNow,
                cancellationToken);

            await Assert.ThrowsAnyAsync<Exception>(async () =>
                await store.CreateWithManagedSecretAsync(
                    id,
                    SsisProviderConfigurationCodec.SsisProviderType,
                    "Duplicate SSIS",
                    true,
                    document,
                    "orphan-canary".AsMemory(),
                    DateTimeOffset.UtcNow,
                    cancellationToken));

            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var counts = connection.CreateCommand();
            counts.CommandText = "SELECT count(*) FROM \"FlowPlaneSecrets\";";
            Assert.Equal(1L, (long)(await counts.ExecuteScalarAsync(cancellationToken))!);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task AssertOnlyReferencesPersistedAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT "SecretReference", "Configuration"::text
            FROM "ProviderInstances";
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.StartsWith("flowplane:", reader.GetString(0), StringComparison.Ordinal);
        var configuration = reader.GetString(1);
        Assert.DoesNotContain("password", configuration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connectionString", configuration, StringComparison.OrdinalIgnoreCase);
    }
}
