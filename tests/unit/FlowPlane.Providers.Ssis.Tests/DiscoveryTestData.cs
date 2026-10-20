using FlowPlane.Providers.Ssis.Discovery;

namespace FlowPlane.Providers.Ssis.Tests;

internal static class DiscoveryTestData
{
    public static readonly Guid InstanceId = Guid.Parse("018f8f55-82e7-734b-ab9a-a2d355bc9f6d");

    public static SsisProjectManifestEntry Project(long id = 1, int format = 1) =>
        new(1, "Folder", id, id == 1 ? "Project" : $"Project{id}", format, 1234,
            DateTimeOffset.Parse("2026-10-18T00:00:00Z"));

    public static SsisDiscoveredPackage Package(SsisProjectManifestEntry project, long id = 1) =>
        new(project, id, id == 1 ? "EntryPoint.dtsx" : $"Package{id}.dtsx", true, 8,
            Guid.Parse("018f8f55-82e7-734b-ab9a-a2d355bc9f70"), 1, 0, 0,
            Guid.Parse("018f8f55-82e7-734b-ab9a-a2d355bc9f71"));
}
