using System.Text.Json;

using FlowPlane.Application.Providers.DiscoveryInventory;
using FlowPlane.Domain.Providers;
using FlowPlane.Infrastructure.Persistence;
using FlowPlane.Persistence.Tests;
using FlowPlane.Providers.Ssis;
using FlowPlane.Providers.Ssis.Configuration;
using FlowPlane.Providers.Ssis.Discovery;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FlowPlane.Ssis.Integration.Tests;

[Collection(RealSsisCollection.Name)]
public sealed class RealSsisInventoryPublicationTests
{
    [Theory]
    [InlineData("VALID", true)]
    [InlineData("READONLY", true)]
    [InlineData("NO_OBJECT_PERMISSION", true)]
    [InlineData("WRONG_PASSWORD", false)]
    [InlineData("NO_CATALOG_ACCESS", false)]
    [InlineData("TLS_FAILURE", false)]
    public async Task Controlled_source_scan_publishes_only_complete_inventory(string role, bool publish)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("FLOWPLANE_TEST_DISCOVERY_MIGRATION_APPROVED") == "true",
            "Pending A-11b migration approval; no database setup or SSISDB connection is performed.");
        Assert.SkipUnless(Environment.GetEnvironmentVariable("FLOWPLANE_TEST_SSIS_A11_ENABLED") == "true", "Enable the reviewed real A-11 fixture.");
        var settings = RealSsisSettings.Load(role == "WRONG_PASSWORD" ? "VALID" : role);
        Assert.SkipWhen(settings is null, "Configure the required private SSIS test role.");
        var token = TestContext.Current.CancellationToken;
        var privateVariable = $"FLOWPLANE_INVENTORY_TEST_{Guid.NewGuid():N}";
        if (role == "WRONG_PASSWORD")
        {
            Environment.SetEnvironmentVariable(privateVariable, Guid.NewGuid().ToString("N"));
            settings = settings! with { PasswordVariable = privateVariable };
        }
        if (role == "TLS_FAILURE") settings = settings! with { TrustServerCertificate = false };
        try
        {
            await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
            Assert.SkipWhen(database is null, "Configure controlled PostgreSQL 18 for publication acceptance.");
            await using var services = database!.CreateServices(null, registrations =>
                registrations.AddFlowPlaneSsisValidation(true, discoveryOptions: new SsisDiscoveryOptions { MaxProjectsPerBatch = 1 }));
            await database.MigrateAsync(services, token);
            var instance = ProviderInstance.Create(Guid.CreateVersion7(), SsisProviderConfigurationCodec.SsisProviderType,
                "Controlled publication", true, new SsisProviderConfigurationCodec().Encode(new(settings!.Host,
                    settings.Port, settings.Username, settings.TrustServerCertificate)),
                SecretReference.Environment(settings.PasswordVariable), DateTimeOffset.UtcNow);
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
            db.ProviderInstances.Add(instance);
            await db.SaveChangesAsync(token);
            var result = await scope.ServiceProvider.GetRequiredService<ProviderDiscoveryRefreshService>().RefreshAsync(instance.Id, token);
            Assert.True(result.Status == (publish ? DiscoveryPublicationStatus.Published : DiscoveryPublicationStatus.NotPublished),
                $"Publication for {role} returned {result.Status}, safe error {result.Error}.");
            Assert.False(JsonSerializer.Serialize(result).Contains(Environment.GetEnvironmentVariable(settings.PasswordVariable)!, StringComparison.Ordinal));
            if (!publish) return;
            var store = scope.ServiceProvider.GetRequiredService<IProviderDiscoveryInventoryStore>();
            await using var reader = await store.OpenReaderAsync(instance.Id, result.RefreshId!.Value, token);
            var entry = false;
            var internalPackage = false;
            long count = 0;
            await foreach (var row in reader.ReadAsync(null, token))
            {
                count++;
                var json = JsonSerializer.Serialize(row);
                Assert.False(json.Contains(settings.PasswordVariable, StringComparison.Ordinal), "Inventory exposed a secret locator.");
                Assert.False(json.Contains(Environment.GetEnvironmentVariable(settings.PasswordVariable)!, StringComparison.Ordinal), "Inventory exposed a credential.");
                Assert.DoesNotContain("flowplane-a11-sensitive-canary-value", json, StringComparison.Ordinal);
                if (row.SchemaId != "flowplane.ssis/discovery-package.v1") continue;
                using var metadata = JsonDocument.Parse(row.Metadata!.CanonicalJson);
                if (metadata.RootElement.GetProperty("projectName").GetString() != "FlowPlaneA11Fixture") continue;
                switch (metadata.RootElement.GetProperty("packageName").GetString())
                {
                    case "EntryPoint.dtsx": entry = true; Assert.NotNull(row.ExecutableCandidate); break;
                    case "InternalStep.dtsx": internalPackage = true; Assert.Null(row.ExecutableCandidate); break;
                }
            }
            if (role == "NO_OBJECT_PERMISSION") Assert.Equal(0, count);
            else { Assert.True(entry); Assert.True(internalPackage); }
            foreach (var sql in new[]
            {
                "SELECT row_to_json(t)::text AS \"Value\" FROM \"ProviderDiscoveryStates\" t",
                "SELECT row_to_json(t)::text AS \"Value\" FROM \"ProviderDiscoveryGenerations\" t",
                "SELECT row_to_json(t)::text AS \"Value\" FROM \"ProviderDiscoveryObservations\" t",
            })
            {
                await foreach (var raw in db.Database.SqlQueryRaw<string>(sql).AsAsyncEnumerable().WithCancellation(token))
                {
                    Assert.False(raw.Contains(Environment.GetEnvironmentVariable(settings.PasswordVariable)!, StringComparison.Ordinal), "PostgreSQL inventory disclosed a credential.");
                    Assert.False(raw.Contains(settings.PasswordVariable, StringComparison.Ordinal), "PostgreSQL inventory disclosed a secret locator.");
                    Assert.False(raw.Contains("flowplane-a11-sensitive-canary-value", StringComparison.Ordinal), "PostgreSQL inventory disclosed a sensitive fixture value.");
                    Assert.False(raw.Contains("Password=", StringComparison.OrdinalIgnoreCase), "PostgreSQL inventory disclosed a connection string.");
                    Assert.False(raw.Contains("PRIVATE KEY", StringComparison.Ordinal), "PostgreSQL inventory disclosed a private key.");
                }
            }
        }
        finally { Environment.SetEnvironmentVariable(privateVariable, null); }
    }
}
