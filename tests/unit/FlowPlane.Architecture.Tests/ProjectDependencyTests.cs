using System.Xml.Linq;

namespace FlowPlane.Architecture.Tests;

public sealed class ProjectDependencyTests
{
    public static TheoryData<string, string[]> ApprovedDependencies => new()
    {
        { "src/backend/FlowPlane.Domain/FlowPlane.Domain.csproj", [] },
        {
            "src/backend/FlowPlane.Application/FlowPlane.Application.csproj",
            ["FlowPlane.Domain"]
        },
        {
            "src/backend/FlowPlane.Infrastructure/FlowPlane.Infrastructure.csproj",
            ["FlowPlane.Application", "FlowPlane.Domain"]
        },
        {
            "src/backend/FlowPlane.Providers.Ssis/FlowPlane.Providers.Ssis.csproj",
            ["FlowPlane.Application", "FlowPlane.Domain"]
        },
        {
            "src/backend/FlowPlane.Api/FlowPlane.Api.csproj",
            [
                "FlowPlane.Application",
                "FlowPlane.Domain",
                "FlowPlane.Infrastructure",
                "FlowPlane.Providers.Ssis",
            ]
        },
    };

    [Theory]
    [MemberData(nameof(ApprovedDependencies))]
    public void Projects_reference_only_approved_inward_dependencies(
        string projectPath,
        string[] approvedDependencies)
    {
        var absoluteProjectPath = Path.Combine(FindRepositoryRoot(), projectPath);
        var projectDirectory = Path.GetDirectoryName(absoluteProjectPath)!;
        var referencedFlowPlaneProjects = XDocument
            .Load(absoluteProjectPath)
            .Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")?.Value)
            .Where(include => include is not null)
            .Select(include => Path.GetFullPath(include!, projectDirectory))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name is not null && name.StartsWith("FlowPlane.", StringComparison.Ordinal))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

        var unexpectedDependencies = referencedFlowPlaneProjects
            .Except(approvedDependencies, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(unexpectedDependencies);
    }

    [Theory]
    [InlineData("src/backend/FlowPlane.Domain/FlowPlane.Domain.csproj")]
    [InlineData("src/backend/FlowPlane.Application/FlowPlane.Application.csproj")]
    public void Inner_projects_do_not_reference_outer_persistence_or_protection_packages(
        string projectPath)
    {
        var packageReferences = ReadPackageReferences(projectPath);

        Assert.DoesNotContain("Microsoft.EntityFrameworkCore", packageReferences);
        Assert.DoesNotContain("Npgsql.EntityFrameworkCore.PostgreSQL", packageReferences);
        Assert.DoesNotContain(
            "Microsoft.AspNetCore.DataProtection.EntityFrameworkCore",
            packageReferences);
        Assert.DoesNotContain("Microsoft.Data.SqlClient", packageReferences);
    }

    [Fact]
    public void SqlClient_is_referenced_only_by_the_SSIS_provider()
    {
        var repositoryRoot = FindRepositoryRoot();
        var projectsReferencingSqlClient = Directory
            .EnumerateFiles(
                Path.Combine(repositoryRoot, "src", "backend"),
                "*.csproj",
                SearchOption.AllDirectories)
            .Where(projectPath => ReadPackageReferencesFromAbsolutePath(projectPath)
                .Contains("Microsoft.Data.SqlClient", StringComparer.Ordinal))
            .Select(Path.GetFileNameWithoutExtension)
            .ToArray();

        Assert.Equal(["FlowPlane.Providers.Ssis"], projectsReferencingSqlClient);
    }

    [Fact]
    public void Provider_independent_source_contains_no_SSIS_configuration_vocabulary()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceFiles = new[]
        {
            Path.Combine(repositoryRoot, "src", "backend", "FlowPlane.Domain"),
            Path.Combine(repositoryRoot, "src", "backend", "FlowPlane.Application"),
        }.SelectMany(directory => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories));

        var offendingFiles = sourceFiles
            .Where(path => File.ReadAllText(path).Contains("Ssis", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .ToArray();

        Assert.Empty(offendingFiles);
    }

    private static string[] ReadPackageReferences(string projectPath) =>
        ReadPackageReferencesFromAbsolutePath(Path.Combine(FindRepositoryRoot(), projectPath));

    private static string[] ReadPackageReferencesFromAbsolutePath(string projectPath) => XDocument
        .Load(projectPath)
        .Descendants("PackageReference")
        .Select(reference => reference.Attribute("Include")?.Value)
        .Where(include => include is not null)
        .Cast<string>()
        .ToArray();

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FlowPlane.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the FlowPlane repository root.");
    }
}
