using FlowPlane.Domain.Providers;

namespace FlowPlane.Application.Providers;

/// <summary>Provider-owned identity and approved metadata, without a universal object hierarchy.</summary>
public sealed record ProviderDiscoveryObservation(
    Guid ProviderInstanceId,
    string NativeKey,
    ProviderDiscoveryMetadataDocument Metadata,
    NativeDefinitionReference? ExecutableCandidate)
{
    public override string ToString() => $"ProviderDiscoveryObservation({Metadata.SchemaId})";
}
