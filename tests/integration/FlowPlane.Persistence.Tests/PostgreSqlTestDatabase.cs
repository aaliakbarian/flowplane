using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using FlowPlane.Infrastructure;
using FlowPlane.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace FlowPlane.Persistence.Tests;

internal sealed class PostgreSqlTestDatabase : IAsyncDisposable
{
    private readonly NpgsqlConnection adminConnection;

    private PostgreSqlTestDatabase(
        string databaseName,
        string connectionString,
        NpgsqlConnection adminConnection)
    {
        DatabaseName = databaseName;
        ConnectionString = connectionString;
        this.adminConnection = adminConnection;
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

        var databaseName = $"flowplane_a09_{Guid.NewGuid():N}";
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

        await using var command = adminConnection.CreateCommand();
        command.CommandText = $"CREATE DATABASE {new NpgsqlCommandBuilder().QuoteIdentifier(databaseName)}";
        await command.ExecuteNonQueryAsync(cancellationToken);

        return new PostgreSqlTestDatabase(databaseName, connectionString, adminConnection);
    }

    public ServiceProvider CreateServices(string? certificatePath, Action<IServiceCollection>? configure = null)
        => CreateServicesForConnection(ConnectionString, certificatePath, configure);

    internal static ServiceProvider CreateServicesForConnection(string connectionString, string? certificatePath,
        Action<IServiceCollection>? configure = null)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var values = new Dictionary<string, string?>
        {
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
        await context.Database.MigrateAsync(cancellationToken);
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
        await using var command = adminConnection.CreateCommand();
        command.CommandText =
            $"DROP DATABASE {new NpgsqlCommandBuilder().QuoteIdentifier(DatabaseName)} WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
        await adminConnection.DisposeAsync();
    }
}
