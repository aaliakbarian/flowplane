using FlowPlane.Domain.Providers;

namespace FlowPlane.Application.Providers;

public abstract class ProviderConfigurationCodec
{
    protected static ProviderConfigurationDocument CreateValidatedDocument(
        string schemaId,
        string canonicalJson) =>
        ProviderConfigurationDocument.CreateValidated(schemaId, canonicalJson);
}
