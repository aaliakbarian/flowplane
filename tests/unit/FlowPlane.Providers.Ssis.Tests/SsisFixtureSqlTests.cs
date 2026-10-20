using System.Text.RegularExpressions;

namespace FlowPlane.Providers.Ssis.Tests;

public sealed class SsisFixtureSqlTests
{
    private static readonly Dictionary<string, string[]> Columns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["folders"] = ["folder_id", "name"],
        ["projects"] = ["project_id", "folder_id", "name", "project_format_version"],
        ["packages"] = ["project_id", "name", "entry_point", "package_format_version"],
        ["environments"] = ["environment_id", "folder_id", "name", "description"],
        ["environment_variables"] = ["environment_id", "name", "type", "sensitive", "description"],
        ["environment_references"] = ["project_id", "environment_name", "reference_type", "environment_folder_name"],
        ["object_parameters"] = ["project_id", "object_type", "object_name", "parameter_name", "required", "sensitive", "value_type", "referenced_variable_name"],
    };

    [Theory]
    [InlineData("configure-catalog.sql")]
    [InlineData("verify-catalog.sql")]
    public void Fixture_selects_bind_only_to_approved_public_schema_and_never_value_columns(string file)
    {
        var sql = File.ReadAllText(Path.Combine(FixtureDirectory(), file));
        foreach (var select in Regex.Split(sql, @"\bSELECT\b", RegexOptions.IgnoreCase).Skip(1))
        {
            var sources = Regex.Matches(select,
                @"(?:FROM|JOIN)\s+\[catalog\]\.\[(\w+)\](?:\s+AS\s+\[(\w+)\])?",
                RegexOptions.IgnoreCase);
            var aliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (Match source in sources)
            {
                Assert.True(Columns.TryGetValue(source.Groups[1].Value, out var columns), "Unapproved catalog view.");
                if (source.Groups[2].Success) aliases.Add(source.Groups[2].Value, columns!);
            }
            foreach (Match column in Regex.Matches(select, @"\[(\w+)\]\.\[(\w+)\]"))
            {
                if (column.Groups[1].Value == "catalog") continue;
                Assert.True(aliases.TryGetValue(column.Groups[1].Value, out var columns), "Unbound catalog alias.");
                Assert.Contains(column.Groups[2].Value, columns!, StringComparer.OrdinalIgnoreCase);
            }
            if (sources.Count == 1 && !sources[0].Groups[2].Success)
            {
                foreach (Match column in Regex.Matches(select, @"(?<!\.)\[(\w+)\](?!\s*\.)"))
                {
                    if (column.Groups[1].Value == sources[0].Groups[1].Value) continue;
                    Assert.Contains(column.Groups[1].Value, Columns[sources[0].Groups[1].Value],
                        StringComparer.OrdinalIgnoreCase);
                }
            }
        }
    }

    internal static string FixtureDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FlowPlane.slnx")))
            directory = directory.Parent;
        return Path.Combine(directory!.FullName, "tests/fixtures/ssis/FlowPlaneA11Fixture");
    }

    [Fact]
    public void Fixture_mutations_use_only_documented_procedures_and_named_parameters()
    {
        var parameters = new Dictionary<string, string[]>
        {
            ["create_environment"] = ["folder_name", "environment_name", "environment_description"],
            ["create_environment_variable"] = ["folder_name", "environment_name", "variable_name", "data_type", "sensitive", "value", "description"],
            ["set_environment_variable_value"] = ["folder_name", "environment_name", "variable_name", "value"],
            ["create_environment_reference"] = ["folder_name", "project_name", "environment_name", "reference_type", "reference_id"],
            ["set_object_parameter_value"] = ["object_type", "folder_name", "project_name", "parameter_name", "parameter_value", "value_type"],
        };
        var sql = File.ReadAllText(Path.Combine(FixtureDirectory(), "configure-catalog.sql"));
        var calls = Regex.Matches(sql, @"EXEC\s+\[catalog\]\.\[(\w+)\]([^;]+);", RegexOptions.IgnoreCase);
        Assert.Equal(6, calls.Count);
        foreach (Match call in calls)
        {
            Assert.True(parameters.TryGetValue(call.Groups[1].Value, out var allowed), "Unapproved catalog mutation.");
            var arguments = Regex.Matches(call.Groups[2].Value, @"@(\w+)\s*=")
                .Select(match => match.Groups[1].Value).ToArray();
            Assert.Equal(allowed!.Order(StringComparer.Ordinal), arguments.Order(StringComparer.Ordinal));
        }
    }
}
