using FlowPlane.Domain.Providers;

namespace FlowPlane.Application.Providers;

public interface IProviderConfigurationCodec<TConfiguration>
{
    ProviderType ProviderType { get; }

    string SchemaId { get; }

    ProviderConfigurationDocument DecodeInput(string schemaId, string json);

    ProviderConfigurationDocument Encode(TConfiguration configuration);

    TConfiguration Decode(ProviderConfigurationDocument document);
}
