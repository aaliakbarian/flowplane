using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using FlowPlane.Api.Agents;
using FlowPlane.Agent.Protocol;
using FlowPlane.Agent.Runtime.Transport;
using FlowPlane.Agent.Runtime.Control;
using FlowPlane.Agent.Runtime.Journal;
using FlowPlane.Agent.Runtime.Setup;
using FlowPlane.Application.Agents;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace FlowPlane.Api.Tests;

// Real loopback Kestrel/TLS and certificate handler; lifecycle storage is a current-authority test double.
public sealed class AgentTlsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transient_authority_read_failure_preserves_pending_poll_and_recovers_without_reenrollment(bool cancelled)
    {
        await using var host = await TlsHost.StartAsync(guarded: true);
        var directory = Directory.CreateTempSubdirectory("flowplane-tls-authority-");
        try
        {
            var installation = Guid.NewGuid();
            var path = Path.Combine(directory.FullName, "control.state");
            var claim = new PairingClaimReceipt("flowplane.agent/pairing-claim-receipt.v1", Guid.NewGuid(), Guid.NewGuid(), host.Reader.AgentId,
                Convert.ToHexStringLower(SHA256.HashData(host.AgentCertificate.RawData)), new string('A', 43), DateTimeOffset.UtcNow.AddMinutes(10), PairingStage.Claimed, 1);
            var status = new PairingStatusReceipt("flowplane.agent/pairing-status.v1", claim.GrantId, claim.ClaimId, PairingStage.Redeemed, claim.ExpiresAtUtc,
                AgentPairingSecurity.VerificationPhrase(claim.GrantId, claim.ClaimId, claim.PinSha256, claim.ServerNonce), 3);
            using (var store = AgentControlStore.Open(path, installation, true))
                store.Save(store.State with { Origin = host.AgentOrigin.AbsoluteUri, Enrollment = new(claim.GrantId, claim.ClaimId, Guid.NewGuid(), claim.PinSha256, claim, status) });
            using var journal = AgentOperationJournal.Open(Path.Combine(directory.FullName, "operations.journal"), installation, true);
            using var local = new AgentControlHost(path, installation, host.AgentCertificate, journal,
                (origin, certificate) => AgentHttpsTransport.CreateWithCustomRoot(origin, certificate, host.ServerCertificate));
            await local.StepAsync(TestContext.Current.CancellationToken);
            await local.StepAsync(TestContext.Current.CancellationToken);
            host.Reader.PauseRead = _ => throw (cancelled ? new OperationCanceledException() : new AgentStoreException(AgentStoreError.Unavailable));
            await local.StepAsync(TestContext.Current.CancellationToken);
            var pending = local.State!.PendingPoll ?? throw new InvalidOperationException("pending poll evidence lost");
            Assert.Single(host.Reader.Polls);
            Assert.Equal(LocalSetupStatus.Paired, (await local.StatusAsync(TestContext.Current.CancellationToken)).Status);
            await local.StepAsync(TestContext.Current.CancellationToken);
            Assert.Null(local.State.PendingPoll);
            Assert.Equal(2, local.State.LatestPoll!.Sequence);
            Assert.Equal(pending, host.Reader.Polls[1]);
            Assert.Equal(LocalSetupStatus.Paired, (await local.StatusAsync(TestContext.Current.CancellationToken)).Status);
            Assert.Single(host.ConnectionIds.Distinct(StringComparer.Ordinal));
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancelled")]
    public async Task Continuing_connection_pin_refresh_is_retryable_and_same_pending_poll_resumes(string outcome)
    {
        await using var host = await TlsHost.StartAsync(guarded: true);
        var directory = Directory.CreateTempSubdirectory("flowplane-tls-refresh-");
        try
        {
            var installation = Guid.NewGuid();
            var path = Path.Combine(directory.FullName, "control.state");
            var claim = new PairingClaimReceipt("flowplane.agent/pairing-claim-receipt.v1", Guid.NewGuid(), Guid.NewGuid(), host.Reader.AgentId,
                Convert.ToHexStringLower(SHA256.HashData(host.AgentCertificate.RawData)), new string('A', 43), DateTimeOffset.UtcNow.AddMinutes(10), PairingStage.Claimed, 1);
            var status = new PairingStatusReceipt("flowplane.agent/pairing-status.v1", claim.GrantId, claim.ClaimId, PairingStage.Redeemed, claim.ExpiresAtUtc,
                AgentPairingSecurity.VerificationPhrase(claim.GrantId, claim.ClaimId, claim.PinSha256, claim.ServerNonce), 3);
            using (var store = AgentControlStore.Open(path, installation, true))
                store.Save(store.State with { Origin = host.AgentOrigin.AbsoluteUri, Enrollment = new(claim.GrantId, claim.ClaimId, Guid.NewGuid(), claim.PinSha256, claim, status) });
            using var journal = AgentOperationJournal.Open(Path.Combine(directory.FullName, "operations.journal"), installation, true);
            AgentHttpsTransport? transport = null;
            using var local = new AgentControlHost(path, installation, host.AgentCertificate, journal,
                (origin, certificate) => transport = AgentHttpsTransport.CreateWithCustomRoot(origin, certificate, host.ServerCertificate));
            await local.StepAsync(TestContext.Current.CancellationToken); // Session and connect establish TLS.
            await local.StepAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, local.State!.LatestPoll!.Sequence);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource();
            var refresh = host.Pins.RefreshAsync(async token =>
            {
                entered.SetResult(); await release.Task.WaitAsync(token);
                if (outcome == "failure") throw new IOException("synthetic-refresh-failure");
                return [new(Guid.NewGuid(), Guid.NewGuid(), host.AgentCertificate.RawData)];
            }, cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            try
            {
                await local.StepAsync(TestContext.Current.CancellationToken);
                var pending = local.State.PendingPoll ?? throw new InvalidOperationException("pending poll evidence lost");
                Assert.Equal(LocalSetupStatus.Paired, (await local.StatusAsync(TestContext.Current.CancellationToken)).Status);
                var unavailable = await Assert.ThrowsAsync<AgentTransportException>(() => transport!.SessionStatusAsync(TestContext.Current.CancellationToken));
                Assert.Equal(AgentTransportError.Unavailable, unavailable.Code);
                Assert.Single(host.Reader.Polls);
                if (outcome == "cancelled") cancellation.Cancel();
                release.TrySetResult();
                if (outcome == "success") await refresh;
                else if (outcome == "failure") await Assert.ThrowsAsync<IOException>(() => refresh);
                else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
                if (outcome != "success") await host.Pins.RefreshAsync(_ => Task.FromResult<IReadOnlyList<AgentPublicPin>>([new(Guid.NewGuid(), Guid.NewGuid(), host.AgentCertificate.RawData)]), TestContext.Current.CancellationToken);
                await local.StepAsync(TestContext.Current.CancellationToken);
                Assert.Null(local.State.PendingPoll);
                Assert.Equal(2, local.State.LatestPoll!.Sequence);
                Assert.Equal(pending, host.Reader.Polls[1]);
                Assert.Equal(LocalSetupStatus.Paired, (await local.StatusAsync(TestContext.Current.CancellationToken)).Status);
                Assert.Single(host.ConnectionIds.Distinct(StringComparer.Ordinal));
            }
            finally { release.TrySetResult(); try { await refresh; } catch (Exception exception) when (exception is IOException or OperationCanceledException) { } }
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_starting_during_certificate_authority_read_never_grants_authority_or_permanently_denies(bool completesBeforeAuthentication)
    {
        await using var host = await TlsHost.StartAsync(guarded: true);
        using var transport = AgentHttpsTransport.CreateWithCustomRoot(host.AgentOrigin.AbsoluteUri, host.AgentCertificate, host.ServerCertificate);
        await transport.SessionStatusAsync(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAuth = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Reader.PauseRead = async token => { entered.SetResult(); await releaseAuth.Task.WaitAsync(token); };
        var request = transport.SessionStatusAsync(TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var refresh = host.Pins.RefreshAsync(async token => { await releaseRefresh.Task.WaitAsync(token); return [new(Guid.NewGuid(), Guid.NewGuid(), host.AgentCertificate.RawData)]; }, TestContext.Current.CancellationToken);
        try
        {
            if (completesBeforeAuthentication) { releaseRefresh.SetResult(); await refresh; }
            releaseAuth.SetResult();
            var unavailable = await Assert.ThrowsAsync<AgentTransportException>(() => request);
            Assert.Equal(AgentTransportError.Unavailable, unavailable.Code);
        }
        finally { releaseAuth.TrySetResult(); releaseRefresh.TrySetResult(); await refresh; }
        Assert.Equal(host.Reader.AgentId, (await transport.SessionStatusAsync(TestContext.Current.CancellationToken)).AgentId);
        Assert.Single(host.ConnectionIds.Distinct(StringComparer.Ordinal));
    }
    [Fact]
    public async Task Actual_agent_transport_checks_server_trust_SAN_and_current_client_authority()
    {
        await using var host = await TlsHost.StartAsync();
        using var transport = AgentHttpsTransport.CreateWithCustomRoot(host.AgentOrigin.AbsoluteUri, host.AgentCertificate, host.ServerCertificate);
        var receipt = await transport.SessionStatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal(host.Reader.AgentId, receipt.AgentId);
        host.Reader.Revoked = true;
        var revoked = await Assert.ThrowsAsync<AgentTransportException>(() => transport.SessionStatusAsync(TestContext.Current.CancellationToken));
        Assert.Equal(AgentTransportError.Denied, revoked.Code);
        using var wrongName = AgentHttpsTransport.CreateWithCustomRoot(host.AgentOrigin.AbsoluteUri.Replace("localhost", "127.0.0.1", StringComparison.Ordinal), host.AgentCertificate, host.ServerCertificate);
        var mismatch = await Assert.ThrowsAsync<AgentTransportException>(() => wrongName.SessionStatusAsync(TestContext.Current.CancellationToken));
        Assert.Equal(AgentTransportError.Unavailable, mismatch.Code);
    }
    [Fact]
    public async Task Current_database_revocation_denies_next_request_on_an_already_authenticated_TLS_connection()
    {
        await using var host = await TlsHost.StartAsync();
        using var client = host.Client(host.AgentCertificate);
        using var first = await client.GetAsync(new Uri(host.AgentOrigin, "/agent/v1/probe"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        host.Reader.Revoked = true; // Leave handshake snapshot unchanged, proving DB is still checked on the next request.
        using var second = await client.GetAsync(new Uri(host.AgentOrigin, "/agent/v1/probe"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
        Assert.True(host.Reader.Reads >= 2);
        Assert.Equal(2, host.ConnectionIds.Length);
        Assert.Equal(host.ConnectionIds[0], host.ConnectionIds[1]);
    }

    [Theory]
    [InlineData("same-key-different-der")]
    [InlineData("no-eku")]
    [InlineData("expired")]
    public async Task Actual_handshake_rejects_unregistered_DER_and_registered_invalid_client_profiles(string profile)
    {
        await using var host = await TlsHost.StartAsync();
        using var key = host.AgentCertificate.GetRSAPrivateKey()!;
        var request = new CertificateRequest("CN=agent", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        if (profile != "no-eku") request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, true));
        using var certificate = profile == "expired"
            ? request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(-1))
            : request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        if (profile != "same-key-different-der") host.Pins.Replace([new(Guid.NewGuid(), Guid.NewGuid(), certificate.RawData)]);
        using var client = host.Client(certificate);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new Uri(host.AgentOrigin, "/agent/v1/probe"), TestContext.Current.CancellationToken));
        Assert.Equal(0, host.Reader.Reads);
    }

    [Fact]
    public async Task Real_listener_isolation_rejects_forwarded_headers_and_browser_routes_on_agent_port()
    {
        await using var host = await TlsHost.StartAsync();
        using var client = host.Client();
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-Port", host.AgentOrigin.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-SSL-CERT", Convert.ToBase64String(host.AgentCertificate.RawData));
        using var browser = await client.GetAsync(new Uri(host.BrowserOrigin, "/agent/v1/probe"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, browser.StatusCode);
        using var agent = await client.GetAsync(new Uri(host.AgentOrigin, "/me"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, agent.StatusCode);
        using var missing = await client.GetAsync(new Uri(host.AgentOrigin, "/agent/v1/probe"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
        Assert.Equal(0, host.Reader.Reads);
    }

    [Fact]
    public async Task Unknown_or_same_key_different_DER_client_certificate_is_not_trusted()
    {
        await using var host = await TlsHost.StartAsync();
        using var key = RSA.Create(3072);
        const string canary = "certificate-subject-private-canary";
        using var unknown = AgentBoundaryTests.Certificate(key, canary);
        using var client = host.Client(unknown);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new Uri(host.AgentOrigin, "/agent/v1/probe"), TestContext.Current.CancellationToken));
        Assert.Equal(0, host.Reader.Reads);
        Assert.DoesNotContain(canary, host.Logs.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Untrusted_server_certificate_is_rejected_by_normal_TLS_validation()
    {
        await using var host = await TlsHost.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new Uri(host.AgentOrigin, "/agent/v1/probe"), TestContext.Current.CancellationToken));
    }

    internal sealed class TlsHost : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly X509Certificate2 server;
        internal X509Certificate2 ServerCertificate => server;
        internal X509Certificate2 AgentCertificate { get; }
        internal AuthorityReader Reader { get; }
        internal TestLogProvider Logs { get; }
        internal AgentPinRegistry Pins { get; }
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> connections;
        internal string[] ConnectionIds => connections.ToArray();
        internal Uri AgentOrigin { get; }
        internal Uri BrowserOrigin { get; }
        private TlsHost(WebApplication app, X509Certificate2 server, X509Certificate2 agent, AuthorityReader reader, Uri agentOrigin, Uri browserOrigin, TestLogProvider logs,
            AgentPinRegistry pins, System.Collections.Concurrent.ConcurrentQueue<string> connections)
        { this.app = app; this.server = server; AgentCertificate = agent; Reader = reader; AgentOrigin = agentOrigin; BrowserOrigin = browserOrigin; Logs = logs; Pins = pins; this.connections = connections; }

        internal static async Task<TlsHost> StartAsync(bool guarded = false)
        {
            using var key = RSA.Create(3072);
            using var serverKey = RSA.Create(3072);
            var agent = AgentBoundaryTests.Certificate(key, "agent");
            var request = new CertificateRequest("CN=localhost", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); request.CertificateExtensions.Add(names.Build());
            var server = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            var builder = WebApplication.CreateBuilder();
            var logs = new TestLogProvider();
            builder.Logging.ClearProviders().AddProvider(logs);
            builder.AddAgentControl(); // Disabled: no files, migrations, background loaders or external database.
            if (guarded) builder.Services.AddSingleton(new AgentControlOptions { Enabled = true });
            var pins = new AgentPinRegistry(); pins.Replace([new(Guid.NewGuid(), Guid.NewGuid(), agent.RawData)]);
            builder.Services.AddSingleton(pins);
            var reader = new AuthorityReader(agent.RawData);
            builder.Services.AddSingleton<IAgentControlReader>(reader);
            var boundary = new AgentListenerBoundary();
            builder.Services.AddSingleton(boundary);
            // Avoid resolving the production service in this focused real transport/authentication harness.
            builder.Services.RemoveAll<AgentControlService>();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, 0);
                options.Listen(IPAddress.Loopback, 0, listen => AgentControlHosting.ConfigureAgentListener(listen, server, pins, boundary));
            });
            var app = builder.Build();
            var connections = new System.Collections.Concurrent.ConcurrentQueue<string>();
            app.UseAgentControlBoundary();
            if (guarded) app.Use(async (context, next) => { connections.Enqueue(context.Connection.Id); await next(context); });
            app.MapGet("/me", () => "browser");
            if (guarded)
            {
                var routes = app.MapGroup("/agent/v1").AddEndpointFilter(AgentControlApi.GuardAgentAsync);
                routes.MapGet("/session", () => AgentHttp.Wire(new AgentSessionStatusReceipt("flowplane.agent/session-status.v1", reader.AgentId, 1, DateTimeOffset.UtcNow, null, AgentRecoveryMode.None)));
                routes.MapPost("/sessions", async (Microsoft.AspNetCore.Http.HttpContext context) =>
                {
                    var input = AgentProtocolCodec.Decode<AgentConnectRequest>(await AgentHttp.ReadBody(context));
                    var now = DateTimeOffset.UtcNow;
                    return AgentHttp.Wire(new FlowPlane.Agent.Protocol.AgentSessionReceipt("flowplane.agent/session-receipt.v1", reader.AgentId, reader.SessionId, input.BootId, 2, 1, now, now.AddSeconds(60), AgentRecoveryMode.None));
                });
                routes.MapPost("/polls", async (Microsoft.AspNetCore.Http.HttpContext context) =>
                {
                    var input = AgentProtocolCodec.Decode<AgentPollRequest>(await AgentHttp.ReadBody(context));
                    reader.Polls.Add(input);
                    var now = DateTimeOffset.UtcNow;
                    return AgentHttp.Wire(new FlowPlane.Agent.Protocol.AgentPollReceipt("flowplane.agent/poll-receipt.v1", reader.SessionId, 2, input.Sequence, now, now.AddSeconds(60), 5, AgentWorkKind.None));
                });
            }
            else
            {
                app.MapGet("/agent/v1/probe", async (Microsoft.AspNetCore.Http.HttpContext context) =>
                {
                    connections.Enqueue(context.Connection.Id);
                    var authentication = await context.AuthenticateAsync(AgentControlHosting.Scheme);
                    return authentication.Succeeded ? Microsoft.AspNetCore.Http.Results.Ok("authenticated") : AgentHttp.Problem(403, "AgentAuthenticationDenied");
                });
                app.MapGet("/agent/v1/session", async (Microsoft.AspNetCore.Http.HttpContext context) =>
                {
                    var authentication = await context.AuthenticateAsync(AgentControlHosting.Scheme);
                    return authentication.Succeeded ? AgentHttp.Wire(new AgentSessionStatusReceipt("flowplane.agent/session-status.v1",
                        reader.AgentId, 1, DateTimeOffset.UtcNow, null, AgentRecoveryMode.None)) : AgentHttp.Problem(403, "AgentAuthenticationDenied");
                });
            }
            await app.StartAsync(TestContext.Current.CancellationToken);
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Select(address => new Uri(address)).ToArray();
            return new(app, server, agent, reader, new Uri(addresses.Single(uri => uri.Scheme == "https").ToString().Replace("127.0.0.1", "localhost", StringComparison.Ordinal)),
                addresses.Single(uri => uri.Scheme == "http"), logs, pins, connections);
        }

        internal HttpClient Client(X509Certificate2? certificate = null)
        {
            var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
            // Exact ephemeral server pin in this harness only, still requiring platform name/validity verification.
            handler.SslOptions.RemoteCertificateValidationCallback = (_, presented, _, errors) => presented is X509Certificate2 supplied &&
                supplied.RawData.AsSpan().SequenceEqual(server.RawData) &&
                (errors & ~System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors) == 0 &&
                DateTime.UtcNow >= supplied.NotBefore.ToUniversalTime() && DateTime.UtcNow < supplied.NotAfter.ToUniversalTime();
            if (certificate is not null)
            {
                handler.SslOptions.ClientCertificates = new X509CertificateCollection { certificate };
                // Force the selected synthetic certificate, including expired/no-EKU attacker cases, to the server.
                handler.SslOptions.LocalCertificateSelectionCallback = (_, _, _, _, _) => certificate;
            }
            return new(handler) { Timeout = TimeSpan.FromSeconds(10) };
        }

        public async ValueTask DisposeAsync()
        { await app.DisposeAsync(); server.Dispose(); AgentCertificate.Dispose(); }
    }

    internal sealed class AuthorityReader(byte[] der) : IAgentControlReader
    {
        internal bool Revoked { get; set; }
        internal int Reads { get; private set; }
        internal Func<CancellationToken, Task>? PauseRead { get; set; }
        internal Guid SessionId { get; } = Guid.NewGuid();
        internal List<AgentPollRequest> Polls { get; } = [];
        private readonly Guid agentId = Guid.NewGuid();
        internal Guid AgentId => agentId;
        public async Task<AgentCredentialAuthority?> ReadCredentialAsync(ReadOnlyMemory<byte> exactCertificateDer, CancellationToken token)
        {
            Reads++;
            var pause = PauseRead; PauseRead = null;
            if (pause is not null) await pause(token);
            return new(new(agentId, Guid.NewGuid(), der), AgentLifecycleState.Active,
                Revoked ? AgentCredentialState.Revoked : AgentCredentialState.Active, 1, 1, 1,
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), null, true, false, DateTimeOffset.UtcNow);
        }
        public Task<IReadOnlyList<AgentPublicPin>> ReadPinsAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<AgentGrantView?> ReadGrantAsync(Guid grantId, CancellationToken token) => throw new NotSupportedException();
        public Task<AgentGrantView?> FindClaimAsync(Guid claimId, Guid credentialId, CancellationToken token) => throw new NotSupportedException();
        public Task<AgentHealthView?> ReadHealthAsync(Guid agentId, CancellationToken token) => throw new NotSupportedException();
        public Task<bool> MatchesIssuedCodeAsync(Guid grantId, ReadOnlyMemory<byte> tokenHash, CancellationToken token) => throw new NotSupportedException();
    }
}
