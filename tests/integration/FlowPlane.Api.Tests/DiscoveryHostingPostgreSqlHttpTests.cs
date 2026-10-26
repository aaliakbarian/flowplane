using System.Data.Common;
using System.Net;
using System.Reflection;
using System.Text.Json;

using FlowPlane.Application.Providers;
using FlowPlane.Domain.Providers;
using FlowPlane.Infrastructure.Persistence;
using FlowPlane.Persistence.Tests;
using FlowPlane.Providers.Ssis.Configuration;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace FlowPlane.Api.Tests;

public sealed class DiscoveryHostingPostgreSqlHttpTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HTTP_202_requires_a_committed_receipt_and_runner_executes_that_generation_exactly_once()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync(Token);
        Assert.SkipWhen(database is null, "Configure isolated PostgreSQL 18.");
        await using (var services = database!.CreateServices(null)) await database.MigrateAsync(services, Token);
        var boundary = new AdmissionCommitBoundary();
        var native = new CompleteScan();
        using var api = new ProviderApiFactory(settings: new Dictionary<string, string?>
        {
            ["ConnectionStrings:FlowPlane"] = database!.ConnectionString,
            ["FlowPlane:DiscoveryHosting:Enabled"] = "true",
        }, configureServices: services =>
        {
            // Invoke one real runner cycle deliberately, not a racing automatic hosted loop.
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IProviderDiscoveryCapability>();
            services.AddSingleton<IProviderDiscoveryCapability>(native);
            services.ConfigureDbContext<FlowPlaneDbContext>(options => options.AddInterceptors(boundary));
        });
        using var client = api.Client("operator");
        var instance = ProviderInstance.Create(Guid.CreateVersion7(), SsisProviderConfigurationCodec.SsisProviderType,
            "Synthetic HTTP receipt", true, new SsisProviderConfigurationCodec().Encode(new("sql.example.test", 1433, "synthetic", false)),
            SecretReference.Environment("SYNTHETIC_HOSTING_LOCATOR"), DateTimeOffset.UtcNow);
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
            db.ProviderInstances.Add(instance);
            await db.SaveChangesAsync(Token);
        }
        var key = Guid.CreateVersion7();
        boundary.Arm(key);
        var path = "/provider-instances/" + instance.Id + "/discovery-refreshes";
        try
        {
            using var request = Request(path, key);
            var pending = client.SendAsync(request, Token);
            await boundary.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.False(pending.IsCompleted);
            await using (var outside = database.CreateServices(null))
            await using (var read = outside.CreateAsyncScope())
                Assert.Equal(0, await read.ServiceProvider.GetRequiredService<FlowPlaneDbContext>().Database
                    .SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM \"ProviderDiscoveryGenerations\"").SingleAsync(Token));
            boundary.Release.TrySetResult();
            using var response = await pending;
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Equal("Accepted", await Status(response));
            // Retry after treating the first response as lost: same durable identity, no second claim.
            using var retry = Request(path, key);
            using var replay = await client.SendAsync(retry, Token);
            Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
            Assert.Equal(response.Headers.Location, replay.Headers.Location);
            await Cycle(api);
            await Cycle(api);
            Assert.Equal(1, native.Scans);
            using var terminal = await client.GetAsync(path + "/" + key, Token);
            Assert.Equal(HttpStatusCode.OK, terminal.StatusCode);
            Assert.Equal("Published", await Status(terminal));
            await using var verify = api.Services.CreateAsyncScope();
            var context = verify.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
            Assert.Equal(1, await context.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM \"ProviderDiscoveryGenerations\"").SingleAsync(Token));
            Assert.Equal(key, await context.Database.SqlQueryRaw<Guid>("SELECT \"CurrentGenerationId\" AS \"Value\" FROM \"ProviderDiscoveryStates\"").SingleAsync(Token));
            foreach (var secret in new[] { "SYNTHETIC_HOSTING_LOCATOR", client.DefaultRequestHeaders.Authorization!.Parameter! })
                Assert.False(api.Logs.Text.Contains(secret, StringComparison.Ordinal));
        }
        finally { boundary.Release.TrySetResult(); }
    }

    private static HttpRequestMessage Request(string path, Guid key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key.ToString());
        return request;
    }

    private static async Task<string?> Status(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain("SYNTHETIC_HOSTING_LOCATOR", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RequestingSubject", text, StringComparison.OrdinalIgnoreCase);
        using var json = JsonDocument.Parse(text);
        return json.RootElement.GetProperty("status").GetString();
    }

    private static async Task Cycle(ProviderApiFactory api)
    {
        var type = typeof(Program).Assembly.GetType("FlowPlane.Api.Providers.DiscoveryHostedRunner")!;
        using var runner = (IDisposable)ActivatorUtilities.CreateInstance(api.Services, type);
        await (Task)type.GetMethod("RunOnceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(runner, [Token])!;
    }

    private sealed class CompleteScan : IProviderDiscoveryCapability
    {
        public int Scans { get; private set; }
        public ProviderType ProviderType => SsisProviderConfigurationCodec.SsisProviderType;
        public Task<ProviderDiscoveryScanResult> ScanAsync(ProviderInstance instance, IProviderDiscoverySink sink, CancellationToken cancellationToken = default)
        {
            Scans++;
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ProviderDiscoveryScanResult(ProviderDiscoveryOutcome.Succeeded, true,
                ProviderDiscoverySourceCompatibility.Compatible, 0, 0, 0, now, now, []));
        }
    }

    private sealed class AdmissionCommitBoundary : DbTransactionInterceptor
    {
        private Guid? key;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Arm(Guid refresh) => key = refresh;
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (key is not { } refresh) return result;
            await using var command = transaction.Connection!.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT count(*) FROM \"ProviderDiscoveryGenerations\" WHERE \"RefreshId\"=@id AND \"Status\"='Accepted'";
            var parameter = command.CreateParameter(); parameter.ParameterName = "id"; parameter.Value = refresh; command.Parameters.Add(parameter);
            if ((long)(await command.ExecuteScalarAsync(cancellationToken))! != 1) return result;
            key = null;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }
}
