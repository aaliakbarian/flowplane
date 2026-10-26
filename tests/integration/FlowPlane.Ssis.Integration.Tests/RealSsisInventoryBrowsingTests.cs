using System.Text.Json;

using FlowPlane.Application.Providers.DiscoveryInventory;
using FlowPlane.Domain.Providers;
using FlowPlane.Infrastructure.Persistence;
using FlowPlane.Persistence.Tests;
using FlowPlane.Providers.Ssis;
using FlowPlane.Providers.Ssis.Configuration;

using Microsoft.Extensions.DependencyInjection;

namespace FlowPlane.Ssis.Integration.Tests;

[Collection(RealSsisCollection.Name)]
public sealed class RealSsisInventoryBrowsingTests
{
    [Theory]
    [InlineData("VALID", true)]
    [InlineData("READONLY", true)]
    [InlineData("NO_OBJECT_PERMISSION", true)]
    [InlineData("WRONG_PASSWORD", false)]
    [InlineData("NO_CATALOG_ACCESS", false)]
    [InlineData("TLS_FAILURE", false)]
    public async Task Published_pages_require_fresh_native_visibility_without_mutating_the_fixture(string role, bool disclose)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("FLOWPLANE_TEST_SSIS_A11_ENABLED") == "true", "Enable reviewed real fixture.");
        var valid = RealSsisSettings.Load(disclose ? role : "VALID");
        var settings = RealSsisSettings.Load(role is "WRONG_PASSWORD" or "TLS_FAILURE" ? "VALID" : role);
        Assert.SkipWhen(valid is null || settings is null, "Configure private controlled SSIS roles.");
        var token = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        Assert.SkipWhen(database is null, "Configure isolated PostgreSQL 18.");
        await using var services = database!.CreateServices(null, s => s.AddFlowPlaneSsisValidation(true));
        await database.MigrateAsync(services, token);
        await using var scope = services.CreateAsyncScope();
        var instance = ProviderInstance.Create(Guid.CreateVersion7(), ProviderType.Parse("ssis"), "Controlled browsing", true,
            new SsisProviderConfigurationCodec().Encode(new(valid!.Host, valid.Port, valid.Username, valid.TrustServerCertificate)),
            SecretReference.Environment(valid.PasswordVariable), DateTimeOffset.UtcNow);
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        db.ProviderInstances.Add(instance); await db.SaveChangesAsync(token);
        var publication = await scope.ServiceProvider.GetRequiredService<ProviderDiscoveryRefreshService>().RefreshAsync(instance.Id, token);
        Assert.Equal(DiscoveryPublicationStatus.Published, publication.Status);
        var pages = scope.ServiceProvider.GetRequiredService<IProviderInventoryPageStore>();
        var native = scope.ServiceProvider.GetRequiredService<IProviderInventoryDisclosure>();
        var browsing = new InventoryBrowsingService(pages, new TestCursor());
        if (disclose)
        {
            var result = await browsing.ReadAsync(instance, new(Limit: 100), null, native, _ => Task.FromResult(true), token);
            Assert.Equal(publication.RefreshId, result.GenerationId);
            if (role == "NO_OBJECT_PERMISSION") Assert.Empty(result.Items);
            else
            {
                Assert.Contains(result.Items, item => item.ExecutableReference?.Data.GetProperty("packageName").GetString() == "EntryPoint.dtsx");
                Assert.Contains(result.Items, item => item.SchemaId.EndsWith("package.v1", StringComparison.Ordinal) && item.Metadata.GetProperty("packageName").GetString() == "InternalStep.dtsx" && item.ExecutableReference is null);
            }
            Safe(JsonSerializer.Serialize(result), settings!);
        }
        else
        {
            // This part exercises the real provider disclosure boundary against an
            // existing published cache. Credentials/config below are test-local;
            // no SQL logins, permissions, fixtures or persisted provider are changed.
            var variable = "FLOWPLANE_STAGE3_NEGATIVE_" + Guid.NewGuid().ToString("N");
            try
            {
                Environment.SetEnvironmentVariable(variable, role == "WRONG_PASSWORD" ? Guid.NewGuid().ToString("N") : Environment.GetEnvironmentVariable(settings!.PasswordVariable));
                var negative = ProviderInstance.Create(instance.Id, instance.ProviderType, instance.DisplayName, true,
                    new SsisProviderConfigurationCodec().Encode(new(settings!.Host, settings.Port, settings.Username, role == "TLS_FAILURE" ? false : settings.TrustServerCertificate)),
                    SecretReference.Environment(variable), DateTimeOffset.UtcNow);
                var error = await Assert.ThrowsAsync<InventoryBrowseException>(() => native.VerifyAsync(negative, [], _ => Task.FromResult(true), token));
                Assert.Equal(InventoryBrowseError.NativeUnavailable, error.Code);
                Safe(error.ToString(), settings);
            }
            finally { Environment.SetEnvironmentVariable(variable, null); }
        }
    }

    private static void Safe(string output, RealSsisSettings settings)
    {
        Assert.DoesNotContain(Environment.GetEnvironmentVariable(settings.PasswordVariable)!, output, StringComparison.Ordinal);
        Assert.DoesNotContain(settings.PasswordVariable, output, StringComparison.Ordinal);
        Assert.DoesNotContain("flowplane-a11-sensitive-canary-value", output, StringComparison.Ordinal);
        Assert.DoesNotContain("default_value", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password=", output, StringComparison.OrdinalIgnoreCase);
    }
    private sealed class TestCursor : IInventoryCursorCodec
    {
        public string Encode(InventoryCursor cursor) => JsonSerializer.Serialize(cursor);
        public InventoryCursor Decode(string cursor) => JsonSerializer.Deserialize<InventoryCursor>(cursor)!;
    }
}
