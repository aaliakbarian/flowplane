using System.Net;

namespace FlowPlane.Api.Tests;

public sealed class DiscoveryRefreshApiTests
{
    [Fact]
    public async Task Missing_admission_precondition_is_428_before_any_database_access()
    {
        using var api = new ProviderApiFactory();
        using var client = api.Client("operator");
        using var response = await client.PostAsync("/provider-instances/00000000-0000-0000-0000-000000000001/discovery-refreshes", null,
            TestContext.Current.CancellationToken);
        Assert.Equal((HttpStatusCode)428, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-key")]
    [InlineData("0199a12b-1234-4000-8000-123456789abc")]
    public async Task Invalid_or_v4_idempotency_keys_cannot_access_storage(string key)
    {
        using var api = new ProviderApiFactory();
        using var client = api.Client("operator");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/provider-instances/00000000-0000-0000-0000-000000000001/discovery-refreshes");
        request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Disabled_hosting_fails_safely_without_requiring_Gate_B_or_SSIS()
    {
        using var api = new ProviderApiFactory();
        using var client = api.Client("operator");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/provider-instances/00000000-0000-0000-0000-000000000001/discovery-refreshes");
        request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.CreateVersion7().ToString("D"));
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("POST", "/provider-instances/00000000-0000-0000-0000-000000000001/discovery-refreshes")]
    [InlineData("GET", "/provider-instances/00000000-0000-0000-0000-000000000001/discovery-refreshes/00000000-0000-0000-0000-000000000002")]
    public async Task Refresh_routes_require_authentication_and_exact_operator_authorization(string method, string path)
    {
        using var api = new ProviderApiFactory();
        foreach (var subject in new string?[] { null, "nonoperator" })
        {
            using var client = api.Client(subject);
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(subject is null ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, response.StatusCode);
        }
    }
}
