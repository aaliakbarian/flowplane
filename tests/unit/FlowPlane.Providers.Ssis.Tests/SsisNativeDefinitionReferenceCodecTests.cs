using FlowPlane.Providers.Ssis.Discovery;

namespace FlowPlane.Providers.Ssis.Tests;

public sealed class SsisNativeDefinitionReferenceCodecTests
{
    private static readonly Guid FirstInstance =
        Guid.Parse("018f8f55-82e7-734b-ab9a-a2d355bc9f6d");

    [Fact]
    public void Reference_round_trips_exact_unicode_native_names()
    {
        var codec = new SsisNativeDefinitionReferenceCodec();
        var identity = new SsisNativeDefinitionIdentity(
            "پوشه داده",
            "Δοκιμή Project",
            "Carga_日本語.dtsx");

        var reference = codec.Encode(FirstInstance, identity);
        var decoded = codec.Decode(reference);

        Assert.Equal(identity, decoded);
        Assert.Equal("ssis", reference.ProviderType.Value);
        Assert.Equal(FirstInstance, reference.ProviderInstanceId);
        Assert.Equal("flowplane.ssis/native-definition-reference.v1", reference.SchemaId);
    }

    [Fact]
    public void Provider_instance_and_full_ordinal_path_define_reference_identity()
    {
        var codec = new SsisNativeDefinitionReferenceCodec();
        var baseline = codec.Encode(
            FirstInstance,
            new("Folder", "Project", "Package.dtsx"));

        Assert.NotEqual(
            baseline,
            codec.Encode(FirstInstance, new("Folder", "OtherProject", "Package.dtsx")));
        Assert.NotEqual(
            baseline,
            codec.Encode(FirstInstance, new("OtherFolder", "Project", "Package.dtsx")));
        Assert.NotEqual(
            baseline,
            codec.Encode(
                Guid.Parse("018f8f55-82e7-734b-ab9a-a2d355bc9f6e"),
                new("Folder", "Project", "Package.dtsx")));
        Assert.NotEqual(
            baseline,
            codec.Encode(FirstInstance, new("folder", "Project", "Package.dtsx")));
    }

    [Fact]
    public void Redeployment_observations_do_not_change_reference_but_path_changes_do()
    {
        var codec = new SsisNativeDefinitionReferenceCodec();
        var identity = new SsisNativeDefinitionIdentity("Folder", "Project", "Package.dtsx");

        var beforeRedeployment = codec.Encode(FirstInstance, identity);
        var afterRedeployment = codec.Encode(FirstInstance, identity);
        var renamed = codec.Encode(FirstInstance, identity with { PackageName = "Renamed.dtsx" });

        Assert.Equal(beforeRedeployment, afterRedeployment);
        Assert.NotEqual(beforeRedeployment, renamed);
    }
}
