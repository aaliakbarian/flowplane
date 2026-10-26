using System.Security.Claims;

using FlowPlane.Application.Providers;
using FlowPlane.Application.Providers.DiscoveryHosting;
using FlowPlane.Providers.Ssis.Configuration;

namespace FlowPlane.Api.Providers;

internal static partial class ProviderApi
{
    private static void MapDiscoveryRefreshes(RouteGroupBuilder group)
    {
        group.MapPost("/{id:guid}/discovery-refreshes", AdmitDiscoveryAsync);
        group.MapGet("/{id:guid}/discovery-refreshes/{refreshId:guid}", DiscoveryStatusAsync);
    }

    private static async Task<IResult> AdmitDiscoveryAsync(Guid id, HttpContext http, IProviderInstanceStore providers,
        IHostedDiscoveryStore hosting, DiscoveryHostingOptions options, SsisProviderConfigurationCodec codec,
        SsisEndpointPolicy endpoints, IHostEnvironment environment)
    {
        var version = ExpectedVersion(http);
        var header = http.Request.Headers["Idempotency-Key"];
        if (header.Count != 1) throw new HostedDiscoveryException(HostedDiscoveryError.InvalidKey);
        var key = DiscoveryIdempotencyKey.Parse(header.ToString());
        if (http.Request.ContentLength > 0 || http.Request.Headers.ContainsKey("Transfer-Encoding"))
            throw new ApiFailure(400, "DiscoveryBodyNotAllowed");
        if (!options.Enabled) throw new HostedDiscoveryException(HostedDiscoveryError.Unavailable);
        var provider = await FindAsync(id, providers, http.RequestAborted);
        RequireDiscoveryProvider(provider, codec, endpoints, environment);
        var actor = new DiscoveryActor(http.User.FindFirstValue("iss")!, http.User.FindFirstValue("sub")!);
        var receipt = await hosting.AdmitAsync(new(key, id, version, actor), http.RequestAborted);
        http.Response.Headers.Location = $"/api/provider-instances/{id:D}/discovery-refreshes/{receipt.RefreshId:D}";
        http.Response.Headers.RetryAfter = "2";
        return Results.Json(DiscoveryResponse(receipt), statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> DiscoveryStatusAsync(Guid id, Guid refreshId, HttpContext http,
        IProviderInstanceStore providers, IHostedDiscoveryStore hosting, DiscoveryHostingOptions options,
        SsisProviderConfigurationCodec codec, SsisEndpointPolicy endpoints, IHostEnvironment environment)
    {
        if (!options.Enabled) throw new HostedDiscoveryException(HostedDiscoveryError.Unavailable);
        var provider = await FindAsync(id, providers, http.RequestAborted);
        RequireDiscoveryProvider(provider, codec, endpoints, environment);
        var receipt = await hosting.FindAsync(id, refreshId, http.RequestAborted)
            ?? throw new HostedDiscoveryException(HostedDiscoveryError.NotFound);
        return Results.Ok(DiscoveryResponse(receipt));
    }

    private static void RequireDiscoveryProvider(FlowPlane.Domain.Providers.ProviderInstance provider,
        SsisProviderConfigurationCodec codec, SsisEndpointPolicy endpoints, IHostEnvironment environment)
    {
        if (!provider.IsEnabled || !provider.ProviderType.Equals(SsisProviderConfigurationCodec.SsisProviderType))
            throw new ApiFailure(409, "ProviderUnavailable");
        RequireEndpoint(codec.Decode(provider.Configuration), endpoints, environment);
    }

    private static object DiscoveryResponse(HostedDiscoveryReceipt receipt) => new
    {
        receipt.RefreshId,
        receipt.ProviderInstanceId,
        Status = receipt.Phase.ToString(),
        receipt.AcceptedAtUtc,
        receipt.ExpiresAtUtc,
        receipt.Observations,
        receipt.Candidates,
        receipt.ScanCompleted,
        Error = receipt.Error?.ToString(),
    };
}
