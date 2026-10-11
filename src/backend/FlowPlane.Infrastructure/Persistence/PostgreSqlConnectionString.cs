using Microsoft.Extensions.Configuration;

using Npgsql;

namespace FlowPlane.Infrastructure.Persistence;

internal static class PostgreSqlConnectionString
{
    private const string ConfigurationPrefix = "FlowPlane:PostgreSql:";
    private const string EnvironmentPrefix = "FlowPlane__PostgreSql__";

    public static string Resolve(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("FlowPlane");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString;
        }

        return Build(
            key => configuration[$"{ConfigurationPrefix}{key}"],
            key => $"{ConfigurationPrefix}{key}");
    }

    public static string ResolveFromEnvironment()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "ConnectionStrings__FlowPlane");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString;
        }

        return Build(
            key => Environment.GetEnvironmentVariable($"{EnvironmentPrefix}{key}"),
            key => $"{EnvironmentPrefix}{key}");
    }

    private static string Build(
        Func<string, string?> getValue,
        Func<string, string> getDisplayName)
    {
        var portValue = getValue("Port");
        var port = 5432;
        if (portValue is not null
            && (!int.TryParse(portValue, out port) || port is < 1 or > 65535))
        {
            throw new InvalidOperationException(
                $"{getDisplayName("Port")} must be a valid TCP port.");
        }

        return new NpgsqlConnectionStringBuilder
        {
            Host = GetRequired("Host"),
            Port = port,
            Database = GetRequired("Database"),
            Username = GetRequired("Username"),
            Password = GetRequired("Password")
        }.ConnectionString;

        string GetRequired(string key)
        {
            var value = getValue(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            throw new InvalidOperationException(
                $"{getDisplayName(key)} is not configured.");
        }
    }
}
