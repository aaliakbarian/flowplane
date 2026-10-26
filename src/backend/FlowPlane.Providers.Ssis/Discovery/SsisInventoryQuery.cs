using System.Data;

using FlowPlane.Application.Providers;
using FlowPlane.Providers.Ssis.Configuration;
using FlowPlane.Providers.Ssis.Validation;

using Microsoft.Data.SqlClient;

namespace FlowPlane.Providers.Ssis.Discovery;

internal sealed class SsisInventoryQuery : ISsisInventoryQuery
{
    public async ValueTask<ISsisInventorySession> OpenAsync(SsisProviderConfiguration configuration,
        ReadOnlyMemory<char> password, SsisValidationOptions options, CancellationToken token)
    {
        var settings = SsisConnectionSettingsFactory.Create(configuration, password, SsisProviderConfiguration.DefaultCatalog, options);
        var connection = new SqlConnection(settings.ConnectionString);
        settings.Password = string.Empty;
        try
        {
            await connection.OpenAsync(token);
            await using var surface = new SqlCommand(SsisSqlDiscoveryQuery.CatalogSurfaceProbeSql, connection) { CommandTimeout = options.CommandTimeoutSeconds };
            await using var reader = await surface.ExecuteReaderAsync(token);
            while (await reader.NextResultAsync(token)) { }
            return new Session(connection, options.CommandTimeoutSeconds);
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    private sealed class Session(SqlConnection connection, int timeout) : ISsisInventorySession
    {
        public async Task<ProviderDiscoveryObservation?> ReadAsync(Guid providerId, SsisDiscoveryMetadata expected, CancellationToken token)
        {
            await using var command = CreateCommand(connection, expected, timeout);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
            if (!await reader.ReadAsync(token)) return null;
            ProviderDiscoveryObservation observation;
            if (expected is SsisFolderMetadata)
                observation = SsisDiscoveryMapper.MapFolder(providerId, new(reader.GetInt64(0), reader.GetString(1), null, null, null, null, null));
            else
            {
                var project = new SsisProjectManifestEntry(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3),
                    reader.GetInt32(4), reader.GetInt64(5), reader.GetFieldValue<DateTimeOffset>(6));
                observation = expected switch
                {
                    SsisProjectMetadata => SsisDiscoveryMapper.MapProject(providerId, project),
                    SsisPackageMetadata => SsisDiscoveryMapper.Map(providerId, new SsisDiscoveredPackage(project,
                        reader.GetInt64(7), reader.GetString(8), reader.GetBoolean(9), reader.GetInt32(10), reader.GetGuid(11),
                        reader.GetInt32(12), reader.GetInt32(13), reader.GetInt32(14), reader.GetGuid(15))),
                    SsisParameterMetadata => SsisDiscoveryMapper.Map(providerId, ReadParameter(reader, project)),
                    SsisEnvironmentReferenceMetadata => SsisDiscoveryMapper.Map(providerId, new SsisDiscoveredEnvironmentReference(project,
                        reader.GetInt64(7), reader.GetString(8) == "R" ? SsisEnvironmentReferenceType.Relative : SsisEnvironmentReferenceType.Absolute,
                        reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetString(10))),
                    _ => throw new ArgumentException("Invalid disclosure schema."),
                };
            }
            // Ambiguous collation matches must not select an arbitrary native identity.
            if (await reader.ReadAsync(token)) return null;
            return observation;
        }
        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private static SsisDiscoveredParameter ReadParameter(SqlDataReader reader, SsisProjectManifestEntry project)
    {
        // SequentialAccess is forward-only even for repeated typed reads of a
        // single ordinal. Consume each value once, in SELECT order.
        var scope = reader.GetInt16(7) switch { 20 => SsisParameterScope.Project, 30 => SsisParameterScope.Package, _ => throw new SsisDiscoverySourceChangedException() };
        var objectName = reader.GetString(8);
        var name = reader.GetString(9);
        var type = reader.GetString(10);
        var required = reader.GetBoolean(11);
        var sensitive = reader.GetBoolean(12);
        var source = reader.IsDBNull(13) ? SsisParameterValueSource.Unknown : reader.GetString(13) switch
        { "V" => SsisParameterValueSource.Literal, "R" => SsisParameterValueSource.EnvironmentReference, _ => SsisParameterValueSource.Unknown };
        var set = reader.GetBoolean(14);
        var variable = reader.IsDBNull(15) ? null : reader.GetString(15);
        return new(project, scope, scope == SsisParameterScope.Package ? objectName : null, name, type, required, sensitive, source, set, variable);
    }

    internal static SqlCommand CreateCommand(SqlConnection connection, SsisDiscoveryMetadata metadata, int timeout)
    {
        var (folder, project) = metadata switch
        {
            SsisFolderMetadata x => (x.FolderName, (string?)null),
            SsisProjectMetadata x => (x.FolderName, x.ProjectName),
            SsisPackageMetadata x => (x.FolderName, x.ProjectName),
            SsisParameterMetadata x => (x.FolderName, x.ProjectName),
            SsisEnvironmentReferenceMetadata x => (x.FolderName, x.ProjectName),
            _ => throw new ArgumentException("Invalid disclosure schema."),
        };
        var command = new SqlCommand { Connection = connection, CommandTimeout = timeout };
        command.Parameters.Add("@folder", SqlDbType.NVarChar, 128).Value = folder;
        if (project is null)
        {
            command.CommandText = "SELECT TOP (2) [folder_id],[name] FROM [catalog].[folders] WHERE [name]=@folder;";
            return command;
        }
        command.Parameters.Add("@project", SqlDbType.NVarChar, 128).Value = project;
        const string fields = "[f].[folder_id],[f].[name],[p].[project_id],[p].[name],[p].[project_format_version],[p].[object_version_lsn],[p].[last_deployed_time]";
        const string from = " FROM [catalog].[projects] [p] JOIN [catalog].[folders] [f] ON [f].[folder_id]=[p].[folder_id]";
        var join = ""; var extra = ""; var predicate = "";
        switch (metadata)
        {
            case SsisPackageMetadata package:
                extra = ",[v].[package_id],[v].[name],[v].[entry_point],[v].[package_format_version],[v].[package_guid],[v].[version_major],[v].[version_minor],[v].[version_build],[v].[version_guid]";
                join = " JOIN [catalog].[packages] [v] ON [v].[project_id]=[p].[project_id]";
                predicate = " AND [v].[name]=@package";
                command.Parameters.Add("@package", SqlDbType.NVarChar, 256).Value = package.PackageName;
                break;
            case SsisParameterMetadata parameter:
                extra = ",[v].[object_type],[v].[object_name],[v].[parameter_name],[v].[data_type],[v].[required],[v].[sensitive],[v].[value_type],[v].[value_set],[v].[referenced_variable_name]";
                join = " JOIN [catalog].[object_parameters] [v] ON [v].[project_id]=[p].[project_id]";
                predicate = " AND [v].[object_type]=@scope AND [v].[object_name]=@object AND [v].[parameter_name]=@parameter";
                command.Parameters.Add("@scope", SqlDbType.SmallInt).Value = parameter.Scope == SsisParameterScope.Project ? 20 : 30;
                command.Parameters.Add("@object", SqlDbType.NVarChar, 260).Value = parameter.PackageName ?? parameter.ProjectName;
                command.Parameters.Add("@parameter", SqlDbType.NVarChar, 128).Value = parameter.Name;
                break;
            case SsisEnvironmentReferenceMetadata environment:
                extra = ",[v].[reference_id],[v].[reference_type],[v].[environment_folder_name],[v].[environment_name]";
                join = " JOIN [catalog].[environment_references] [v] ON [v].[project_id]=[p].[project_id]";
                predicate = " AND [v].[reference_id]=@reference";
                command.Parameters.Add("@reference", SqlDbType.BigInt).Value = environment.ReferenceId;
                break;
        }
        command.CommandText = "SELECT TOP (2) " + fields + extra + from + join + " WHERE [f].[name]=@folder AND [p].[name]=@project" + predicate + ";";
        return command;
    }
}
