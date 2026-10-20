using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;

using FlowPlane.Providers.Ssis.Validation;

using Microsoft.Data.SqlClient;

namespace FlowPlane.Providers.Ssis.Discovery;

internal sealed class SsisSqlDiscoverySession(SqlConnection connection, SsisDiscoveryOptions options)
    : ISsisDiscoverySession
{
    public async IAsyncEnumerable<SsisProjectManifestEntry> ReadManifestAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(SsisSqlDiscoveryQuery.ManifestSql, connection)
        {
            CommandTimeout = options.CommandTimeoutSeconds,
        };
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            yield return ReadProject(reader);
    }

    public async IAsyncEnumerable<SsisDiscoveryRow> ReadProjectBatchAsync(
        IReadOnlyList<SsisProjectManifestEntry> projects,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var byId = projects.ToDictionary(project => project.ProjectId!.Value);
        await using var command = SsisSqlDiscoveryQuery.CreateProjectBatchCommand(
            connection, projects, options);
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess, cancellationToken);
        await VerifyRevisionsAsync(reader, byId, cancellationToken);
        await NextAsync(reader, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var packageId = reader.GetInt64(0);
            var project = GetProject(byId, reader.GetInt64(1));
            yield return new SsisDiscoveredPackage(project, packageId, reader.GetString(2),
                reader.GetBoolean(3), reader.GetInt32(4), reader.GetGuid(5),
                reader.GetInt32(6), reader.GetInt32(7), reader.GetInt32(8), reader.GetGuid(9));
        }
        await NextAsync(reader, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var project = GetProject(byId, reader.GetInt64(0));
            var scope = reader.GetInt16(1) switch
            {
                20 => SsisParameterScope.Project,
                30 => SsisParameterScope.Package,
                _ => throw new SsisDiscoverySourceChangedException(),
            };
            var objectName = reader.GetString(2);
            yield return new SsisDiscoveredParameter(project, scope,
                scope == SsisParameterScope.Package ? objectName : null,
                reader.GetString(3), reader.GetString(4), reader.GetBoolean(5), reader.GetBoolean(6),
                reader.IsDBNull(7) ? SsisParameterValueSource.Unknown : reader.GetString(7) switch
                {
                    "V" => SsisParameterValueSource.Literal,
                    "R" => SsisParameterValueSource.EnvironmentReference,
                    _ => SsisParameterValueSource.Unknown,
                }, reader.GetBoolean(8), reader.IsDBNull(9) ? null : reader.GetString(9));
        }
        await NextAsync(reader, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var project = GetProject(byId, reader.GetInt64(0));
            var id = reader.GetInt64(1);
            var type = reader.GetString(2) switch
            {
                "R" => SsisEnvironmentReferenceType.Relative,
                "A" => SsisEnvironmentReferenceType.Absolute,
                _ => throw new SsisDiscoverySourceChangedException(),
            };
            yield return new SsisDiscoveredEnvironmentReference(project, id, type,
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4));
        }
        await NextAsync(reader, cancellationToken);
        await VerifyRevisionsAsync(reader, byId, cancellationToken);
        if (await reader.NextResultAsync(cancellationToken))
            throw new SsisDiscoverySourceChangedException();
    }

    internal static void VerifyRevision(SsisProjectManifestEntry expected, SsisProjectManifestEntry actual)
    {
        if (expected != actual)
            throw new SsisDiscoverySourceChangedException();
    }

    internal static async Task VerifyRevisionsAsync(DbDataReader reader,
        IReadOnlyDictionary<long, SsisProjectManifestEntry> expected, CancellationToken token)
    {
        var seen = new HashSet<long>();
        while (await reader.ReadAsync(token))
        {
            var actual = ReadProject(reader);
            if (actual.ProjectId is not { } id || !seen.Add(id))
                throw new SsisDiscoverySourceChangedException();
            VerifyRevision(GetProject(expected, id), actual);
        }
        if (seen.Count != expected.Count)
            throw new SsisDiscoverySourceChangedException();
    }

    private static SsisProjectManifestEntry GetProject(
        IReadOnlyDictionary<long, SsisProjectManifestEntry> projects, long id) =>
        projects.TryGetValue(id, out var project) ? project : throw new SsisDiscoverySourceChangedException();

    private static SsisProjectManifestEntry ReadProject(DbDataReader reader)
    {
        var folderId = reader.GetInt64(0);
        if (reader.IsDBNull(1))
            throw new SsisProbeException(SsisFailureKind.PermissionDenied, SsisValidationStage.DiscoveryQuery);
        return new(folderId, reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetInt64(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6));
    }

    private static async Task NextAsync(SqlDataReader reader, CancellationToken token)
    {
        if (!await reader.NextResultAsync(token))
            throw new SsisDiscoverySourceChangedException();
    }

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}
