using System.Text.Json;
using System.Text.Json.Serialization;

using FlowPlane.Application.Providers;
using FlowPlane.Domain.Providers;
using FlowPlane.Providers.Ssis.Configuration;

namespace FlowPlane.Providers.Ssis.Discovery;

internal sealed record SsisNativeDefinitionIdentity(
    string FolderName,
    string ProjectName,
    string PackageName);

internal sealed class SsisNativeDefinitionReferenceCodec : NativeDefinitionReferenceCodec
{
    internal const string SchemaId = "flowplane.ssis/native-definition-reference.v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public NativeDefinitionReference Encode(
        Guid providerInstanceId,
        SsisNativeDefinitionIdentity identity)
    {
        Validate(identity);
        return CreateValidatedReference(
            providerInstanceId,
            SsisProviderConfigurationCodec.SsisProviderType,
            SchemaId,
            JsonSerializer.Serialize(identity, JsonOptions));
    }

    public SsisNativeDefinitionIdentity Decode(NativeDefinitionReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.ProviderType != SsisProviderConfigurationCodec.SsisProviderType ||
            !string.Equals(reference.SchemaId, SchemaId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The native definition reference is not an SSIS reference.", nameof(reference));
        }

        var identity = JsonSerializer.Deserialize<SsisNativeDefinitionIdentity>(
            reference.CanonicalJson,
            JsonOptions) ?? throw new ArgumentException(
                "The SSIS native definition reference is invalid.",
                nameof(reference));
        Validate(identity);
        return identity;
    }

    private static void Validate(SsisNativeDefinitionIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ValidateName(identity.FolderName, 128, nameof(identity.FolderName));
        ValidateName(identity.ProjectName, 128, nameof(identity.ProjectName));
        ValidateName(identity.PackageName, 256, nameof(identity.PackageName));
    }

    private static void ValidateName(string value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maximumLength)
        {
            throw new ArgumentException("The SSIS native name is invalid.", parameterName);
        }
    }
}
