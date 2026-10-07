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
