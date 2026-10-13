using FlowPlane.Providers.Ssis.Configuration;

namespace FlowPlane.ProviderConfiguration.Tests;

public sealed class SsisProviderConfigurationCodecTests
{
    [Fact]
    public void Codec_round_trips_valid_nonsecret_configuration_with_safe_defaults()
    {
        var codec = new SsisProviderConfigurationCodec();

        var document = codec.DecodeInput(
            SsisProviderConfigurationCodec.SchemaId,
            """
            {
              "host": "sql-test.example.internal",
              "username": "flowplane_test"
            }
            """);
        var configuration = codec.Decode(document);

        Assert.Equal("sql-test.example.internal", configuration.Host);
        Assert.Equal(1433, configuration.Port);
        Assert.Equal("flowplane_test", configuration.Username);
        Assert.False(configuration.TrustServerCertificate);
        Assert.Equal("SSISDB", configuration.Catalog);
    }

    [Theory]
    [InlineData("{\"host\":\"sql\",\"username\":\"user\",\"password\":\"secret\"}")]
    [InlineData("{\"host\":\"sql\",\"username\":\"user\",\"connectionString\":\"Server=sql\"}")]
    [InlineData("{\"host\":\"sql\",\"username\":\"user\",\"unexpected\":true}")]
    [InlineData("{\"host\":\"\",\"username\":\"user\"}")]
    [InlineData("{\"host\":\"sql;Password=x\",\"username\":\"user\"}")]
    [InlineData("{\"host\":\"sql\",\"port\":0,\"username\":\"user\"}")]
    [InlineData("{\"host\":\"sql\",\"port\":65536,\"username\":\"user\"}")]
    [InlineData("{\"host\":\"sql\",\"username\":\"\"}")]
    public void Codec_rejects_unknown_secret_like_or_invalid_fields(string json)
    {
        var codec = new SsisProviderConfigurationCodec();

        Assert.Throws<ProviderConfigurationException>(() =>
            codec.DecodeInput(SsisProviderConfigurationCodec.SchemaId, json));
    }

    [Fact]
    public void Codec_rejects_wrong_schema()
    {
        var codec = new SsisProviderConfigurationCodec();

        Assert.Throws<ProviderConfigurationException>(() =>
            codec.DecodeInput("ssis.provider-instance/v2", "{\"host\":\"sql\",\"username\":\"user\"}"));
    }

    [Fact]
    public void Rejected_secret_like_input_does_not_echo_its_value_in_the_exception()
    {
        var codec = new SsisProviderConfigurationCodec();
        const string canary = "a09-codec-exception-secret-canary";

        var exception = Assert.Throws<ProviderConfigurationException>(() =>
            codec.DecodeInput(
                SsisProviderConfigurationCodec.SchemaId,
                $$"""{"host":"sql","username":"user","password":"{{canary}}"}"""));

        Assert.DoesNotContain(canary, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Encoded_document_never_contains_password_or_connection_string_fields()
    {
        var codec = new SsisProviderConfigurationCodec();

        var document = codec.Encode(new SsisProviderConfiguration(
            "sql-prod",
            1433,
            "flowplane_prod",
            TrustServerCertificate: false));

        Assert.DoesNotContain("password", document.CanonicalJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connectionString", document.CanonicalJson, StringComparison.OrdinalIgnoreCase);
    }
}
