using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;

using FlowPlane.Application.Agents;

using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace FlowPlane.Api.Agents;

// Only Kestrel's bound socket identifies this listener. Host/forwarding/certificate headers are never authority.
internal sealed class AgentListenerBoundary
{
    private int port;
    internal void Bind(ListenOptions value) => port = value.IPEndPoint?.Port ?? 0;
    internal void ObserveSocket(System.Net.EndPoint? endpoint)
    {
        if (endpoint is not System.Net.IPEndPoint { Port: > 0 } socket)
            throw new InvalidOperationException("The agent TLS listener requires a bound IP socket.");
        var previous = Interlocked.CompareExchange(ref port, socket.Port, 0);
        if (previous != 0 && previous != socket.Port) throw new InvalidOperationException("The agent listener identity changed.");
    }
    internal bool Matches(HttpContext context) => Volatile.Read(ref port) is > 0 and var value && context.Connection.LocalPort == value;
}

internal sealed class AgentServerIdentity(X509Certificate2 certificate) : IDisposable
{
    public void Dispose() => certificate.Dispose();
}

internal sealed record AgentPinAuthentication(long Generation, bool Unavailable);

internal static class AgentControlHosting
{
    internal const string Scheme = "FlowPlaneAgentCertificate";

    public static void AddAgentControl(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(AgentControlOptions.SectionName)
            .Get<AgentControlOptions>(binder => binder.ErrorOnUnknownConfiguration = true) ?? new();
        options.Validate();
        builder.Services.AddSingleton(options);
        var pins = new AgentPinRegistry();
        builder.Services.AddSingleton(pins);
        builder.Services.AddHealthChecks().AddCheck<AgentPinHealthCheck>("agent-pins", tags: ["ready"]);
        builder.Services.AddSingleton<AgentControlLimits>();
        var boundary = new AgentListenerBoundary();
        builder.Services.AddSingleton(boundary);
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddScoped<AgentControlService>();
        builder.Services.AddAuthentication().AddCertificate(Scheme, certificate =>
        {
            certificate.AllowedCertificateTypes = CertificateTypes.SelfSigned;
            certificate.ValidateCertificateUse = true;
            certificate.ValidateValidityPeriod = true;
            certificate.RevocationMode = X509RevocationMode.NoCheck;
            certificate.Events = new CertificateAuthenticationEvents
            {
                OnCertificateValidated = async context =>
                {
                    try
                    {
                        var http = context.HttpContext;
                        var registry = http.RequestServices.GetRequiredService<AgentPinRegistry>();
                        var generation = registry.Generation;
                        http.Features.Set(new AgentPinAuthentication(generation, false));
                        bool RefreshChanged() => registry.NeedsRefresh || registry.Generation != generation;
                        void Unavailable()
                        {
                            http.Features.Set(new AgentPinAuthentication(generation, true));
                            context.Fail("AgentUnavailable");
                        }
                        if (!IsAgentListener(http) || !http.Request.IsHttps)
                        { context.Fail("AgentAuthenticationDenied"); return; }
                        if (RefreshChanged()) { Unavailable(); return; }
                        if (!registry.Accepts(context.ClientCertificate, DateTimeOffset.UtcNow))
                        {
                            if (RefreshChanged()) Unavailable();
                            else context.Fail("AgentAuthenticationDenied");
                            return;
                        }
                        var authority = await http.RequestServices.GetRequiredService<IAgentControlReader>()
                            .ReadCredentialAsync(context.ClientCertificate.RawData, http.RequestAborted);
                        if (RefreshChanged()) { Unavailable(); return; }
                        if (authority is null || !authority.ContextCurrent || authority.AgentState == AgentLifecycleState.Revoked ||
                            authority.CredentialState is AgentCredentialState.Revoked or AgentCredentialState.Retired)
                        { context.Fail("AgentAuthenticationDenied"); return; }
                        http.Features.Set(authority);
                        context.Principal = new ClaimsPrincipal(new ClaimsIdentity(
                            [new Claim(ClaimTypes.NameIdentifier, authority.Certificate.AgentId.ToString("D"))], Scheme));
                        context.Success();
                    }
                    catch (Exception exception) when (exception is OperationCanceledException or AgentStoreException { Code: AgentStoreError.Unavailable })
                    {
                        var http = context.HttpContext;
                        var generation = http.Features.Get<AgentPinAuthentication>()!.Generation;
                        http.Features.Set(new AgentPinAuthentication(generation, true));
                        context.Fail("AgentUnavailable");
                    }
                    catch (AgentStoreException)
                    { context.Fail("AgentAuthenticationDenied"); }
                },
                OnAuthenticationFailed = context =>
                { context.Fail("AgentAuthenticationDenied"); return Task.CompletedTask; },
            };
        });
        // No certificate authentication cache and no forwarded-certificate middleware.
        builder.Logging.AddFilter("Microsoft.AspNetCore.Authentication.Certificate", LogLevel.None);
        if (!options.Enabled) return;
        builder.Services.AddHostedService<AgentPinLoader>();
        var server = X509CertificateLoader.LoadPkcs12FromFile(options.ServerCertificatePath!, null, X509KeyStorageFlags.EphemeralKeySet);
        ValidateServerCertificate(server);
        // Do not replace the separate A-09 Data Protection certificate registration.
        builder.Services.AddSingleton(new AgentServerIdentity(server));
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxConcurrentConnections = 1024;
            kestrel.ListenAnyIP(options.BrowserPort, listen =>
            {
                if (options.BrowserHttps) listen.UseHttps(server);
            });
            kestrel.ListenAnyIP(options.AgentPort, listen => ConfigureAgentListener(listen, server, pins, boundary));
        });
    }

    internal static void ConfigureAgentListener(ListenOptions listen, X509Certificate2 server, AgentPinRegistry pins, AgentListenerBoundary boundary)
    {
        listen.Protocols = HttpProtocols.Http1;
        boundary.Bind(listen);
        listen.Use(next => connection =>
        {
            boundary.ObserveSocket(connection.LocalEndPoint);
            return next(connection);
        });
        listen.UseHttps(AgentHttpsOptions(server, pins));
    }

    internal static HttpsConnectionAdapterOptions AgentHttpsOptions(X509Certificate2 server, AgentPinRegistry pins) => new()
    {
        ServerCertificate = server,
        ClientCertificateMode = ClientCertificateMode.AllowCertificate,
        ClientCertificateValidation = (certificate, _, _) => pins.Accepts(certificate, DateTimeOffset.UtcNow),
        HandshakeTimeout = TimeSpan.FromSeconds(10),
        SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
    };

    internal static bool IsAgentListener(HttpContext context) => context.RequestServices.GetRequiredService<AgentListenerBoundary>().Matches(context);

    internal static void ValidateServerCertificate(X509Certificate2 certificate)
    {
        var now = DateTimeOffset.UtcNow;
        if (!certificate.HasPrivateKey || certificate.NotBefore.ToUniversalTime() > now || certificate.NotAfter.ToUniversalTime() <= now ||
            !certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(extension =>
                extension.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Any(oid => oid.Value == "1.3.6.1.5.5.7.3.1")) ||
            !certificate.Extensions.Any(extension => extension.Oid?.Value == "2.5.29.17"))
            throw new InvalidOperationException("An explicit valid server-authentication TLS identity is required.");
    }

    public static void UseAgentControlBoundary(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var agentPath = context.Request.Path.StartsWithSegments("/agent");
            if (agentPath != IsAgentListener(context) || agentPath && !context.Request.IsHttps)
            { await AgentHttp.Problem(404, "AgentEndpointUnavailable").ExecuteAsync(context); return; }
            if (agentPath)
            {
                using var admission = context.RequestServices.GetRequiredService<AgentControlLimits>().AcquireRequest();
                if (!admission.IsAcquired)
                {
                    context.Response.Headers.RetryAfter = "5";
                    await AgentHttp.Problem(429, "AgentCapacity").ExecuteAsync(context); return;
                }
                context.Response.Headers.CacheControl = "no-store";
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                context.RequestAborted = timeout.Token;
                try { await next(context); }
                catch (Exception exception)
                {
                    if (!context.Response.HasStarted)
                    {
                        var invalid = exception is FlowPlane.Agent.Protocol.AgentProtocolException or System.Text.Json.JsonException or FormatException;
                        await AgentHttp.Problem(exception is AgentStoreException store ? AgentHttp.Status(store.Code) : invalid ? 400 : 503,
                            exception is AgentStoreException error ? "Agent" + error.Code : invalid ? "AgentRequestRejected" : "AgentUnavailable").ExecuteAsync(context);
                    }
                }
                return;
            }
            await next(context);
        });
    }
}

internal sealed class AgentPinLoader(IServiceScopeFactory scopeFactory, AgentPinRegistry pins) : BackgroundService
{
    public override async Task StartAsync(CancellationToken token)
    {
        try { await Refresh(token); }
        catch (Exception) when (!token.IsCancellationRequested) { } // Empty registry denies authentication until recovery.
        await base.StartAsync(token);
    }
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        var backoff = 1;
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(pins.NeedsRefresh ? backoff : 1), token);
            if (!pins.NeedsRefresh) continue;
            try { await Refresh(token); backoff = 1; }
            catch (Exception) when (!token.IsCancellationRequested) { backoff = Math.Min(15, backoff * 2); }
        }
    }
    private async Task Refresh(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var scope = scopeFactory.CreateScope();
        await pins.RefreshAsync(scope.ServiceProvider.GetRequiredService<IAgentControlReader>().ReadPinsAsync, deadline.Token);
    }
}

internal sealed class AgentPinHealthCheck(AgentControlOptions options, AgentPinRegistry pins) : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    public Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(options.Enabled && pins.NeedsRefresh
            ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("Agent authentication pins are unavailable.")
            : Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy());
}
