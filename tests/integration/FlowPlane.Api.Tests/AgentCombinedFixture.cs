using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

using FlowPlane.Agent.Protocol;
using FlowPlane.Api.Agents;
using FlowPlane.Application.Agents;
using FlowPlane.Application.Providers;
using FlowPlane.Infrastructure.Persistence;
using FlowPlane.Persistence.Tests;
using FlowPlane.Providers.Ssis.Configuration;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using Npgsql;

using AgentSessionReceipt = FlowPlane.Agent.Protocol.AgentSessionReceipt;

namespace FlowPlane.Api.Tests;

// One owned database and a real child application process per test. No authority, service, store or HTTP endpoint doubles.
internal sealed class AgentCombinedFixture : IAsyncDisposable
{
    internal const string Issuer = "https://identity.example.test/realms/isolated-agent";
    internal static CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly PostgreSqlTestDatabase database;
    private readonly ServiceProvider services;
    private readonly DirectoryInfo directory;
    private readonly X509Certificate2 server;
    private readonly RSA signingKey = RSA.Create(2048);
    private readonly List<IDisposable> resources = [];
    private readonly List<string> processOutput = [];
    private Process? process;
    private Task<string>? errors;
    internal Guid ProviderId { get; private set; }
    internal Uri BrowserOrigin { get; private set; } = null!;
    internal Uri AgentOrigin { get; private set; } = null!;
    internal int ProcessId => process!.Id;
    internal Process OwnedProcess => process!;
    internal PostgreSqlTestDatabase OwnedDatabase => database;
    internal string TemporaryDirectory => directory.FullName;
    internal string SecretCanary { get; } = "synthetic-agent-" + Guid.NewGuid().ToString("N");

    private AgentCombinedFixture(PostgreSqlTestDatabase database, ServiceProvider services, DirectoryInfo directory, X509Certificate2 server)
    { this.database = database; this.services = services; this.directory = directory; this.server = server; }

    internal static async Task<AgentCombinedFixture> CreateAsync(Action<PostgreSqlTestDatabase, DirectoryInfo>? afterCertificate = null)
    {
        var database = await PostgreSqlTestDatabase.CreateAsync(Token);
        Assert.SkipWhen(database is null, "Configure separately approved isolated PostgreSQL 18.");
        DirectoryInfo? directory = null;
        ServiceProvider? services = null;
        X509Certificate2? server = null;
        AgentCombinedFixture? fixture = null;
        try
        {
            directory = Directory.CreateTempSubdirectory("flowplane-agent-combined-");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var root = PostgreSqlTestDatabase.CreateRootCertificate(directory.FullName, "disposable-data-protection");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            afterCertificate?.Invoke(database!, directory);
            services = database!.CreateServices(root);
            server = ServerCertificate();
            fixture = new AgentCombinedFixture(database, services, directory, server);
            await database.MigrateAsync(services, Token);
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
            Assert.Equal(new[] { "20261010155624_Baseline", "20261013120748_ProviderConfigurationAndSecretStorage",
                "20261020082907_CurrentDiscoveryInventory", "20261026084710_DurableDiscoveryHosting", "20261111081939_AgentControlFoundation" },
                await db.Database.GetAppliedMigrationsAsync(Token));
            var provider = await scope.ServiceProvider.GetRequiredService<IProviderInstanceStore>().CreateWithManagedSecretAsync(
                Guid.NewGuid(), SsisProviderConfigurationCodec.SsisProviderType, "Isolated synthetic provider", true,
                new SsisProviderConfigurationCodec().Encode(new("sql.example.test", 1433, "synthetic", false)),
                fixture.SecretCanary.AsMemory(), DateTimeOffset.UtcNow, Token);
            fixture.ProviderId = provider.Id;
            await fixture.StartAsync();
            return fixture;
        }
        catch
        {
            if (fixture is not null) await fixture.DisposeAsync();
            else
            {
                try
                {
                    server?.Dispose();
                    if (services is not null) await services.DisposeAsync();
                }
                finally
                {
                    try { if (directory is not null) RemovePrivateCertificate(directory); }
                    finally { await database!.DisposeAsync(); }
                }
            }
            throw;
        }
    }

    private static X509Certificate2 ServerCertificate()
    {
        using var key = RSA.Create(3072);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
    }

    private async Task StartAsync()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "FlowPlane.slnx"))) root = root.Parent;
        var configuration = typeof(AgentCombinedFixture).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
            .Cast<System.Reflection.AssemblyConfigurationAttribute>().Single().Configuration;
        var host = Path.Combine(root!.FullName, "tests/integration/FlowPlane.Agent.IntegrationHost/bin", configuration, "net10.0/FlowPlane.Agent.IntegrationHost.dll");
        Assert.True(File.Exists(host), "Build the test-only integration host before running combined acceptance.");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = directory.FullName
        };
        start.ArgumentList.Add(host);
        start.Environment.Clear();
        start.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH");
        start.Environment["DOTNET_ROOT"] = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        start.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
        process = Process.Start(start) ?? throw new InvalidOperationException("Isolated host process did not start.");
        errors = process.StandardError.ReadToEndAsync();
        var identity = database.Identity;
        var pfx = server.Export(X509ContentType.Pfx);
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                database.ConnectionString,
                RunId = PostgreSqlTestDatabase.InvocationId,
                identity.Name,
                identity.Oid,
                identity.Owner,
                identity.Marker,
                ServerPfx = Convert.ToBase64String(pfx),
                SigningPublicKey = Convert.ToBase64String(signingKey.ExportSubjectPublicKeyInfo()),
                Issuer
            }));
        }
        finally { CryptographicOperations.ZeroMemory(pfx); }
        var line = await process.StandardOutput.ReadLineAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(30), Token);
        Assert.NotNull(line);
        using var receipt = JsonDocument.Parse(line);
        Assert.Equal(process.Id, receipt.RootElement.GetProperty("processId").GetInt32());
        Assert.Equal(configuration, receipt.RootElement.GetProperty("configuration").GetString());
        var addresses = receipt.RootElement.GetProperty("addresses").EnumerateArray().Select(value => new Uri(value.GetString()!)).ToArray();
        Assert.Equal(2, addresses.Length);
        Assert.All(addresses, uri => { Assert.Equal("https", uri.Scheme); Assert.Equal("127.0.0.1", uri.Host); Assert.True(uri.Port > 0); });
        BrowserOrigin = new(addresses[0].AbsoluteUri.Replace("127.0.0.1", "localhost", StringComparison.Ordinal));
        AgentOrigin = new(addresses[1].AbsoluteUri.Replace("127.0.0.1", "localhost", StringComparison.Ordinal));
    }

    internal HttpClient Client(X509Certificate2? certificate = null, bool browser = false, string? subject = null)
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, MaxConnectionsPerServer = 1 };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, supplied, _, errors) => supplied is X509Certificate2 leaf &&
            leaf.RawData.AsSpan().SequenceEqual(server.RawData) &&
            (errors & ~System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors) == 0 &&
            DateTime.UtcNow >= leaf.NotBefore.ToUniversalTime() && DateTime.UtcNow < leaf.NotAfter.ToUniversalTime();
        if (certificate is not null)
        {
            handler.SslOptions.ClientCertificates = new X509CertificateCollection { certificate };
            handler.SslOptions.LocalCertificateSelectionCallback = (_, _, _, _, _) => certificate;
        }
        var client = new HttpClient(handler)
        {
            BaseAddress = browser ? BrowserOrigin : AgentOrigin,
            Timeout = TimeSpan.FromSeconds(15),
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        if (subject is not null)
        {
            var jwt = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Issuer,
                Audience = "flowplane-api",
                Expires = DateTime.UtcNow.AddMinutes(3),
                Subject = new ClaimsIdentity([new Claim("sub", subject)]),
                SigningCredentials = new(new RsaSecurityKey(signingKey) { KeyId = "integration" }, SecurityAlgorithms.RsaSha256)
            });
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        }
        resources.Add(client);
        return client;
    }

    internal X509Certificate2 Certificate()
    {
        using var key = RSA.Create(3072);
        var certificate = AgentBoundaryTests.Certificate(key, "isolated-agent");
        resources.Add(certificate);
        return certificate;
    }

    internal async Task<JsonElement> IssueAsync(HttpClient human, Guid? providerId = null)
    {
        using var response = await human.PostAsJsonAsync("/agents/pairing-grants", new
        {
            providerInstanceId = providerId ?? ProviderId,
            providerVersion = "1",
            clientRequestId = Guid.NewGuid(),
            displayName = "Isolated agent"
        }, Token);
        return await JsonAsync(response);
    }

    internal async Task<EnrolledAgent> EnrollAsync(Guid? providerId = null, X509Certificate2? certificate = null)
    {
        var human = Client(browser: true, subject: "operator");
        var issued = await IssueAsync(human, providerId);
        var cert = certificate ?? Certificate();
        var claim = new PairingClaimRequest("flowplane.agent/pairing-claim.v1", Guid.NewGuid(), issued.GetProperty("grantId").GetGuid(),
            issued.GetProperty("pairingCode").GetString()!, Convert.ToBase64String(cert.RawData));
        var bootstrap = Client();
        var receipt = await PostAsync<PairingClaimRequest, PairingClaimReceipt>(bootstrap, "/agent/v1/pairing-claims", claim);
        var client = Client(cert);
        var proof = new PairingProofRequest("flowplane.agent/pairing-proof.v1", claim.ClaimId, Guid.NewGuid(), receipt.ServerNonce);
        var proven = await PostAsync<PairingProofRequest, PairingStatusReceipt>(client, $"/agent/v1/pairings/{claim.ClaimId}/proof", proof);
        Assert.Equal(PairingStage.Proven, proven.Stage);
        var path = $"/agents/{receipt.AgentId}/pairings/{receipt.GrantId}";
        using var status = await human.GetAsync(path, Token);
        var json = await JsonAsync(status);
        Assert.True(json.GetProperty("verificationPhrase").GetString() == proven.VerificationPhrase);
        using var confirmation = new HttpRequestMessage(HttpMethod.Post, path + "/confirmation")
        { Content = JsonContent.Create(new { verificationPhrase = proven.VerificationPhrase }) };
        confirmation.Headers.TryAddWithoutValidation("If-Match", status.Headers.ETag!.Tag);
        using var confirmed = await human.SendAsync(confirmation, Token);
        Assert.Equal("Redeemed", (await JsonAsync(confirmed)).GetProperty("stage").GetString());
        return new(receipt.AgentId, receipt.GrantId, claim.ClaimId, cert, client, human);
    }

    internal static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(Token));
        return document.RootElement.Clone();
    }

    internal static async Task<TReceipt> PostAsync<TRequest, TReceipt>(HttpClient client, string path, TRequest request)
        where TRequest : class where TReceipt : class
    {
        using var response = await SendAsync(client, path, request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return AgentProtocolCodec.Decode<TReceipt>(await response.Content.ReadAsByteArrayAsync(Token));
    }

    internal static Task<HttpResponseMessage> SendAsync<T>(HttpClient client, string path, T request) where T : class
    {
        var content = new ByteArrayContent(AgentProtocolCodec.Encode(request));
        content.Headers.ContentType = new("application/json");
        return client.PostAsync(path, content, Token);
    }

    internal static async Task<AgentSessionReceipt> ConnectAsync(HttpClient client, AgentConnectRequest? request = null)
    {
        if (request is null)
        {
            using var response = await client.GetAsync("/agent/v1/session", Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var status = AgentProtocolCodec.Decode<AgentSessionStatusReceipt>(await response.Content.ReadAsByteArrayAsync(Token));
            request = new("flowplane.agent/connect.v1", Guid.NewGuid(), Guid.NewGuid(), "0.1.0", 1, status.Fence, AgentPairingSecurity.RecoveryDigest(0, false));
        }
        return await PostAsync<AgentConnectRequest, AgentSessionReceipt>(client, "/agent/v1/sessions", request);
    }

    internal async Task ChangeProviderAsync(bool credential)
    {
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderInstanceStore>();
        var provider = (await store.FindAsync(ProviderId, Token))!;
        if (credential) await store.ReplaceManagedSecretAsync(ProviderId, provider.Version, "synthetic-replacement".AsMemory(), DateTimeOffset.UtcNow, Token);
        else await store.UpdateAsync(ProviderId, provider.Version, "Changed", true, provider.Configuration, DateTimeOffset.UtcNow, Token);
    }

    internal async Task ChangeOnlyCredentialVersionAsync()
    {
        await using var scope = services.CreateAsyncScope();
        var provider = (await scope.ServiceProvider.GetRequiredService<IProviderInstanceStore>().FindAsync(ProviderId, Token))!;
        Assert.True(provider.SecretReference.TryGetFlowPlaneId(out var secretId));
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"FlowPlaneSecrets\" SET \"Version\"=\"Version\"+1, \"UpdatedAtUtc\"=clock_timestamp() WHERE \"Id\"={secretId}", Token));
    }

    internal async Task<Guid> AddProviderAsync()
    {
        await using var scope = services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IProviderInstanceStore>().CreateWithManagedSecretAsync(
            Guid.NewGuid(), SsisProviderConfigurationCodec.SsisProviderType, "Other isolated provider", true,
            new SsisProviderConfigurationCodec().Encode(new("sql.example.test", 1433, "synthetic", false)),
            SecretCanary.AsMemory(), DateTimeOffset.UtcNow, Token)).Id;
    }

    internal async Task RevokeWithoutPinRefreshAsync(Guid agentId)
    {
        await using var scope = services.CreateAsyncScope();
        var health = (await scope.ServiceProvider.GetRequiredService<IAgentControlReader>().ReadHealthAsync(agentId, Token))!;
        await scope.ServiceProvider.GetRequiredService<IAgentControlStore>().RevokeAsync(
            new(agentId, new(Issuer, "operator"), health.Version), Token);
    }

    internal async Task ExecuteAsync(string sql)
    {
        PostgreSqlTestDatabase.RequireCurrentRunDatabase(database.DatabaseName);
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Token);
    }

    internal async Task RestartAsync()
    {
        Assert.NotNull(process);
        await StopProcessAsync(abrupt: true);
        await StartAsync();
    }

    private async Task CollectAsync()
    {
        processOutput.Add(await process!.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        processOutput.Add(await errors!.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.All(processOutput, text => Assert.False(text.Contains(SecretCanary, StringComparison.Ordinal), "Sensitive process output detected."));
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopProcessAsync(abrupt: false); }
        finally
        {
            try
            {
                foreach (var resource in resources) resource.Dispose();
                signingKey.Dispose(); server.Dispose();
                await services.DisposeAsync();
            }
            finally
            {
                try { RemovePrivateCertificate(directory); }
                finally { await database.DisposeAsync(); }
            }
        }
    }

    private async Task StopProcessAsync(bool abrupt)
    {
        if (process is null) return;
        var owned = process;
        try
        {
            if (!owned.HasExited)
            {
                if (abrupt) owned.Kill(entireProcessTree: true);
                else
                {
                    try { await owned.StandardInput.WriteLineAsync("stop").WaitAsync(TimeSpan.FromSeconds(2)); }
                    catch (Exception exception) when (exception is IOException or ObjectDisposedException or TimeoutException) { }
                }
                try { await owned.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (TimeoutException)
                {
                    if (!owned.HasExited) owned.Kill(entireProcessTree: true);
                    await owned.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
            await CollectAsync();
        }
        finally
        {
            try
            {
                if (!owned.HasExited)
                {
                    owned.Kill(entireProcessTree: true);
                    await owned.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
            finally { owned.Dispose(); process = null; }
        }
    }

    private static void RemovePrivateCertificate(DirectoryInfo ownedDirectory)
    {
        var path = Path.Combine(ownedDirectory.FullName, "disposable-data-protection.pfx");
        if (File.Exists(path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Unexpected certificate identity; resource preserved.");
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            { stream.Write(new byte[stream.Length]); stream.Flush(true); }
            File.Delete(path);
        }
        ownedDirectory.Delete(); // Deliberately nonrecursive: preserve unexpected contents and still attempt guarded DB cleanup.
    }

    internal sealed record EnrolledAgent(Guid Id, Guid GrantId, Guid ClaimId, X509Certificate2 Certificate, HttpClient Client, HttpClient Human);
}
