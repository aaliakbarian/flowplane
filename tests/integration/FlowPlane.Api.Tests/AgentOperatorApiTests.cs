using System.Net;
using System.Net.Http.Json;

namespace FlowPlane.Api.Tests;

public sealed class AgentOperatorApiTests
{
    [Theory]
    [InlineData(null, 401)]
    [InlineData("non-operator", 403)]
    public async Task Agent_administration_requires_existing_JWT_operator_policy(string? subject, int status)
    {
        using var api = new ProviderApiFactory();
        using var client = api.Client(subject);
        using var response = await client.PostAsJsonAsync("/agents/pairing-grants", new { }, TestContext.Current.CancellationToken);
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
    }

    [Fact]
    public async Task Disabled_agent_foundation_cannot_access_F3_tables_or_issue_enrollment()
    {
        using var api = new ProviderApiFactory();
        using var client = api.Client("operator");
        using var response = await client.PostAsJsonAsync("/agents/pairing-grants", new { }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("AgentControlDisabled", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Browser_listener_cannot_become_agent_listener_via_spoofed_headers()
    {
        using var api = new ProviderApiFactory();
        using var client = api.Client("operator");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-Port", "8443");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-SSL-CERT", "sensitive-canary");
        using var response = await client.GetAsync("/agent/v1/session", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("sensitive-canary", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }
}
