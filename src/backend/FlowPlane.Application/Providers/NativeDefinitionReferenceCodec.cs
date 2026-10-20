using FlowPlane.Domain.Providers;

namespace FlowPlane.Application.Providers;

public abstract class NativeDefinitionReferenceCodec
{
    protected static NativeDefinitionReference CreateValidatedReference(
        Guid providerInstanceId,
        ProviderType providerType,
        string schemaId,
        string canonicalJson) =>
        NativeDefinitionReference.CreateValidated(
            providerInstanceId,
            providerType,
            schemaId,
            canonicalJson);
}
