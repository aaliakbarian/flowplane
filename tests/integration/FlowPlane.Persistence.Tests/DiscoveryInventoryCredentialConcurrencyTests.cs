using System.Diagnostics;
using FlowPlane.Application.Providers;
using FlowPlane.Application.Providers.DiscoveryInventory;
using FlowPlane.Application.Secrets;
using FlowPlane.Domain.Providers;
using FlowPlane.Infrastructure.Persistence;
using FlowPlane.Infrastructure.Persistence.Discovery;
using FlowPlane.Providers.Ssis.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FlowPlane.Persistence.Tests;

public sealed class DiscoveryInventoryCredentialConcurrencyTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Concurrent_credential_replacement_commits_before_obsolete_publication(bool directManagedReplacement)
    {
        ProviderDiscoveryInventoryTests.RequireMigrationApproval();
        var token = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        Assert.SkipWhen(database is null, "Configure isolated PostgreSQL 18.");
        var directory = Path.Combine(Path.GetTempPath(), $"flowplane-credential-race-{Guid.NewGuid():N}");
        var certificate = PostgreSqlTestDatabase.CreateRootCertificate(directory, "race-root");
        try
        {
            await using var services = database!.CreateServices(certificate);
            await database.MigrateAsync(services, token);
            await using var scope = services.CreateAsyncScope();
            var instance = await scope.ServiceProvider.GetRequiredService<IProviderInstanceStore>().CreateWithManagedSecretAsync(
                Guid.NewGuid(), SsisProviderConfigurationCodec.SsisProviderType, "Credential race", true,
                new SsisProviderConfigurationCodec().Encode(new("fixture", 1433, "fixture-user", false)),
                Guid.NewGuid().ToString("N").AsMemory(), DateTimeOffset.UtcNow, token);
            var store = scope.ServiceProvider.GetRequiredService<IProviderDiscoveryInventoryStore>();
            await using var refresh = await store.BeginRefreshAsync(instance.Id, token);
            await using var blockingScope = services.CreateAsyncScope();
            var blocked = blockingScope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
            await using var blocker = await blocked.Database.BeginTransactionAsync(token);
            await DiscoveryDatabase.LockSecretAsync(blocked, instance, token);
            await using var editing = services.CreateAsyncScope();
            var replacement = directManagedReplacement
                ? editing.ServiceProvider.GetRequiredService<IManagedSecretStore>().ReplaceAsync(instance.SecretReference,
                    Guid.NewGuid().ToString("N").AsMemory(), token).AsTask()
                : editing.ServiceProvider.GetRequiredService<IProviderInstanceStore>().ReplaceManagedSecretAsync(instance.Id,
                    1, Guid.NewGuid().ToString("N").AsMemory(), DateTimeOffset.UtcNow, token);
            var probe = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
            await WaitForLocks(1);
            var publication = refresh.PublishAsync(ProviderDiscoveryInventoryTests.Summary(0), token);
            await WaitForLocks(2);
            await blocker.CommitAsync(token);
            await replacement;
            var error = await Assert.ThrowsAsync<DiscoveryInventoryException>(() => publication);
            Assert.Equal(DiscoveryInventoryError.ContextChanged, error.Code);
            await refresh.AbortAsync(error.Code, token);
            Assert.Null((await probe.DiscoveryStates.AsNoTracking().SingleAsync(token)).CurrentGenerationId);
            Assert.Equal(directManagedReplacement ? 1 : 2, (await probe.ProviderInstances.AsNoTracking().SingleAsync(token)).Version);

            async Task WaitForLocks(int minimum)
            {
                var timer = Stopwatch.StartNew();
                while (timer.Elapsed < TimeSpan.FromSeconds(3))
                {
                    var count = await probe.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'").SingleAsync(token);
                    if (count >= minimum) return;
                    await Task.Delay(20, token);
                }
                Assert.Fail("Concurrent test operations did not reach the expected database lock boundary.");
            }
        }
        finally { Directory.Delete(directory, true); }
    }
}
