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
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace FlowPlane.Api.Tests;

public sealed class ProviderApiTests
{
    [Fact]
    public async Task Production_rejects_certificate_bypass_during_configuration_before_any_database_write()
    {
        using var api = new ProviderApiFactory(environment: "Production", settings: new Dictionary<string, string?>
        {
            ["FlowPlane:Authentication:Issuer"] = ProviderApiFactory.Issuer,
            ["FlowPlane:Authentication:MetadataAddress"] = ProviderApiFactory.Issuer + "/.well-known/openid-configuration",
            ["FlowPlane:Authentication:Audience"] = "flowplane-api",
            ["FlowPlane:Authentication:RequireHttpsMetadata"] = "true",
        });
        using var client = api.Client("operator");
        using var response = await client.PostAsJsonAsync("/provider-instances", new
        {
            providerType = "ssis",
            displayName = "Synthetic",
            isEnabled = true,
            configuration = new { schemaId = "ssis.provider-instance/v1", data = new { host = "sql.example.test", username = "user", trustServerCertificate = true } },
            credential = new { password = "synthetic-not-persisted" },
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("ProviderTlsPolicyDenied", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=101")]
    [InlineData("offset=-1")]
    public async Task Invalid_list_bounds_are_rejected_before_querying_storage(string query)
    {
        using var api = new ProviderApiFactory();
        using var client = api.Client("operator");
        using var response = await client.GetAsync("/provider-instances?" + query, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Oversized_authenticated_input_is_rejected_without_echoing_the_body()
    {
        using var api = new ProviderApiFactory();
        using var client = api.Client("operator");
        using var body = new StringContent("{\"credential\":{\"password\":\"" + new string('x', 33000) + "\"}}", System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/provider-instances", body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.True((await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Length < 1000);
    }

    [Theory]
    [InlineData("https://IDENTITY.example.test/realms/flowplane", "operator")]
    [InlineData("https://identity.example.test/realms/flowplane/", "operator")]
    [InlineData(ProviderApiFactory.Issuer, "Operator")]
    public async Task Operator_pair_matching_is_exact(string issuer, string subject)
    {
        using var api = new ProviderApiFactory(settings: new Dictionary<string, string?>
        {
            ["FlowPlane:Operators:Allowed:0:Issuer"] = issuer,
            ["FlowPlane:Operators:Allowed:0:Subject"] = subject,
        });
        using var client = api.Client("operator");
        using var response = await client.GetAsync("/provider-instances", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("FlowPlane:Operators:Allowed:0:Issuer", "not-a-uri")]
    [InlineData("FlowPlane:Operators:Allowed:0:Subject", " ")]
    [InlineData("FlowPlane:Ssis:AllowedEndpoints:0:Host", "*.example.test")]
    [InlineData("FlowPlane:Ssis:AllowedEndpoints:0:Port", "0")]
    [InlineData("FlowPlane:Ssis:AllowedEndpoints:0:Port", "65536")]
    [InlineData("FlowPlane:Ssis:AllowedEndpoints:0:Port", "not-a-port")]
    public void Invalid_operator_or_endpoint_configuration_fails_closed(string key, string value)
    {
        using var api = new ProviderApiFactory(settings: new Dictionary<string, string?> { [key] = value });
        Assert.ThrowsAny<InvalidOperationException>(() => api.CreateClient());
    }

    [Theory]
    [InlineData("W/\"1\"")]
    [InlineData("*")]
    [InlineData("\"1\", \"2\"")]
    [InlineData("\"01\"")]
    [InlineData("\"0\"")]
    [InlineData("\"9223372036854775808\"")]
    public async Task Unsafe_mutation_preconditions_are_rejected(string header)
    {
        using var api = new ProviderApiFactory();
        using var client = api.Client("operator");
        using var request = new HttpRequestMessage(HttpMethod.Put, "/provider-instances/00000000-0000-0000-0000-000000000001/credential")
        { Content = JsonContent.Create(new { }) };
        request.Headers.TryAddWithoutValidation("If-Match", header);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("{\"secretReference\":\"env:FORBIDDEN\"}")]
    [InlineData("{\"password\":\"x\",\"password\":\"y\"}")]
    [InlineData("{\"Password\":\"x\"}")]
    [InlineData("{\"password\":null}")]
    public async Task Credential_input_is_strict_and_cannot_select_locators(string body)
    {
        using var api = new ProviderApiFactory();
        using var client = api.Client("operator");
        using var request = new HttpRequestMessage(HttpMethod.Put, "/provider-instances/00000000-0000-0000-0000-000000000001/credential")
        { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void Credential_input_cannot_be_serialized_or_formatted_with_its_value()
    {
        var type = typeof(Program).Assembly.GetType("FlowPlane.Api.Providers.CredentialInput")!;
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var input = System.Text.Json.JsonSerializer.Deserialize("{\"password\":\"synthetic-never-returned\"}", type, options)!;
        Assert.False(System.Text.Json.JsonSerializer.Serialize(input, type, options).Contains("synthetic-never-returned", StringComparison.Ordinal));
        Assert.False(input.ToString()!.Contains("synthetic-never-returned", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("GET", "/provider-instances")]
    [InlineData("POST", "/provider-instances")]
    [InlineData("GET", "/provider-instances/00000000-0000-0000-0000-000000000001")]
    [InlineData("PUT", "/provider-instances/00000000-0000-0000-0000-000000000001")]
    [InlineData("PUT", "/provider-instances/00000000-0000-0000-0000-000000000001/credential")]
    [InlineData("POST", "/provider-instances/00000000-0000-0000-0000-000000000001/validation")]
    public async Task Every_route_requires_authentication_and_operator_authorization(string method, string path)
    {
        using var api = new ProviderApiFactory();
        foreach (var subject in new string?[] { null, "not-an-operator" })
        {
            using var client = api.Client(subject);
            using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { }) };
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(subject is null ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("other-subject")]
    public async Task Missing_or_nonmatching_operator_configuration_denies_access(string? configuredSubject)
    {
        using var api = new ProviderApiFactory(operatorSubject: configuredSubject);
        using var client = api.Client("operator");
        using var response = await client.GetAsync("/provider-instances", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Nonoperator_still_has_access_to_me()
    {
        using var api = new ProviderApiFactory();
        using var client = api.Client("not-an-operator");
        using var response = await client.GetAsync("/me", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("credential")]
    [InlineData("")]
    public async Task Missing_mutation_precondition_is_428_before_database_access(string suffix)
    {
        using var api = new ProviderApiFactory();
        using var client = api.Client("operator");
        using var response = await client.PutAsJsonAsync("/provider-instances/00000000-0000-0000-0000-000000000001" +
            (suffix.Length == 0 ? "" : "/" + suffix), new { }, TestContext.Current.CancellationToken);
        Assert.Equal((HttpStatusCode)428, response.StatusCode);
    }

    [Theory]
    [InlineData("http://sql.example.test")]
    [InlineData("sql.example.test,1433")]
    [InlineData("sql.example.test:1433")]
    [InlineData("*.example.test")]
    [InlineData("sql.example.test;Password=not-a-credential")]
    [InlineData("unlisted.example.test")]
    public async Task Unsafe_or_unlisted_endpoint_is_rejected_without_database_or_network_access(string host)
    {
        using var api = new ProviderApiFactory();
        using var client = api.Client("operator");
        using var response = await client.PostAsJsonAsync("/provider-instances", new
        {
            providerType = "ssis",
            displayName = "Test",
            isEnabled = true,
            configuration = new { schemaId = "ssis.provider-instance/v1", data = new { host, port = 1433, username = "test" } },
            credential = new { password = "synthetic-write-only-input" }
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("synthetic-write-only-input", text, StringComparison.Ordinal);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}

internal sealed class ProviderApiFactory : WebApplicationFactory<Program>
{
    internal const string Issuer = "https://identity.example.test/realms/flowplane";
    private readonly RsaSecurityKey key = new(RSA.Create(2048)) { KeyId = Guid.NewGuid().ToString("N") };
    private readonly string? operatorSubject;
    private readonly IReadOnlyDictionary<string, string?> settings;
    private readonly Action<IServiceCollection>? configureServices;
    private readonly string environment;

    internal TestLogProvider Logs { get; } = new();

    internal ProviderApiFactory(string? operatorSubject = "operator", IReadOnlyDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? configureServices = null, string environment = "Development")
    {
        this.operatorSubject = operatorSubject;
        this.settings = settings ?? new Dictionary<string, string?>();
        this.configureServices = configureServices;
        this.environment = environment;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        foreach (var setting in settings) builder.UseSetting(setting.Key, setting.Value);
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["ConnectionStrings:FlowPlane"] = "Host=127.0.0.1;Port=1;Database=not-used;Username=test;Password=not-used;Timeout=1",
                ["FlowPlane:Authentication:Issuer"] = Issuer,
                ["FlowPlane:Authentication:MetadataAddress"] = Issuer + "/.well-known/openid-configuration",
                ["FlowPlane:Authentication:Audience"] = "flowplane-api",
                ["FlowPlane:Ssis:AllowedEndpoints:0:Host"] = "sql.example.test",
                ["FlowPlane:Ssis:AllowedEndpoints:0:Port"] = "1433",
            };
            if (operatorSubject is not null)
            {
                values["FlowPlane:Operators:Allowed:0:Issuer"] = Issuer;
                values["FlowPlane:Operators:Allowed:0:Subject"] = operatorSubject;
            }
            foreach (var setting in settings) values[setting.Key] = setting.Value;
            configuration.AddInMemoryCollection(values);
        });
        builder.ConfigureServices(services =>
        {
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                configuration.SigningKeys.Add(key);
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            });
            services.AddLogging(logging => logging.ClearProviders().AddProvider(Logs));
            configureServices?.Invoke(services);
        });
    }

    internal HttpClient Client(string? subject)
    {
        var client = CreateClient();
        if (subject is not null)
        {
            var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Issuer,
                Audience = "flowplane-api",
                Expires = DateTime.UtcNow.AddMinutes(5),
                Subject = new ClaimsIdentity([new Claim("sub", subject)]),
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
            });
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) key.Rsa.Dispose();
    }
}

internal sealed class TestLogProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> entries = new();
    internal string Text => string.Join('\n', entries);
    public ILogger CreateLogger(string categoryName) => new TestLogger(entries);
    public void Dispose() { }
    private sealed class TestLogger(ConcurrentQueue<string> entries) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => entries.Enqueue(formatter(state, exception) + exception?.ToString());
    }
}
