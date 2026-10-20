using FlowPlane.Providers.Ssis.Configuration;

using Microsoft.Data.SqlClient;

namespace FlowPlane.Providers.Ssis.Validation;

internal static class SsisConnectionSettingsFactory
{
    public static SqlConnectionStringBuilder Create(
        SsisProviderConfiguration configuration,
        ReadOnlyMemory<char> password,
        string catalog,
        SsisValidationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Create(configuration, password, catalog, options.ConnectionTimeoutSeconds);
    }

    public static SqlConnectionStringBuilder Create(
        SsisProviderConfiguration configuration,
        ReadOnlyMemory<char> password,
        string catalog,
        int connectionTimeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalog);
        if (connectionTimeoutSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(connectionTimeoutSeconds),
                "The connection timeout must be positive.");
        }

        return new SqlConnectionStringBuilder
        {
            DataSource = $"tcp:{configuration.Host},{configuration.Port}",
            InitialCatalog = catalog,
            UserID = configuration.Username,
            Password = new string(password.Span),
            IntegratedSecurity = false,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = configuration.TrustServerCertificate,
            PersistSecurityInfo = false,
            Pooling = false,
            ConnectTimeout = connectionTimeoutSeconds,
            ApplicationName = "FlowPlane",
        };
    }
}
