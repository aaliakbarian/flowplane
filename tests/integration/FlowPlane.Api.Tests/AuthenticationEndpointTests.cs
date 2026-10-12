using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace FlowPlane.Api.Tests;

public sealed class AuthenticationEndpointTests : IDisposable
{
    private const string TrustedIssuer = "http://identity.example.test/realms/flowplane";
    private const string TrustedAudience = "flowplane-api";
    private const string SpaAudience = "flowplane-spa";
    private const string UnavailablePostgreSqlConnection =
        "Host=127.0.0.1;Port=1;Database=flowplane;Username=flowplane;Password=test-only;Timeout=1;Command Timeout=1;Pooling=false";

    private readonly RsaSecurityKey trustedKey = CreateSigningKey();
    private readonly RsaSecurityKey untrustedKey = CreateSigningKey();

    [Fact]
    public async Task AnonymousRequestIsRejected()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/me",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ValidAccessTokenReturnsOnlyMinimalIdentity()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateToken(trustedKey, TrustedIssuer, TrustedAudience));

        using var response = await client.GetAsync(
            "/me",
            TestContext.Current.CancellationToken);
        var identity = await response.Content.ReadFromJsonAsync<IdentityResponse>(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new IdentityResponse("test-subject", "FlowPlane Test User"), identity);
    }

    [Theory]
    [InlineData(TokenFailure.InvalidSignature)]
    [InlineData(TokenFailure.WrongIssuer)]
    [InlineData(TokenFailure.WrongAudience)]
    [InlineData(TokenFailure.Expired)]
    [InlineData(TokenFailure.IdTokenAsBearer)]
    [InlineData(TokenFailure.SpaOnlyAccessToken)]
    public async Task InvalidBearerCredentialIsRejected(TokenFailure failure)
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateInvalidToken(failure));

        using var response = await client.GetAsync(
            "/me",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void ProductionEnvironmentRejectsHttpMetadataConfiguration()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:FlowPlane", UnavailablePostgreSqlConnection);
            builder.UseSetting("FlowPlane:Authentication:Issuer", TrustedIssuer);
            builder.UseSetting(
                "FlowPlane:Authentication:MetadataAddress",
                $"{TrustedIssuer}/.well-known/openid-configuration");
            builder.UseSetting("FlowPlane:Authentication:Audience", TrustedAudience);
            builder.UseSetting("FlowPlane:Authentication:RequireHttpsMetadata", "false");
        });

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("RequireHttpsMetadata", exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("Development", exception.ToString(), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        trustedKey.Rsa?.Dispose();
        untrustedKey.Rsa?.Dispose();
    }

    private WebApplicationFactory<Program> CreateFactory()
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:FlowPlane"] = UnavailablePostgreSqlConnection,
                    ["FlowPlane:Authentication:Issuer"] = TrustedIssuer,
                    ["FlowPlane:Authentication:MetadataAddress"] =
                        $"{TrustedIssuer}/.well-known/openid-configuration",
                    ["FlowPlane:Authentication:Audience"] = TrustedAudience,
                    ["FlowPlane:Authentication:RequireHttpsMetadata"] = "true"
                });
            });
            builder.ConfigureServices(services =>
            {
                services.PostConfigure<JwtBearerOptions>(
                    JwtBearerDefaults.AuthenticationScheme,
                    options =>
                    {
                        var configuration = new OpenIdConnectConfiguration
                        {
                            Issuer = TrustedIssuer
                        };
                        configuration.SigningKeys.Add(trustedKey);
                        options.ConfigurationManager =
                            new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                    });
            });
        });
    }

    private string CreateInvalidToken(TokenFailure failure)
    {
        return failure switch
        {
            TokenFailure.InvalidSignature =>
                CreateToken(untrustedKey, TrustedIssuer, TrustedAudience),
            TokenFailure.WrongIssuer =>
                CreateToken(trustedKey, "http://identity.example.test/realms/other", TrustedAudience),
            TokenFailure.WrongAudience =>
                CreateToken(trustedKey, TrustedIssuer, "other-api"),
            TokenFailure.Expired =>
                CreateToken(trustedKey, TrustedIssuer, TrustedAudience, DateTime.UtcNow.AddMinutes(-10)),
            TokenFailure.IdTokenAsBearer =>
                CreateToken(trustedKey, TrustedIssuer, SpaAudience, tokenType: "JWT"),
            TokenFailure.SpaOnlyAccessToken =>
                CreateToken(trustedKey, TrustedIssuer, SpaAudience, tokenType: "at+jwt"),
            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, null)
        };
    }

    private static string CreateToken(
        SecurityKey signingKey,
        string issuer,
        string audience,
        DateTime? expires = null,
        string tokenType = "at+jwt")
    {
        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, "test-subject"),
                new Claim("name", "FlowPlane Test User")
            ]),
            NotBefore = now.AddMinutes(-1),
            Expires = expires ?? now.AddMinutes(5),
            SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256),
            AdditionalHeaderClaims = new Dictionary<string, object>
            {
                ["typ"] = tokenType
            }
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static RsaSecurityKey CreateSigningKey()
    {
        return new RsaSecurityKey(RSA.Create(2048))
        {
            KeyId = Guid.NewGuid().ToString("N")
        };
    }

    public sealed record IdentityResponse(string Subject, string? Name);

    public enum TokenFailure
    {
        InvalidSignature,
        WrongIssuer,
        WrongAudience,
        Expired,
        IdTokenAsBearer,
        SpaOnlyAccessToken
    }
}
