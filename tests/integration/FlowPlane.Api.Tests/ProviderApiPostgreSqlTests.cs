using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FlowPlane.Application.Providers;
using FlowPlane.Application.Secrets;
using FlowPlane.Infrastructure.Persistence;
using FlowPlane.Persistence.Tests;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

namespace FlowPlane.Api.Tests;

public sealed class ProviderApiPostgreSqlTests
{
    [Fact]
    public async Task Host_change_and_password_rotation_during_validation_cannot_send_new_password_to_old_host()
    {
        await using var database = await DatabaseAsync();
        var directory = Directory.CreateTempSubdirectory("flowplane-a12-").FullName;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = new ValidationCapture();
        try
        {
            var certificate = PostgreSqlTestDatabase.CreateRootCertificate(directory, "disposable");
            await using (var services = database.CreateServices(certificate)) await database.MigrateAsync(services, Token);
            using var api = new ProviderApiFactory(settings: new Dictionary<string, string?>
            {
                ["ConnectionStrings:FlowPlane"] = database.ConnectionString,
                ["FlowPlane:Secrets:RootCertificatePath"] = certificate,
                ["FlowPlane:Ssis:AllowedEndpoints:1:Host"] = "other.example.test",
                ["FlowPlane:Ssis:AllowedEndpoints:1:Port"] = "1433",
            }, configureServices: services =>
            {
                var resolver = services.Last(descriptor => descriptor.ServiceType == typeof(ISecretResolver));
                services.Remove(resolver);
                services.AddScoped<ISecretResolver>(provider => new PausingResolver(
                    (ISecretResolver)ActivatorUtilities.CreateInstance(provider, resolver.ImplementationType!), reached, resume));
                var validator = services.Last(descriptor => descriptor.ServiceType == typeof(IProviderInstanceValidationCapability));
                services.Remove(validator);
                services.AddScoped<IProviderInstanceValidationCapability>(provider => new CapturedValidator(
                    (IProviderInstanceValidationCapability)ActivatorUtilities.CreateInstance(provider, validator.ImplementationType!), capture));
            });
            using var client = api.Client("operator");
            using var created = await client.PostAsJsonAsync("/provider-instances", CreateInput("synthetic-old-host-password"), Token);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Token));
            var path = "/provider-instances/" + document.RootElement.GetProperty("id").GetGuid();
            var validating = client.PostAsync(path + "/validation", null, Token);
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            using var changed = await PutAsync(client, path, ConfigurationInput("Changed host", "other.example.test"), "\"1\"");
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            using var rotated = await PutAsync(client, path + "/credential", new { password = "synthetic-new-host-password" }, "\"2\"");
            Assert.Equal(HttpStatusCode.NoContent, rotated.StatusCode);
            resume.TrySetResult();
            using var result = await validating;
            Assert.Equal(HttpStatusCode.Conflict, result.StatusCode);
            Assert.NotNull(capture.Result);
            Assert.Contains(capture.Result.Checks, check => check.Code == "ssis.configuration.context-changed");
            AssertSafe(await result.Content.ReadAsStringAsync(Token), "synthetic-new-host-password", "synthetic-old-host-password");
        }
        finally { resume.TrySetResult(); Directory.Delete(directory, true); }
    }

    private sealed class ValidationCapture
    {
        public ProviderValidationResult? Result { get; set; }
    }

    private sealed class CapturedValidator(IProviderInstanceValidationCapability inner, ValidationCapture capture)
        : IProviderInstanceValidationCapability
    {
        public FlowPlane.Domain.Providers.ProviderType ProviderType => inner.ProviderType;
        public async Task<ProviderValidationResult> ValidateAsync(FlowPlane.Domain.Providers.ProviderInstance instance,
            CancellationToken cancellationToken = default,
            Func<CancellationToken, ValueTask<bool>>? contextIsCurrent = null)
        {
            var result = await inner.ValidateAsync(instance, cancellationToken, contextIsCurrent);
            capture.Result = result;
            return result;
        }
    }

    private sealed class PausingResolver(ISecretResolver inner, TaskCompletionSource reached, TaskCompletionSource resume)
        : ISecretResolver
    {
        public async ValueTask<T> UseAsync<T>(FlowPlane.Domain.Providers.SecretReference reference,
            Func<ReadOnlyMemory<char>, CancellationToken, ValueTask<T>> use, CancellationToken cancellationToken = default)
        {
            reached.TrySetResult();
            await resume.Task.WaitAsync(cancellationToken);
            return await inner.UseAsync(reference, use, cancellationToken);
        }
    }

    [Fact]
    public async Task Operator_can_create_read_update_and_replace_credentials_without_disclosing_them()
    {
        await using var database = await DatabaseAsync();
        var directory = Directory.CreateTempSubdirectory("flowplane-a12-").FullName;
        try
        {
            var certificate = PostgreSqlTestDatabase.CreateRootCertificate(directory, "disposable");
            await using (var services = database.CreateServices(certificate))
                await database.MigrateAsync(services, TestContext.Current.CancellationToken);
            using var api = Factory(database, certificate);
            using var client = api.Client("operator");
            var first = "synthetic-a12-" + Guid.NewGuid().ToString("N");
            var second = "synthetic-a12-" + Guid.NewGuid().ToString("N");
            using var created = await client.PostAsJsonAsync("/provider-instances", CreateInput(first), Token);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.Equal("\"1\"", created.Headers.ETag?.Tag);
            var text = await created.Content.ReadAsStringAsync(Token);
            AssertSafe(text, first, second);
            var id = JsonDocument.Parse(text).RootElement.GetProperty("id").GetGuid();
            var path = "/provider-instances/" + id;
            using var scope = api.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
            var original = await context.ProviderInstances.AsNoTracking().SingleAsync(Token);
            Assert.Equal(1, original.Version);
            Assert.Equal(first, await ResolveAsync(scope.ServiceProvider, original.SecretReference));

            using var get = await client.GetAsync(path, Token);
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            Assert.Equal("\"1\"", get.Headers.ETag?.Tag);
            AssertSafe(await get.Content.ReadAsStringAsync(Token), first, second);
            using var listed = await client.GetAsync("/provider-instances?limit=1&offset=0", Token);
            Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
            AssertSafe(await listed.Content.ReadAsStringAsync(Token), first, second);

            using var update = await PutAsync(client, path, ConfigurationInput("Changed"), "\"1\"");
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
            Assert.Equal("\"2\"", update.Headers.ETag?.Tag);
            using var stale = await PutAsync(client, path, ConfigurationInput("Obsolete"), "\"1\"");
            Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
            using var replaced = await PutAsync(client, path + "/credential", new { password = second }, "\"2\"");
            Assert.Equal(HttpStatusCode.NoContent, replaced.StatusCode);
            Assert.Equal("\"3\"", replaced.Headers.ETag?.Tag);
            using var staleCredential = await PutAsync(client, path + "/credential", new { password = first }, "\"2\"");
            Assert.Equal(HttpStatusCode.PreconditionFailed, staleCredential.StatusCode);
            var current = await context.ProviderInstances.AsNoTracking().SingleAsync(Token);
            Assert.True(original.SecretReference.Equals(current.SecretReference));
            Assert.Equal(second, await ResolveAsync(scope.ServiceProvider, current.SecretReference));

            var updates = await Task.WhenAll(PutAsync(client, path, ConfigurationInput("One"), "\"3\""),
                PutAsync(client, path, ConfigurationInput("Two"), "\"3\""));
            try
            {
                Assert.Single(updates, response => response.StatusCode == HttpStatusCode.OK);
                Assert.Single(updates, response => response.StatusCode == HttpStatusCode.PreconditionFailed);
            }
            finally { foreach (var response in updates) response.Dispose(); }
            Assert.Equal(4, (await context.ProviderInstances.AsNoTracking().SingleAsync(Token)).Version);
            await AssertDatabaseRedactedAsync(database.ConnectionString, first, second);
            foreach (var value in new[] { first, second, database.ConnectionString,
                         original.SecretReference.ToCanonicalString(), client.DefaultRequestHeaders.Authorization!.Parameter! })
                Assert.False(api.Logs.Text.Contains(value, StringComparison.Ordinal));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Failed_provider_insert_rolls_back_the_owned_secret()
    {
        await using var database = await DatabaseAsync();
        var directory = Directory.CreateTempSubdirectory("flowplane-a12-").FullName;
        try
        {
            var certificate = PostgreSqlTestDatabase.CreateRootCertificate(directory, "disposable");
            await using var services = database.CreateServices(certificate);
            await database.MigrateAsync(services, Token);
            await using (var connection = new NpgsqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync(Token);
                await using var command = connection.CreateCommand();
                command.CommandText = "ALTER TABLE \"ProviderInstances\" ADD CONSTRAINT test_reject_insert CHECK (false)";
                await command.ExecuteNonQueryAsync(Token);
            }
            using var api = Factory(database, certificate);
            using var client = api.Client("operator");
            using var response = await client.PostAsJsonAsync("/provider-instances", CreateInput("synthetic-rollback-input"), Token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            await AssertNoProviderOrSecretAsync(database.ConnectionString);
            AssertSafe(await response.Content.ReadAsStringAsync(Token), "synthetic-rollback-input");
            Assert.False(api.Logs.Text.Contains("synthetic-rollback-input", StringComparison.Ordinal));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Missing_root_protection_returns_safe_503_and_persists_no_credential()
    {
        await using var database = await DatabaseAsync();
        await using var services = database.CreateServices(null);
        await database.MigrateAsync(services, Token);
        using var api = Factory(database, "/nonexistent/a12-disposable-root.pfx");
        using var client = api.Client("operator");
        using var response = await client.PostAsJsonAsync("/provider-instances", CreateInput("synthetic-missing-root-input"), Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertSafe(await response.Content.ReadAsStringAsync(Token), "synthetic-missing-root-input");
        await AssertNoProviderOrSecretAsync(database.ConnectionString);
    }

    [Fact]
    public async Task Validation_uses_the_current_provider_and_returns_safe_structured_checks()
    {
        await using var database = await DatabaseAsync();
        await using var services = database.CreateServices(null);
        await database.MigrateAsync(services, Token);
        using var scope = services.CreateScope();
        var codec = new FlowPlane.Providers.Ssis.Configuration.SsisProviderConfigurationCodec();
        var instance = FlowPlane.Domain.Providers.ProviderInstance.Create(Guid.CreateVersion7(),
            FlowPlane.Providers.Ssis.Configuration.SsisProviderConfigurationCodec.SsisProviderType, "Validation", true,
            codec.DecodeInput("ssis.provider-instance/v1", "{\"host\":\"sql.example.test\",\"username\":\"test\"}"),
            FlowPlane.Domain.Providers.SecretReference.Environment("FLOWPLANE_A12_NOT_CONFIGURED"), DateTimeOffset.UtcNow);
        var context = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        context.ProviderInstances.Add(instance);
        await context.SaveChangesAsync(Token);
        using var api = Factory(database, "/nonexistent/a12-disposable-root.pfx");
        using var client = api.Client("operator");
        using var response = await client.PostAsync("/provider-instances/" + instance.Id + "/validation", null, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(Token);
        Assert.Contains("ssis.secret.unavailable", text, StringComparison.Ordinal);
        AssertSafe(text, "FLOWPLANE_A12_NOT_CONFIGURED");
        Assert.Equal(1, (await context.ProviderInstances.AsNoTracking().SingleAsync(Token)).Version);
        using var deniedApi = new ProviderApiFactory(settings: new Dictionary<string, string?>
        {
            ["ConnectionStrings:FlowPlane"] = database.ConnectionString,
            ["FlowPlane:Ssis:AllowedEndpoints:0:Host"] = "different.example.test",
            ["FlowPlane:Secrets:RootCertificatePath"] = "/nonexistent/a12-disposable-root.pfx",
        });
        using var deniedClient = deniedApi.Client("operator");
        using var denied = await deniedClient.PostAsync("/provider-instances/" + instance.Id + "/validation", null, Token);
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.Contains("ProviderEndpointDenied", await denied.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Unrecognized_persisted_provider_returns_safe_problem_during_list_serialization()
    {
        await using var database = await DatabaseAsync();
        await using var services = database.CreateServices(null);
        await database.MigrateAsync(services, Token);
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        var codec = new FlowPlane.Providers.Ssis.Configuration.SsisProviderConfigurationCodec();
        context.ProviderInstances.Add(FlowPlane.Domain.Providers.ProviderInstance.Create(Guid.CreateVersion7(),
            FlowPlane.Domain.Providers.ProviderType.Parse("unsupported"), "Unrecognized", true,
            codec.DecodeInput("ssis.provider-instance/v1", "{\"host\":\"sql.example.test\",\"username\":\"test\"}"),
            FlowPlane.Domain.Providers.SecretReference.Environment("FLOWPLANE_A12_NOT_CONFIGURED"), DateTimeOffset.UtcNow));
        await context.SaveChangesAsync(Token);
        using var api = Factory(database, "/nonexistent/a12-disposable-root.pfx");
        using var client = api.Client("operator");
        using var response = await client.GetAsync("/provider-instances", Token);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertSafe(await response.Content.ReadAsStringAsync(Token), "FLOWPLANE_A12_NOT_CONFIGURED");
    }

    private static async Task<PostgreSqlTestDatabase> DatabaseAsync()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("FLOWPLANE_TEST_DISCOVERY_MIGRATION_APPROVED") == "true", "Isolated migration approval required.");
        var database = await PostgreSqlTestDatabase.CreateAsync(Token);
        Assert.SkipWhen(database is null, "Configure isolated PostgreSQL integration tests.");
        return database!;
    }

    private static ProviderApiFactory Factory(PostgreSqlTestDatabase database, string certificate) => new(settings:
        new Dictionary<string, string?>
        {
            ["ConnectionStrings:FlowPlane"] = database.ConnectionString,
            ["FlowPlane:Secrets:RootCertificatePath"] = certificate
        });

    private static object ConfigurationInput(string displayName = "Synthetic", string host = "sql.example.test") => new
    {
        providerType = "ssis",
        displayName,
        isEnabled = true,
        configuration = new { schemaId = "ssis.provider-instance/v1", data = new { host, username = "test", port = 1433 } },
    };

    private static object CreateInput(string password) => new
    {
        providerType = "ssis",
        displayName = "Synthetic",
        isEnabled = true,
        configuration = new { schemaId = "ssis.provider-instance/v1", data = new { host = "sql.example.test", username = "test", port = 1433 } },
        credential = new { password },
    };

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, string path, object input, string etag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = JsonContent.Create(input) };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await client.SendAsync(request, Token);
    }

    private static void AssertSafe(string text, params string[] canaries)
    {
        foreach (var canary in canaries) Assert.False(text.Contains(canary, StringComparison.Ordinal));
        foreach (var forbidden in new[] { "secretReference", "flowplane:", "env:", "Password=", "ConnectionString" })
            Assert.False(text.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string> ResolveAsync(IServiceProvider services, FlowPlane.Domain.Providers.SecretReference reference) =>
        await services.GetRequiredService<ISecretResolver>().UseAsync(reference,
            (value, _) => ValueTask.FromResult(new string(value.Span)), Token);

    private static async Task AssertNoProviderOrSecretAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT count(*) FROM \"ProviderInstances\") + (SELECT count(*) FROM \"FlowPlaneSecrets\")";
        Assert.Equal(0L, await command.ExecuteScalarAsync(Token));
    }

    private static async Task AssertDatabaseRedactedAsync(string connectionString, params string[] canaries)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);
        foreach (var canary in canaries)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT EXISTS (SELECT 1 FROM "FlowPlaneSecrets" WHERE position(convert_to(@canary,'UTF8') in "Ciphertext")>0)
                OR EXISTS (SELECT 1 FROM "ProviderInstances" p WHERE position(@canary in row_to_json(p)::text)>0)
                OR EXISTS (SELECT 1 FROM "DataProtectionKeys" WHERE position(@canary in "Xml")>0)
                """;
            command.Parameters.AddWithValue("canary", canary);
            Assert.False((bool)(await command.ExecuteScalarAsync(Token))!);
        }
    }
}
