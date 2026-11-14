using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;

using FlowPlane.Infrastructure;
using FlowPlane.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace FlowPlane.Persistence.Tests;

internal sealed record TestDatabaseIdentity(string Name, long Oid, string Owner, string Marker);

internal sealed class PostgreSqlTestDatabase : IAsyncDisposable
{
    internal static void VerifyCleanupIdentity(TestDatabaseIdentity expected, TestDatabaseIdentity? actual)
    {
        if (!Regex.IsMatch(expected.Name, "^flowplane_agent_[a-f0-9]{12}_[a-f0-9]{32}$") || expected.Oid <= 0 ||
            string.IsNullOrWhiteSpace(expected.Owner) || !expected.Marker.StartsWith("flowplane-test:", StringComparison.Ordinal) || expected != actual)
            throw new InvalidOperationException("Test database ownership is uncertain; resource preserved.");
    }
    private readonly NpgsqlConnection adminConnection;
    private readonly TestDatabaseIdentity identity;
    private static readonly object EvidenceLock = new();
    private static readonly string RunId = Environment.GetEnvironmentVariable("FLOWPLANE_TEST_AGENT_RUN_ID") ?? Guid.NewGuid().ToString("N");
    internal static string InvocationId => RunId;
    internal static void RequireCurrentRunDatabase(string? name)
    {
        if (name is null || !Regex.IsMatch(name, "^flowplane_agent_[a-f0-9]{12}_[a-f0-9]{32}$") ||
            !name.StartsWith($"flowplane_agent_{RunId[..12]}_", StringComparison.Ordinal))
            throw new InvalidOperationException("Child process target is not an isolated database from this invocation.");
    }

    private PostgreSqlTestDatabase(
        string databaseName,
        string connectionString,
        NpgsqlConnection adminConnection,
        TestDatabaseIdentity identity)
    {
        DatabaseName = databaseName;
        ConnectionString = connectionString;
        this.adminConnection = adminConnection;
        this.identity = identity;
    }

    public string DatabaseName { get; }

    public string ConnectionString { get; }

    public static async Task<PostgreSqlTestDatabase?> CreateAsync(CancellationToken cancellationToken)
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(
            "FLOWPLANE_TEST_POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            return null;
        }

        Assert.SkipUnless(Environment.GetEnvironmentVariable("FLOWPLANE_TEST_DISCOVERY_HOSTING_MIGRATION_APPROVED") == "true",
            "Gate B is not approved for application; no isolated database is created or migrated.");
        Assert.SkipUnless(Environment.GetEnvironmentVariable("FLOWPLANE_TEST_AGENT_MIGRATION_APPROVED") == "true",
            "F3 requires separate isolated migration approval; no database is created or migrated.");

        if (!Regex.IsMatch(RunId, "^[a-f0-9]{32}$")) throw new InvalidOperationException("Invalid isolated test invocation.");
        var databaseName = $"flowplane_agent_{RunId[..12]}_{Guid.NewGuid():N}";
        var adminConnectionString = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Database = "postgres",
            Pooling = false,
        }.ConnectionString;
        var connectionString = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Database = databaseName,
            Pooling = false,
        }.ConnectionString;
        var adminConnection = new NpgsqlConnection(adminConnectionString);
        await adminConnection.OpenAsync(cancellationToken);

        await using (var exists = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname=@name)", adminConnection))
        {
            exists.Parameters.AddWithValue("name", databaseName);
            if ((bool)(await exists.ExecuteScalarAsync(cancellationToken))!) throw new InvalidOperationException("Test database already exists; no reuse permitted.");
        }

        await using var command = adminConnection.CreateCommand();
        command.CommandText = $"CREATE DATABASE {new NpgsqlCommandBuilder().QuoteIdentifier(databaseName)}";
        await command.ExecuteNonQueryAsync(cancellationToken);
        var marker = $"flowplane-test:{RunId}:{Guid.NewGuid():N}";
        command.CommandText = $"COMMENT ON DATABASE {new NpgsqlCommandBuilder().QuoteIdentifier(databaseName)} IS '{marker}'";
        await command.ExecuteNonQueryAsync(cancellationToken);
        var identity = await ReadIdentity(adminConnection, databaseName, cancellationToken)
            ?? throw new InvalidOperationException("Created test database cannot be identified; preserve for review.");
        if (identity.Marker != marker || identity.Owner != adminConnection.UserName)
            throw new InvalidOperationException("Created database ownership mismatch; preserve for review.");
        var result = new PostgreSqlTestDatabase(databaseName, connectionString, adminConnection, identity);
        result.RecordEvidence("created", []);
        return result;
    }

    public ServiceProvider CreateServices(string? certificatePath, Action<IServiceCollection>? configure = null)
        => CreateServicesForConnection(ConnectionString, certificatePath, configure);

    internal static ServiceProvider CreateServicesForConnection(string connectionString, string? certificatePath,
        Action<IServiceCollection>? configure = null)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:FlowPlane"] = builder.ConnectionString,
            ["FlowPlane:PostgreSql:Host"] = builder.Host,
            ["FlowPlane:PostgreSql:Port"] = builder.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["FlowPlane:PostgreSql:Database"] = builder.Database,
            ["FlowPlane:PostgreSql:Username"] = builder.Username,
            ["FlowPlane:PostgreSql:Password"] = builder.Password,
            ["FlowPlane:Secrets:RootCertificatePath"] = certificatePath,
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlowPlanePostgreSql(configuration);
        configure?.Invoke(services);
        return services.BuildServiceProvider(validateScopes: true);
    }

    public async Task MigrateAsync(ServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        RecordEvidence("before-migration", (await context.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray());
        await context.Database.MigrateAsync(cancellationToken);
        RecordEvidence("after-migration", (await context.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray());
    }

    public static string CreateRootCertificate(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={name}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment,
            critical: true));
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(2));
        var path = Path.Combine(directory, $"{name}.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx));
        return path;
    }

    public async ValueTask DisposeAsync()
    {
        VerifyCleanupIdentity(identity, await ReadIdentity(adminConnection, DatabaseName, CancellationToken.None));
        RecordEvidence("cleanup-verified", []);
        await using var command = adminConnection.CreateCommand();
        command.CommandText =
            $"DROP DATABASE {new NpgsqlCommandBuilder().QuoteIdentifier(DatabaseName)}";
        await command.ExecuteNonQueryAsync();
        if (await ReadIdentity(adminConnection, DatabaseName, CancellationToken.None) is not null)
            throw new InvalidOperationException("Isolated database cleanup could not be verified.");
        RecordEvidence("removed", []);
        await adminConnection.DisposeAsync();
    }

    private static async Task<TestDatabaseIdentity?> ReadIdentity(NpgsqlConnection connection, string name, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT oid::bigint, pg_get_userbyid(datdba), coalesce(shobj_description(oid,'pg_database'),'') FROM pg_database WHERE datname=@name", connection);
        command.Parameters.AddWithValue("name", name);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? new(name, reader.GetInt64(0), reader.GetString(1), reader.GetString(2)) : null;
    }

    internal void RecordEvidence(string phase, string[] migrations)
    {
        var directory = Environment.GetEnvironmentVariable("FLOWPLANE_TEST_AGENT_EVIDENCE_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory)) return;
        var line = JsonSerializer.Serialize(new { phase, identity, RunId, migrations });
        lock (EvidenceLock) File.AppendAllText(Path.Combine(directory, "databases.jsonl"), line + Environment.NewLine);
    }
}
