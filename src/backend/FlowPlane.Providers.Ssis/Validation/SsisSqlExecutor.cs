using System.Net.Sockets;
using System.Security.Authentication;

using FlowPlane.Providers.Ssis.Configuration;

using Microsoft.Data.SqlClient;

namespace FlowPlane.Providers.Ssis.Validation;

internal sealed class SsisSqlExecutor : ISsisSqlExecutor
{
    private const string MasterProbeSql =
        """
        SELECT
            CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')),
            CONVERT(nvarchar(128), SERVERPROPERTY('ProductLevel')),
            CONVERT(nvarchar(128), SERVERPROPERTY('Edition')),
            HAS_DBACCESS(N'SSISDB');
        """;

    private const string CatalogSurfaceProbeSql =
        """
        SELECT TOP (0) [id], [name] FROM [catalog].[folders];
        SELECT TOP (0) [project_id], [folder_id], [project_format_version]
            FROM [catalog].[projects];
        SELECT TOP (0) [package_id], [project_id], [package_format_version]
            FROM [catalog].[packages];
        SELECT TOP (0) [object_type], [object_id], [permission_type]
            FROM [catalog].[effective_object_permissions];
        """;

    private const string CatalogEvidenceSql =
        """
        SELECT
            CONVERT(bit, CASE WHEN EXISTS (
                SELECT 1
                FROM [catalog].[effective_object_permissions]
                WHERE ([object_type] = 2 AND [permission_type] = 1)
                   OR ([object_type] = 1 AND [permission_type] = 101)
            ) THEN 1 ELSE 0 END) AS [has_readable_scope],
            CONVERT(bit, CASE WHEN EXISTS (
                SELECT 1
                FROM [catalog].[effective_object_permissions] AS [execute_permission]
                WHERE
                    ([execute_permission].[object_type] = 2
                     AND [execute_permission].[permission_type] = 3
                     AND EXISTS (
                         SELECT 1
                         FROM [catalog].[effective_object_permissions] AS [read_permission]
                         WHERE [read_permission].[object_type] = 2
                           AND [read_permission].[object_id] = [execute_permission].[object_id]
                           AND [read_permission].[permission_type] = 1))
                 OR ([execute_permission].[object_type] = 1
                     AND [execute_permission].[permission_type] = 103
                     AND EXISTS (
                         SELECT 1
                         FROM [catalog].[effective_object_permissions] AS [read_permission]
                         WHERE [read_permission].[object_type] = 1
                           AND [read_permission].[object_id] = [execute_permission].[object_id]
                           AND [read_permission].[permission_type] = 101))
            ) THEN 1 ELSE 0 END) AS [has_executable_readable_scope],
            CONVERT(bit, CASE WHEN EXISTS (SELECT 1 FROM [catalog].[projects])
                                   OR EXISTS (SELECT 1 FROM [catalog].[packages])
                THEN 1 ELSE 0 END) AS [has_visible_definitions];

        SELECT [definition_kind], [format_version]
        FROM (
            SELECT CONVERT(tinyint, 1) AS [definition_kind], [project_format_version] AS [format_version]
            FROM [catalog].[projects]
            GROUP BY [project_format_version]
            UNION ALL
            SELECT CONVERT(tinyint, 2) AS [definition_kind], [package_format_version] AS [format_version]
            FROM [catalog].[packages]
            GROUP BY [package_format_version]
        ) AS [visible_formats]
        ORDER BY [definition_kind], [format_version];
        """;

    public async Task<SsisMasterFacts> ReadMasterFactsAsync(
        SsisProviderConfiguration configuration,
        ReadOnlyMemory<char> password,
        SsisValidationOptions options,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(configuration, password, "master", options);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(MasterProbeSql, connection)
            {
                CommandTimeout = options.CommandTimeoutSeconds,
            };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new SsisProbeException(
                    SsisFailureKind.Unexpected,
                    SsisValidationStage.MasterConnection);
            }

            return new SsisMasterFacts(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SsisProbeException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedProviderFailure(exception))
        {
            throw SafeProbeException(exception, SsisValidationStage.MasterConnection);
        }
    }

    public async Task<SsisCatalogEvidence> ReadCatalogEvidenceAsync(
        SsisProviderConfiguration configuration,
        ReadOnlyMemory<char> password,
        SsisValidationOptions options,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(
            configuration,
            password,
            SsisProviderConfiguration.DefaultCatalog,
            options);
        try
        {
            await connection.OpenAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedProviderFailure(exception))
        {
            throw SafeProbeException(exception, SsisValidationStage.CatalogConnection);
        }

        try
        {
            await VerifyCatalogSurfaceAsync(connection, options, cancellationToken);
            return await ReadEvidenceAsync(connection, options, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedProviderFailure(exception))
        {
            throw SafeProbeException(exception, SsisValidationStage.CatalogQuery);
        }
    }

    private static SqlConnection CreateConnection(
        SsisProviderConfiguration configuration,
        ReadOnlyMemory<char> password,
        string catalog,
        SsisValidationOptions options)
    {
        var builder = SsisConnectionSettingsFactory.Create(
            configuration,
            password,
            catalog,
            options);
        var connection = new SqlConnection(builder.ConnectionString);
        builder.Password = string.Empty;
        return connection;
    }

    private static async Task VerifyCatalogSurfaceAsync(
        SqlConnection connection,
        SsisValidationOptions options,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(CatalogSurfaceProbeSql, connection)
        {
            CommandTimeout = options.CommandTimeoutSeconds,
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.NextResultAsync(cancellationToken))
        {
        }
    }

    private static async Task<SsisCatalogEvidence> ReadEvidenceAsync(
        SqlConnection connection,
        SsisValidationOptions options,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(CatalogEvidenceSql, connection)
        {
            CommandTimeout = options.CommandTimeoutSeconds,
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new SsisProbeException(SsisFailureKind.Unexpected, SsisValidationStage.CatalogQuery);
        }

        var permissions = new SsisPermissionEvidence(
            reader.GetBoolean(0),
            reader.GetBoolean(1),
            reader.GetBoolean(2),
            IsReadExplicitlyDenied: false,
            HasUnknownDefinitionFormat: false);

        if (!await reader.NextResultAsync(cancellationToken))
        {
            throw new SsisProbeException(SsisFailureKind.Unexpected, SsisValidationStage.CatalogQuery);
        }

        var projectFormats = new HashSet<int>();
        var packageFormats = new HashSet<int>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var target = reader.GetByte(0) == 1 ? projectFormats : packageFormats;
            target.Add(reader.GetInt32(1));
        }

        return new SsisCatalogEvidence(permissions, projectFormats, packageFormats);
    }

    private static SsisProbeException SafeProbeException(
        Exception exception,
        SsisValidationStage stage) => new(
            SsisSqlFailureClassifier.Classify(SsisSqlFailure.FromException(exception), stage),
            stage);

    private static bool IsExpectedProviderFailure(Exception exception) =>
        exception is SqlException or SocketException or AuthenticationException or InvalidOperationException;
}
