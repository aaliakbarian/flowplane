using FlowPlane.Application.Providers.DiscoveryInventory;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FlowPlane.Api.Tests;

[Collection(SsisConfigurationCollection.Name)]
public sealed class DiscoveryInventoryRegistrationTests
{
    [Fact]
    public void Operator_scan_timeout_and_inventory_budgets_share_one_snapshot_without_external_services()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:FlowPlane", "Host=127.0.0.1;Port=1;Database=unreachable;Username=test;Password=test-only");
            builder.UseSetting("FlowPlane:Ssis:OverallTimeoutSeconds", "86400");
            builder.UseSetting("FlowPlane:DiscoveryInventory:PreparationTimeoutSeconds", "600");
        });
        var options = factory.Services.GetRequiredService<DiscoveryInventoryOptions>();
        Assert.Equal(TimeSpan.FromSeconds(87060), options.RefreshTimeout);
        using var scope = factory.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ProviderDiscoveryRefreshService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IProviderDiscoveryInventoryStore>());
    }
}
