namespace FlowPlane.Providers.Ssis.Configuration;

using System.Text.Json.Serialization;

public sealed record SsisProviderConfiguration(
    string Host,
    int Port,
    string Username,
    bool TrustServerCertificate)
{
    public const string DefaultCatalog = "SSISDB";

    [JsonIgnore]
    public string Catalog => DefaultCatalog;
}
