using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using FlowPlane.Api.Authentication;
using FlowPlane.Application.Providers;
using FlowPlane.Application.Providers.DiscoveryInventory;
using FlowPlane.Application.Secrets;
using FlowPlane.Domain.Providers;
using FlowPlane.Providers.Ssis.Configuration;

using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace FlowPlane.Api.Providers;

internal static class ProviderApi
{
    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };

    public static void AddProviderApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(provider =>
        {
            var operators = provider.GetRequiredService<IConfiguration>().GetSection("FlowPlane:Operators")
                .Get<OperatorOptions>(options => options.ErrorOnUnknownConfiguration = true) ?? new OperatorOptions();
            operators.Validate();
            return operators;
        });
        services.AddSingleton<IAuthorizationHandler, OperatorAuthorizationHandler>();
        services.AddAuthorizationBuilder().AddPolicy(OperatorAuthorizationHandler.Policy,
            policy => policy.RequireAuthenticatedUser().AddRequirements(new OperatorRequirement()));
        services.AddSingleton(provider => new SsisEndpointPolicy(provider.GetRequiredService<IConfiguration>()
            .GetSection("FlowPlane:Ssis:AllowedEndpoints")
            .Get<SsisAllowedEndpoint[]>(options => options.ErrorOnUnknownConfiguration = true) ?? []));
    }

    public static void MapProviderApi(this WebApplication app)
    {
        var group = app.MapGroup("/provider-instances").RequireAuthorization(OperatorAuthorizationHandler.Policy);
        group.AddEndpointFilter(async (invocation, next) =>
        {
            invocation.HttpContext.Response.Headers.CacheControl = "no-store";
            try { return await next(invocation); }
            catch (ApiFailure error) { return Problem(error.Status, error.Code); }
            catch (ProviderConfigurationException) { return Problem(400, "ProviderConfigurationInvalid"); }
            catch (JsonException) { return Problem(400, "InvalidJson"); }
            catch (ArgumentException) { return Problem(400, "ProviderConfigurationInvalid"); }
            catch (DbUpdateConcurrencyException) { return Problem(412, "ProviderVersionChanged"); }
            catch (DiscoveryInventoryException error)
            {
                return Problem(error.Code == DiscoveryInventoryError.ProviderUnavailable ? 404 : 409, "ProviderUnavailable");
            }
            catch (SecretResolutionException error)
            {
                return Problem(error.Code == SecretErrorCode.ConcurrencyConflict ? 412 : 503, "CredentialProtectionUnavailable");
            }
            catch (OperationCanceledException) when (invocation.HttpContext.RequestAborted.IsCancellationRequested) { throw; }
            catch (DbException) { return Problem(503, "StorageUnavailable"); }
            catch (DbUpdateException) { return Problem(503, "StorageUnavailable"); }
            catch (Exception) { return Problem(500, "ProviderOperationFailed"); }
        });
        group.MapGet("", ListAsync);
        group.MapPost("", CreateAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPut("/{id:guid}", UpdateAsync);
        group.MapPut("/{id:guid}/credential", ReplaceCredentialAsync);
        group.MapPost("/{id:guid}/validation", ValidateAsync);
    }

    private static async Task<IResult> ListAsync(HttpContext http, IProviderInstanceStore store, SsisProviderConfigurationCodec codec)
    {
        var offset = QueryNumber(http, "offset", 0, 0, int.MaxValue);
        var limit = QueryNumber(http, "limit", 50, 1, 100);
        var rows = await store.ListAsync(offset, limit, http.RequestAborted);
        return Results.Ok(rows.Select(instance => Response(instance, codec)).ToArray());
    }

    private static async Task<IResult> GetAsync(Guid id, HttpContext http, IProviderInstanceStore store, SsisProviderConfigurationCodec codec)
    {
        var instance = await FindAsync(id, store, http.RequestAborted);
        SetVersion(http, instance.Version);
        return Results.Ok(Response(instance, codec));
    }

    private static async Task<IResult> CreateAsync(HttpContext http, IProviderInstanceStore store,
        SsisProviderConfigurationCodec codec, SsisEndpointPolicy endpoints, IHostEnvironment environment)
    {
        var input = await ReadAsync<CreateProviderInput>(http.Request);
        var configuration = Configuration(input, codec, endpoints, environment);
        var password = Password(input.Credential);
        var instance = await store.CreateWithManagedSecretAsync(Guid.CreateVersion7(), SsisProviderConfigurationCodec.SsisProviderType,
            input.DisplayName, input.IsEnabled, configuration, password.AsMemory(), DateTimeOffset.UtcNow, http.RequestAborted);
        SetVersion(http, instance.Version);
        return Results.Created($"/api/provider-instances/{instance.Id:D}", Response(instance, codec));
    }

    private static async Task<IResult> UpdateAsync(Guid id, HttpContext http, IProviderInstanceStore store,
        SsisProviderConfigurationCodec codec, SsisEndpointPolicy endpoints, IHostEnvironment environment)
    {
        var version = ExpectedVersion(http);
        var input = await ReadAsync<ProviderConfigurationInput>(http.Request);
        var configuration = Configuration(input, codec, endpoints, environment);
        var original = await FindAsync(id, store, http.RequestAborted);
        if (original.Version != version) throw new ApiFailure(412, "ProviderVersionChanged");
        if (original.ProviderType.Value != input.ProviderType) throw new ApiFailure(400, "ProviderTypeImmutable");
        var instance = await store.UpdateAsync(id, version, input.DisplayName, input.IsEnabled, configuration,
            DateTimeOffset.UtcNow, http.RequestAborted);
        SetVersion(http, instance.Version);
        return Results.Ok(Response(instance, codec));
    }

    private static async Task<IResult> ReplaceCredentialAsync(Guid id, HttpContext http, IProviderInstanceStore store)
    {
        var version = ExpectedVersion(http);
        var password = Password(await ReadAsync<CredentialInput>(http.Request));
        var instance = await FindAsync(id, store, http.RequestAborted);
        if (instance.Version != version) throw new ApiFailure(412, "ProviderVersionChanged");
        await store.ReplaceManagedSecretAsync(id, version, password.AsMemory(), DateTimeOffset.UtcNow, http.RequestAborted);
        SetVersion(http, checked(version + 1));
        return Results.NoContent();
    }

    private static async Task<IResult> ValidateAsync(Guid id, HttpContext http, IProviderInstanceStore store,
        SsisProviderConfigurationCodec codec, SsisEndpointPolicy endpoints, IHostEnvironment environment,
        IEnumerable<IProviderInstanceValidationCapability> validators)
    {
        var instance = await FindAsync(id, store, http.RequestAborted);
        var configuration = codec.Decode(instance.Configuration);
        RequireEndpoint(configuration, endpoints, environment);
        var validator = validators.SingleOrDefault(capability => capability.ProviderType.Equals(instance.ProviderType))
            ?? throw new ApiFailure(409, "ProviderTypeUnsupported");
        var result = await validator.ValidateAsync(instance, http.RequestAborted,
            async token => (await store.FindAsync(id, token))?.Version == instance.Version);
        var current = await FindAsync(id, store, http.RequestAborted);
        if (current.Version != instance.Version) throw new ApiFailure(409, "ProviderContextChanged");
        SetVersion(http, instance.Version);
        return Results.Ok(result);
    }

    private static ProviderResponse Response(ProviderInstance instance, SsisProviderConfigurationCodec codec)
    {
        if (!instance.ProviderType.Equals(SsisProviderConfigurationCodec.SsisProviderType)) throw new ApiFailure(409, "ProviderTypeUnsupported");
        var decoded = codec.Decode(instance.Configuration);
        return new(instance.Id, instance.ProviderType.Value, instance.DisplayName, instance.IsEnabled,
            new(instance.Configuration.SchemaId, JsonSerializer.SerializeToElement(decoded, InputJson)),
            instance.Version, instance.CreatedAtUtc, instance.UpdatedAtUtc);
    }

    private static ProviderConfigurationDocument Configuration(ProviderConfigurationInput input, SsisProviderConfigurationCodec codec,
        SsisEndpointPolicy endpoints, IHostEnvironment environment)
    {
        if (input.ProviderType != "ssis" || input.Configuration is null ||
            input.Configuration.Data.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(input.DisplayName) ||
            input.DisplayName.Length > 200 || input.DisplayName != input.DisplayName.Trim() || input.DisplayName.Any(char.IsControl))
            throw new ApiFailure(400, "ProviderConfigurationInvalid");
        var document = codec.DecodeInput(input.Configuration.SchemaId, input.Configuration.Data.GetRawText());
        RequireEndpoint(codec.Decode(document), endpoints, environment);
        return document;
    }

    private static void RequireEndpoint(SsisProviderConfiguration configuration, SsisEndpointPolicy endpoints, IHostEnvironment environment)
    {
        if (!endpoints.Allows(configuration)) throw new ApiFailure(400, "ProviderEndpointDenied");
        if (configuration.TrustServerCertificate && !environment.IsDevelopment()) throw new ApiFailure(400, "ProviderTlsPolicyDenied");
    }

    private static string Password(CredentialInput? input) => input?.Password is { Length: >= 1 and <= 4096 } password
        ? password : throw new ApiFailure(400, "CredentialInputInvalid");

    private static async Task<ProviderInstance> FindAsync(Guid id, IProviderInstanceStore store, CancellationToken token) =>
        await store.FindAsync(id, token) ?? throw new ApiFailure(404, "ProviderInstanceNotFound");

    private static void SetVersion(HttpContext http, long version) => http.Response.Headers.ETag = $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    private static long ExpectedVersion(HttpContext http)
    {
        var value = http.Request.Headers.IfMatch;
        if (value.Count == 0) throw new ApiFailure(428, "MutationPreconditionRequired");
        var text = value.ToString();
        if (value.Count != 1 || text.Length is < 3 or > 21 || text[0] != '"' || text[^1] != '"' ||
            !long.TryParse(text.AsSpan(1, text.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var version) ||
            version < 1 || text != $"\"{version.ToString(CultureInfo.InvariantCulture)}\"")
            throw new ApiFailure(400, "MutationPreconditionInvalid");
        return version;
    }

    private static int QueryNumber(HttpContext http, string key, int fallback, int min, int max)
    {
        if (!http.Request.Query.TryGetValue(key, out var values)) return fallback;
        if (values.Count != 1 || !int.TryParse(values.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            throw new ApiFailure(400, "InvalidListBounds");
        return value;
    }

    private static async Task<T> ReadAsync<T>(HttpRequest request)
    {
        if (!request.HasJsonContentType()) throw new ApiFailure(415, "JsonContentRequired");
        const int maximum = 32 * 1024;
        if (request.ContentLength > maximum) throw new ApiFailure(413, "RequestTooLarge");
        using var stream = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = await request.Body.ReadAsync(buffer, request.HttpContext.RequestAborted)) > 0)
        {
            if (stream.Length + count > maximum) throw new ApiFailure(413, "RequestTooLarge");
            stream.Write(buffer, 0, count);
        }
        using var document = JsonDocument.Parse(stream.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
        RejectDuplicates(document.RootElement);
        return document.RootElement.Deserialize<T>(InputJson) ?? throw new ApiFailure(400, "InvalidJson");
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new ApiFailure(400, "InvalidJson");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }

    internal static IResult Problem(int status, string code) => Results.Problem(statusCode: status,
        title: code, type: "about:blank", extensions: new Dictionary<string, object?> { ["code"] = code });

    private sealed class ApiFailure(int status, string code) : Exception
    {
        public int Status { get; } = status;
        public string Code { get; } = code;
    }
}
