using System.Data;

using FlowPlane.Providers.Ssis.Configuration;
using FlowPlane.Providers.Ssis.Validation;

using Microsoft.Data.SqlClient;

namespace FlowPlane.Providers.Ssis.Discovery;

internal sealed class SsisSqlDiscoveryQuery : ISsisDiscoveryQuery
{
    internal const string CatalogSurfaceProbeSql =
        """
        SELECT TOP (0) [folder_id], [name] FROM [catalog].[folders];
        SELECT TOP (0) [project_id], [folder_id], [name], [project_format_version],
            [last_deployed_time], [object_version_lsn] FROM [catalog].[projects];
        SELECT TOP (0) [package_id], [project_id], [name], [package_guid],
            [package_format_version], [version_major], [version_minor], [version_build],
            [version_guid], [entry_point] FROM [catalog].[packages];
        SELECT TOP (0) [parameter_id], [project_id], [object_type], [object_name], [parameter_name], [data_type],
            [required], [sensitive], [value_type], [value_set], [referenced_variable_name]
            FROM [catalog].[object_parameters];
        SELECT TOP (0) [reference_id], [project_id], [reference_type],
            [environment_folder_name], [environment_name] FROM [catalog].[environment_references];
        """;

    internal const string ManifestSql =
        """
        SELECT COALESCE([folder].[folder_id], [project].[folder_id]), [folder].[name],
            [project].[project_id], [project].[name], [project].[project_format_version],
            [project].[object_version_lsn], [project].[last_deployed_time]
        FROM [catalog].[folders] AS [folder]
        FULL OUTER JOIN [catalog].[projects] AS [project]
            ON [project].[folder_id] = [folder].[folder_id]
        ORDER BY COALESCE([folder].[folder_id], [project].[folder_id]), [project].[project_id];
        """;

    private const string RevisionSql =
        """
        SELECT [folder].[folder_id], [folder].[name],
            [project].[project_id], [project].[name], [project].[project_format_version],
            [project].[object_version_lsn], [project].[last_deployed_time]
        FROM [catalog].[projects] AS [project]
        INNER JOIN [catalog].[folders] AS [folder]
            ON [folder].[folder_id] = [project].[folder_id]
        WHERE [project].[project_id] IN ({0})
        ORDER BY [project].[project_id];
        """;

    private const string MetadataSql =
        """
        SELECT [package].[package_id], [package].[project_id], [package].[name],
            [package].[entry_point], [package].[package_format_version], [package].[package_guid],
            [package].[version_major], [package].[version_minor], [package].[version_build],
            [package].[version_guid]
        FROM [catalog].[packages] AS [package]
        WHERE [package].[project_id] IN ({0})
        ORDER BY [package].[project_id], [package].[package_id];

        SELECT [parameter].[project_id], [parameter].[object_type], [parameter].[object_name],
            [parameter].[parameter_name], [parameter].[data_type], [parameter].[required],
            [parameter].[sensitive], [parameter].[value_type], [parameter].[value_set],
            [parameter].[referenced_variable_name]
        FROM [catalog].[object_parameters] AS [parameter]
        WHERE [parameter].[project_id] IN ({0})
          AND [parameter].[object_type] IN (20, 30)
        ORDER BY [parameter].[project_id], [parameter].[parameter_id];

        SELECT [reference].[project_id], [reference].[reference_id], [reference].[reference_type],
            [reference].[environment_folder_name], [reference].[environment_name]
        FROM [catalog].[environment_references] AS [reference]
        WHERE [reference].[project_id] IN ({0})
        ORDER BY [reference].[project_id], [reference].[reference_id];
        """;

    public async ValueTask<ISsisDiscoverySession> OpenSessionAsync(
        SsisProviderConfiguration configuration, ReadOnlyMemory<char> password,
        SsisDiscoveryOptions options, CancellationToken cancellationToken)
    {
        options.Validate();
        var builder = SsisConnectionSettingsFactory.Create(configuration, password,
            SsisProviderConfiguration.DefaultCatalog, options.ConnectionTimeoutSeconds);
        var connection = new SqlConnection(builder.ConnectionString);
        builder.Password = string.Empty;
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(CatalogSurfaceProbeSql, connection)
            {
                CommandTimeout = options.CommandTimeoutSeconds,
            };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.NextResultAsync(cancellationToken)) { }
            return new SsisSqlDiscoverySession(connection, options);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    internal static SqlCommand CreateProjectBatchCommand(SqlConnection connection,
        IReadOnlyList<SsisProjectManifestEntry> projects, SsisDiscoveryOptions options)
    {
        options.Validate();
        if (projects.Count == 0 || projects.Count > options.MaxProjectsPerBatch ||
            projects.Any(project => project.ProjectId is null) ||
            projects.Select(project => project.ProjectId).Distinct().Count() != projects.Count)
            throw new ArgumentException("Invalid SSIS internal project batch.", nameof(projects));

        var parameterNames = string.Join(", ", Enumerable.Range(0, projects.Count)
            .Select(index => $"@project{index}"));
        var sql = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            RevisionSql + "\n" + MetadataSql + "\n" + RevisionSql, parameterNames);
        var command = new SqlCommand(sql, connection) { CommandTimeout = options.CommandTimeoutSeconds };
        for (var index = 0; index < projects.Count; index++)
            command.Parameters.Add($"@project{index}", SqlDbType.BigInt).Value = projects[index].ProjectId!.Value;
        return command;
    }
}
