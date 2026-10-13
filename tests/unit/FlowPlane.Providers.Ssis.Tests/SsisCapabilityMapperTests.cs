using FlowPlane.Application.Providers;
using FlowPlane.Providers.Ssis.Validation;

namespace FlowPlane.Providers.Ssis.Tests;

public sealed class SsisCapabilityMapperTests
{
    [Fact]
    public void Zero_visible_permissions_maps_capabilities_to_unknown_not_unavailable()
    {
        var capabilities = SsisCapabilityMapper.Map(new SsisPermissionEvidence(
            HasReadableScope: false,
            HasExecutableReadableScope: false,
            HasVisibleDefinitions: false,
            IsReadExplicitlyDenied: false,
            HasUnknownDefinitionFormat: false));

        Assert.All(capabilities, capability =>
            Assert.Equal(ProviderCapabilityAvailability.Unknown, capability.Availability));
    }

    [Fact]
    public void Positive_read_and_execute_evidence_makes_both_capabilities_available()
    {
        var capabilities = SsisCapabilityMapper.Map(new SsisPermissionEvidence(
            HasReadableScope: true,
            HasExecutableReadableScope: true,
            HasVisibleDefinitions: true,
            IsReadExplicitlyDenied: false,
            HasUnknownDefinitionFormat: false));

        Assert.Equal(ProviderCapabilityAvailability.Available, Find(capabilities, "discovery").Availability);
        Assert.Equal(ProviderCapabilityAvailability.Available, Find(capabilities, "execution").Availability);
    }

    [Fact]
    public void Readable_definitions_without_execute_permission_make_execution_unavailable()
    {
        var capabilities = SsisCapabilityMapper.Map(new SsisPermissionEvidence(
            HasReadableScope: true,
            HasExecutableReadableScope: false,
            HasVisibleDefinitions: true,
            IsReadExplicitlyDenied: false,
            HasUnknownDefinitionFormat: false));

        Assert.Equal(ProviderCapabilityAvailability.Available, Find(capabilities, "discovery").Availability);
        Assert.Equal(ProviderCapabilityAvailability.Unavailable, Find(capabilities, "execution").Availability);
    }

    [Fact]
    public void Empty_but_readable_scope_keeps_execution_unknown()
    {
        var capabilities = SsisCapabilityMapper.Map(new SsisPermissionEvidence(
            HasReadableScope: true,
            HasExecutableReadableScope: false,
            HasVisibleDefinitions: false,
            IsReadExplicitlyDenied: false,
            HasUnknownDefinitionFormat: false));

        Assert.Equal(ProviderCapabilityAvailability.Available, Find(capabilities, "discovery").Availability);
        Assert.Equal(ProviderCapabilityAvailability.Unknown, Find(capabilities, "execution").Availability);
    }

    [Fact]
    public void Unknown_visible_definition_format_makes_capabilities_unknown()
    {
        var capabilities = SsisCapabilityMapper.Map(new SsisPermissionEvidence(
            HasReadableScope: true,
            HasExecutableReadableScope: true,
            HasVisibleDefinitions: true,
            IsReadExplicitlyDenied: false,
            HasUnknownDefinitionFormat: true));

        Assert.All(capabilities, capability =>
            Assert.Equal(ProviderCapabilityAvailability.Unknown, capability.Availability));
    }

    private static ProviderCapabilityResult Find(
        IReadOnlyList<ProviderCapabilityResult> capabilities,
        string code) => Assert.Single(capabilities, capability => capability.Code == code);
}
