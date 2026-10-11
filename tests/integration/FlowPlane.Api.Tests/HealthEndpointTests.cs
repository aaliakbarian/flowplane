using System.Net;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace FlowPlane.Api.Tests;

public sealed class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string UnavailablePostgreSqlConnection =
        "Host=127.0.0.1;Port=1;Database=flowplane;Username=flowplane;Password=test-only;Timeout=1;Command Timeout=1;Pooling=false";

    private readonly WebApplicationFactory<Program> factory;

    public HealthEndpointTests(WebApplicationFactory<Program> factory)
    {
        this.factory = factory;
    }

    [Fact]
    public void ApplicationCanBeCreated()
    {
        using var configuredFactory = CreateFactory(UnavailablePostgreSqlConnection);

        Assert.NotNull(configuredFactory.Services);
    }

    [Fact]
    public async Task LivenessEndpointReportsHealthy()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var configuredFactory = CreateFactory(UnavailablePostgreSqlConnection);
        using var client = configuredFactory.CreateClient();

        using var response = await client.GetAsync("/health/live", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(cancellationToken));
    }

    [Fact]
    public async Task ReadinessEndpointReportsUnhealthyWhenPostgreSqlIsUnavailable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var configuredFactory = CreateFactory(UnavailablePostgreSqlConnection);
        using var client = configuredFactory.CreateClient();

        using var response = await client.GetAsync("/health/ready", cancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("Unhealthy", await response.Content.ReadAsStringAsync(cancellationToken));
    }

    [Fact]
    public async Task ReadinessEndpointReportsHealthyWhenPostgreSqlIsAvailable()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "FLOWPLANE_TEST_POSTGRES_CONNECTION_STRING");
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(connectionString),
            "Set FLOWPLANE_TEST_POSTGRES_CONNECTION_STRING to run the PostgreSQL integration test.");

        var cancellationToken = TestContext.Current.CancellationToken;
        using var configuredFactory = CreateFactory(connectionString!);
        using var client = configuredFactory.CreateClient();

        using var response = await client.GetAsync("/health/ready", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private WebApplicationFactory<Program> CreateFactory(string connectionString)
    {
        return factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:FlowPlane"] = connectionString
                });
            });
        });
    }
}
