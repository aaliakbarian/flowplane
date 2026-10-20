using System.Data;

using FlowPlane.Providers.Ssis.Discovery;

using Microsoft.Data.SqlClient;

namespace FlowPlane.Providers.Ssis.Tests;

public sealed class SsisSqlDiscoveryQueryTests
{
    [Fact]
    public void Project_batches_use_only_parameterized_project_ids_and_no_total_metadata_limit()
    {
        using var connection = new SqlConnection();
        using var command = SsisSqlDiscoveryQuery.CreateProjectBatchCommand(connection,
            [DiscoveryTestData.Project(1), DiscoveryTestData.Project(2)], SsisDiscoveryOptions.Default);

        Assert.Equal(15, command.CommandTimeout);
        Assert.Equal(1L, command.Parameters["@project0"].Value);
        Assert.Equal(2L, command.Parameters["@project1"].Value);
        Assert.Equal(2, command.Parameters.Count);
        Assert.DoesNotContain("ROW_NUMBER", command.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TOP", command.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OFFSET", command.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[entry_point] = 1", command.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("@project_format", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("IN (@project0, @project1)", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("[catalog].[object_parameters]", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("[catalog].[environment_references]", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("[parameter].[object_name]", command.CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_includes_empty_folders_and_all_visible_projects_without_package_joins()
    {
        var sql = SsisSqlDiscoveryQuery.ManifestSql;
        Assert.Contains("FULL OUTER JOIN [catalog].[projects]", sql, StringComparison.Ordinal);
        Assert.Contains("[folder].[folder_id]", sql, StringComparison.Ordinal);
        Assert.Contains("[project].[object_version_lsn]", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("[catalog].[packages]", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("TOP", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Queries_select_only_approved_columns_and_never_native_values_or_history()
    {
        using var connection = new SqlConnection();
        using var command = SsisSqlDiscoveryQuery.CreateProjectBatchCommand(connection,
            [DiscoveryTestData.Project()], SsisDiscoveryOptions.Default);
        var sql = command.CommandText + SsisSqlDiscoveryQuery.ManifestSql +
            SsisSqlDiscoveryQuery.CatalogSurfaceProbeSql;
        foreach (var forbidden in new[] { "SELECT *", "default_value", "design_default_value",
            "[catalog].[environment_variables]", "[catalog].[execution_parameter_values]",
            "[catalog].[object_versions]", "NOLOCK", "ISOLATION LEVEL", "deploy_project" })
            Assert.DoesNotContain(forbidden, sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[parameter].[required]", sql, StringComparison.Ordinal);
        Assert.Contains("[parameter].[sensitive]", sql, StringComparison.Ordinal);
        Assert.Contains("[parameter].[referenced_variable_name]", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Unsafe_or_empty_project_batches_are_rejected()
    {
        using var connection = new SqlConnection();
        Assert.Throws<ArgumentException>(() => SsisSqlDiscoveryQuery.CreateProjectBatchCommand(
            connection, [], SsisDiscoveryOptions.Default));
        Assert.Throws<ArgumentException>(() => SsisSqlDiscoveryQuery.CreateProjectBatchCommand(
            connection, [DiscoveryTestData.Project(), DiscoveryTestData.Project(2)],
            new SsisDiscoveryOptions { MaxProjectsPerBatch = 1 }));
    }

    [Fact]
    public void Revision_comparison_detects_changed_revision_path_or_missing_project()
    {
        var expected = DiscoveryTestData.Project();
        SsisSqlDiscoverySession.VerifyRevision(expected, expected);
        Assert.Throws<SsisDiscoverySourceChangedException>(() =>
            SsisSqlDiscoverySession.VerifyRevision(expected, expected with { ObjectVersionLsn = 7 }));
        Assert.Throws<SsisDiscoverySourceChangedException>(() =>
            SsisSqlDiscoverySession.VerifyRevision(expected, expected with { ProjectName = "Other" }));
        Assert.Throws<SsisDiscoverySourceChangedException>(() =>
            SsisSqlDiscoverySession.VerifyRevision(expected, expected with { ProjectId = null }));
    }

    [Fact]
    public void Discovery_surface_probe_allowlists_SQL_Server_2025_columns()
    {
        var sql = SsisSqlDiscoveryQuery.CatalogSurfaceProbeSql;
        Assert.Contains("[folder_id], [name]", sql, StringComparison.Ordinal);
        Assert.Contains("[project_id], [folder_id], [name], [project_format_version]", sql,
            StringComparison.Ordinal);
        Assert.Contains("[parameter_id], [project_id], [object_type], [object_name], [parameter_name], [data_type]",
            sql, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT *", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_revision_row_is_detected_even_without_a_returned_project_record()
    {
        var expected = DiscoveryTestData.Project();
        using var rows = new DataTable();
        using var reader = rows.CreateDataReader();

        await Assert.ThrowsAsync<SsisDiscoverySourceChangedException>(() =>
            SsisSqlDiscoverySession.VerifyRevisionsAsync(reader,
                new Dictionary<long, SsisProjectManifestEntry> { [expected.ProjectId!.Value] = expected },
                CancellationToken.None));
    }
}
