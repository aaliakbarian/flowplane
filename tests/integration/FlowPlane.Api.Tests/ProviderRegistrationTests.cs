using FlowPlane.Application.Providers;
using FlowPlane.Domain.Providers;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FlowPlane.Api.Tests;

public sealed class ProviderRegistrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> factory;

    public ProviderRegistrationTests(WebApplicationFactory<Program> factory)
    {
        this.factory = factory;
    }

    [Fact]
    public void Api_composition_root_registers_the_SSIS_validation_capability()
    {
        using var configuredFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:FlowPlane"] =
                        "Host=127.0.0.1;Port=1;Database=flowplane;Username=flowplane;Password=test-only"
                });
            });
        });
        using var scope = configuredFactory.Services.CreateScope();

        var capability = Assert.Single(
            scope.ServiceProvider.GetServices<IProviderInstanceValidationCapability>());

        Assert.Equal(ProviderType.Parse("ssis"), capability.ProviderType);
    }
}
