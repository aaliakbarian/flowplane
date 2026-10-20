using System.Text.Json;

using FlowPlane.Providers.Ssis.Discovery;

namespace FlowPlane.Providers.Ssis.Tests;

public sealed class SsisDiscoveryMapperTests
{
    [Theory]
    [InlineData(true, 1, 8, true, 0)]
    [InlineData(false, 1, 8, false, 0)]
    [InlineData(true, 99, 8, false, 1)]
    [InlineData(true, 1, 99, false, 2)]
    [InlineData(true, 8, 1, false, 3)]
    public void Every_package_remains_observed_with_independent_eligibility(
        bool entryPoint, int projectFormat, int packageFormat, bool eligible,
        int compatibility)
    {
        var project = DiscoveryTestData.Project(format: projectFormat);
        var observation = SsisDiscoveryMapper.Map(
            DiscoveryTestData.InstanceId,
            DiscoveryTestData.Package(project) with
            {
                IsEntryPoint = entryPoint,
                PackageFormatVersion = packageFormat,
            });

        var metadata = Assert.IsType<SsisPackageMetadata>(
            new SsisDiscoveryMetadataCodec().Decode(observation.Metadata));
        Assert.Equal((SsisDefinitionCompatibility)compatibility, metadata.Compatibility);
        Assert.Equal(entryPoint, metadata.IsEntryPoint);
        Assert.Equal(eligible, observation.ExecutableCandidate is not null);
        Assert.Equal(DiscoveryTestData.InstanceId, observation.ProviderInstanceId);
        if (eligible)
        {
            var identity = new SsisNativeDefinitionReferenceCodec().Decode(
                observation.ExecutableCandidate!);
            Assert.Equal(new SsisNativeDefinitionIdentity("Folder", "Project", "EntryPoint.dtsx"),
                identity);
        }
    }

    [Fact]
    public void Exact_names_qualify_project_package_parameter_and_environment_keys()
    {
        var project = DiscoveryTestData.Project() with
        {
            FolderName = "資料 Å",
            ProjectName = "Project ß",
        };
        var projectParameter = new SsisDiscoveredParameter(project, SsisParameterScope.Project,
            null, "Shared", "String", true, true, SsisParameterValueSource.EnvironmentReference,
            true, "SecretVariable");
        var packageParameter = projectParameter with
        {
            Scope = SsisParameterScope.Package,
            PackageName = "EntryPoint.dtsx",
        };
        var first = SsisDiscoveryMapper.Map(DiscoveryTestData.InstanceId, projectParameter);
        var second = SsisDiscoveryMapper.Map(DiscoveryTestData.InstanceId, packageParameter);
        var other = SsisDiscoveryMapper.Map(DiscoveryTestData.InstanceId,
            packageParameter with { PackageName = "Other.dtsx" });

        Assert.NotEqual(first.NativeKey, second.NativeKey);
        Assert.NotEqual(second.NativeKey, other.NativeKey);
        var metadata = Assert.IsType<SsisParameterMetadata>(
            new SsisDiscoveryMetadataCodec().Decode(first.Metadata));
        Assert.Equal("資料 Å", metadata.FolderName);
        Assert.Equal(SsisParameterScope.Project, metadata.Scope);
        Assert.Null(metadata.PackageName);
        Assert.True(metadata.IsSensitive);
        Assert.Equal("SecretVariable", metadata.ReferencedVariableName);
        Assert.DoesNotContain("Shared", first.ToString(), StringComparison.Ordinal);
        Assert.Null(first.ExecutableCandidate);
    }

    [Fact]
    public void Environment_references_are_project_scoped_and_contain_no_values()
    {
        var project = DiscoveryTestData.Project();
        var row = new SsisDiscoveredEnvironmentReference(project, 42,
            SsisEnvironmentReferenceType.Relative, null, "LocalEnvironment");
        var first = SsisDiscoveryMapper.Map(DiscoveryTestData.InstanceId, row);
        var second = SsisDiscoveryMapper.Map(DiscoveryTestData.InstanceId, row with
        {
            Project = project with { ProjectId = 2, ProjectName = "Other" },
        });
        var metadata = Assert.IsType<SsisEnvironmentReferenceMetadata>(
            new SsisDiscoveryMetadataCodec().Decode(first.Metadata));

        Assert.NotEqual(first.NativeKey, second.NativeKey);
        Assert.Equal(SsisEnvironmentReferenceType.Relative, metadata.ReferenceType);
        Assert.Null(metadata.EnvironmentFolderName);
        using var json = JsonDocument.Parse(first.Metadata.CanonicalJson);
        Assert.False(json.RootElement.TryGetProperty("value", out _));
        Assert.False(json.RootElement.TryGetProperty("defaultValue", out _));
    }

    [Fact]
    public void Redeployment_changes_current_revision_observations_not_stable_reference()
    {
        var project = DiscoveryTestData.Project();
        var package = DiscoveryTestData.Package(project);
        var first = SsisDiscoveryMapper.Map(DiscoveryTestData.InstanceId, package);
        var second = SsisDiscoveryMapper.Map(DiscoveryTestData.InstanceId, package with
        {
            Project = project with { ObjectVersionLsn = 7 },
            PackageId = 100,
            PackageVersionGuid = Guid.NewGuid(),
        });

        Assert.Equal(first.NativeKey, second.NativeKey);
        Assert.Equal(first.ExecutableCandidate, second.ExecutableCandidate);
        Assert.NotEqual(first.Metadata.CanonicalJson, second.Metadata.CanonicalJson);
    }
}
