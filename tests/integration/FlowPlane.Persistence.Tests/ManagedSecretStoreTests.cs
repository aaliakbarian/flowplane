using FlowPlane.Application.Secrets;
using FlowPlane.Domain.Providers;
using FlowPlane.Infrastructure;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace FlowPlane.Persistence.Tests;

public sealed class ManagedSecretStoreTests
{
    [Fact]
    public async Task Missing_root_certificate_does_not_prevent_boot_and_flowplane_operations_fail_safely()
    {
        var services = new ServiceCollection();
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FlowPlane:PostgreSql:Host"] = "127.0.0.1",
                ["FlowPlane:PostgreSql:Database"] = "flowplane",
                ["FlowPlane:PostgreSql:Username"] = "flowplane",
                ["FlowPlane:PostgreSql:Password"] = "not-used",
            })
            .Build();
        services.AddLogging();
        services.AddFlowPlanePostgreSql(configuration);
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IManagedSecretStore>();

        var exception = await Assert.ThrowsAsync<SecretResolutionException>(async () =>
            await store.CreateAsync(
                "never-persisted".AsMemory(),
                TestContext.Current.CancellationToken));

        Assert.Equal(SecretErrorCode.ProtectionUnavailable, exception.Code);
        Assert.DoesNotContain("never-persisted", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Managed_secrets_are_isolated_encrypted_and_survive_restart_with_same_root()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(cancellationToken);
        Assert.SkipWhen(database is null, "Set FLOWPLANE_TEST_POSTGRES_CONNECTION_STRING to run this test.");
        var directory = Path.Combine(Path.GetTempPath(), $"flowplane-a09-{Guid.NewGuid():N}");
        var certificatePath = PostgreSqlTestDatabase.CreateRootCertificate(directory, "root-a");
        const string firstCanary = "a09-first-plaintext-canary";
        const string secondCanary = "a09-second-plaintext-canary";

        try
        {
            SecretReference firstReference;
            SecretReference secondReference;
            await using (var services = database!.CreateServices(certificatePath))
            {
                await database.MigrateAsync(services, cancellationToken);
                await using var scope = services.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IManagedSecretStore>();
                var resolver = scope.ServiceProvider.GetRequiredService<ISecretResolver>();
                firstReference = await store.CreateAsync(firstCanary.AsMemory(), cancellationToken);
                secondReference = await store.CreateAsync(secondCanary.AsMemory(), cancellationToken);

                Assert.Equal(firstCanary, await ResolveAsync(resolver, firstReference, cancellationToken));
                Assert.Equal(secondCanary, await ResolveAsync(resolver, secondReference, cancellationToken));
            }

            await AssertDatabaseDoesNotContainAsync(
                database.ConnectionString,
                [firstCanary, secondCanary],
                cancellationToken);
            await AssertProtectedKeyRingAndSecretMetadataAsync(
                database.ConnectionString,
                cancellationToken);

            await using (var restarted = database.CreateServices(certificatePath))
            {
                await using var scope = restarted.CreateAsyncScope();
                var resolver = scope.ServiceProvider.GetRequiredService<ISecretResolver>();
                Assert.Equal(firstCanary, await ResolveAsync(resolver, firstReference, cancellationToken));
                Assert.Equal(secondCanary, await ResolveAsync(resolver, secondReference, cancellationToken));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Wrong_missing_tampered_or_moved_protection_material_fails_without_plaintext_disclosure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(cancellationToken);
        Assert.SkipWhen(database is null, "Set FLOWPLANE_TEST_POSTGRES_CONNECTION_STRING to run this test.");
        var directory = Path.Combine(Path.GetTempPath(), $"flowplane-a09-{Guid.NewGuid():N}");
        var correctCertificate = PostgreSqlTestDatabase.CreateRootCertificate(directory, "correct");
        var wrongCertificate = PostgreSqlTestDatabase.CreateRootCertificate(directory, "wrong");
        const string firstCanary = "a09-purpose-first-canary";
        const string secondCanary = "a09-purpose-second-canary";

        try
        {
            SecretReference firstReference;
            SecretReference secondReference;
            await using (var services = database!.CreateServices(correctCertificate))
            {
                await database.MigrateAsync(services, cancellationToken);
                await using var scope = services.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IManagedSecretStore>();
                firstReference = await store.CreateAsync(firstCanary.AsMemory(), cancellationToken);
                secondReference = await store.CreateAsync(secondCanary.AsMemory(), cancellationToken);
            }

            await using (var missing = database.CreateServices(certificatePath: null))
            {
                await AssertSafeResolutionFailureAsync(missing, firstReference, cancellationToken);
            }

            await using (var wrong = database.CreateServices(wrongCertificate))
            {
                await AssertSafeResolutionFailureAsync(wrong, firstReference, cancellationToken);
            }

            await SwapCiphertextsAsync(
                database.ConnectionString,
                firstReference,
                secondReference,
                cancellationToken);
            await using (var moved = database.CreateServices(correctCertificate))
            {
                await AssertSafeResolutionFailureAsync(moved, secondReference, cancellationToken);
            }

            await TamperCiphertextAsync(database.ConnectionString, firstReference, cancellationToken);
            await using (var tampered = database.CreateServices(correctCertificate))
            {
                await AssertSafeResolutionFailureAsync(tampered, firstReference, cancellationToken);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ValueTask<string> ResolveAsync(
        ISecretResolver resolver,
        SecretReference reference,
        CancellationToken cancellationToken) =>
        resolver.UseAsync(
            reference,
            static (secret, _) => ValueTask.FromResult(new string(secret.Span)),
            cancellationToken);

    private static async Task AssertSafeResolutionFailureAsync(
        ServiceProvider services,
        SecretReference reference,
        CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ISecretResolver>();
        var exception = await Assert.ThrowsAsync<SecretResolutionException>(async () =>
            await ResolveAsync(resolver, reference, cancellationToken));
        Assert.Contains(
            exception.Code,
            new[] { SecretErrorCode.ProtectionUnavailable, SecretErrorCode.ProtectionFailed });
        Assert.DoesNotContain("canary", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task AssertDatabaseDoesNotContainAsync(
        string connectionString,
        IReadOnlyCollection<string> canaries,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (var canary in canaries)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    EXISTS (
                        SELECT 1 FROM "FlowPlaneSecrets"
                        WHERE position(convert_to(@canary, 'UTF8') in "Ciphertext") > 0)
                    OR EXISTS (
                        SELECT 1 FROM "DataProtectionKeys"
                        WHERE position(@canary in COALESCE("Xml", '')) > 0);
                """;
            command.Parameters.AddWithValue("canary", canary);
            Assert.False((bool)(await command.ExecuteScalarAsync(cancellationToken))!);
        }
    }

    private static async Task SwapCiphertextsAsync(
        string connectionString,
        SecretReference source,
        SecretReference destination,
        CancellationToken cancellationToken)
    {
        Assert.True(source.TryGetFlowPlaneId(out var sourceId));
        Assert.True(destination.TryGetFlowPlaneId(out var destinationId));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE "FlowPlaneSecrets" AS destination
            SET "Ciphertext" = source."Ciphertext"
            FROM "FlowPlaneSecrets" AS source
            WHERE destination."Id" = @destination AND source."Id" = @source;
            """;
        command.Parameters.AddWithValue("source", sourceId);
        command.Parameters.AddWithValue("destination", destinationId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
    }

    private static async Task AssertProtectedKeyRingAndSecretMetadataAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var keyRing = connection.CreateCommand())
        {
            keyRing.CommandText = "SELECT \"Xml\" FROM \"DataProtectionKeys\";";
            var xml = (string)(await keyRing.ExecuteScalarAsync(cancellationToken))!;
            Assert.Contains("encryptedSecret", xml, StringComparison.Ordinal);
        }

        await using (var metadata = connection.CreateCommand())
        {
            metadata.CommandText =
                """
                SELECT count(*)
                FROM "FlowPlaneSecrets"
                WHERE "ProtectionScheme" = 'aspnet-data-protection'
                  AND "FormatVersion" = 'v1'
                  AND "Version" = 1;
                """;
            Assert.Equal(2L, (long)(await metadata.ExecuteScalarAsync(cancellationToken))!);
        }
    }

    private static async Task TamperCiphertextAsync(
        string connectionString,
        SecretReference reference,
        CancellationToken cancellationToken)
    {
        Assert.True(reference.TryGetFlowPlaneId(out var id));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE "FlowPlaneSecrets"
            SET "Ciphertext" = set_byte("Ciphertext", 0, get_byte("Ciphertext", 0) # 1)
            WHERE "Id" = @id;
            """;
        command.Parameters.AddWithValue("id", id);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
    }
}
