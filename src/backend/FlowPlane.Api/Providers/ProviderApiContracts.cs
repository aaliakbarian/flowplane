using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowPlane.Api.Providers;

internal class ProviderConfigurationInput
{
    public required string ProviderType { get; init; }
    public required string DisplayName { get; init; }
    public required bool IsEnabled { get; init; }
    public required ConfigurationInput Configuration { get; init; }
    public override string ToString() => "Provider configuration input (redacted)";
}

internal sealed class CreateProviderInput : ProviderConfigurationInput
{
    public required CredentialInput Credential { get; init; }
}

internal sealed class CredentialInput
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public required string Password { get; init; }
    public override string ToString() => "Credential input (redacted)";
}

internal sealed class ConfigurationInput
{
    public required string SchemaId { get; init; }
    public required JsonElement Data { get; init; }
    public override string ToString() => "Configuration input (redacted)";
}

internal sealed record ProviderResponse(Guid Id, string ProviderType, string DisplayName, bool IsEnabled,
    ConfigurationResponse Configuration, long Version, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

internal sealed record ConfigurationResponse(string SchemaId, JsonElement Data);
