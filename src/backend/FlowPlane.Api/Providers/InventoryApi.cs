using FlowPlane.Api.Authentication;
using FlowPlane.Application.Providers;
using FlowPlane.Application.Providers.DiscoveryInventory;
using FlowPlane.Providers.Ssis.Configuration;

using Microsoft.AspNetCore.Authorization;

namespace FlowPlane.Api.Providers;

internal static partial class ProviderApi
{
    private static async Task<IResult> InventoryAsync(Guid id, HttpContext http, IProviderInstanceStore providers,
        SsisProviderConfigurationCodec codec, SsisEndpointPolicy endpoints, IHostEnvironment environment,
        IAuthorizationService authorization, InventoryBrowsingService browsing, IEnumerable<IProviderInventoryDisclosure> disclosures)
    {
        var provider = await FindAsync(id, providers, http.RequestAborted);
        RequireDiscoveryProvider(provider, codec, endpoints, environment);
        if (http.Request.Query.Keys.Any(key => key is not ("schema" or "candidatesOnly" or "limit" or "cursor")))
            throw new InventoryBrowseException(InventoryBrowseError.InvalidRequest);
        string? One(string name)
        {
            if (!http.Request.Query.TryGetValue(name, out var values)) return null;
            if (values.Count != 1 || string.IsNullOrEmpty(values[0])) throw new InventoryBrowseException(InventoryBrowseError.InvalidRequest);
            return values[0];
        }
        var candidates = One("candidatesOnly") switch
        { null or "false" => false, "true" => true, _ => throw new InventoryBrowseException(InventoryBrowseError.InvalidRequest) };
        var query = new InventoryPageQuery(One("schema"), candidates, QueryNumber(http, "limit", 50, 1, 100));
        var disclosure = disclosures.SingleOrDefault(item => item.ProviderType.Equals(provider.ProviderType))
            ?? throw new ApiFailure(409, "ProviderTypeUnsupported");
        async Task<bool> Current(CancellationToken token)
        {
            if (!(await authorization.AuthorizeAsync(http.User, id, OperatorAuthorizationHandler.Policy)).Succeeded) return false;
            var current = await providers.FindAsync(id, token);
            if (current is null || current.Version != provider.Version || !current.IsEnabled) return false;
            RequireDiscoveryProvider(current, codec, endpoints, environment);
            return true;
        }
        var page = await browsing.ReadAsync(provider, query, One("cursor"), disclosure, Current, http.RequestAborted);
        SetVersion(http, provider.Version);
        return Results.Ok(page);
    }
}
