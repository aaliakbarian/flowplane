using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;

using FlowPlane.Agent.Protocol;
using FlowPlane.Api.Authentication;
using FlowPlane.Application.Agents;

using Microsoft.AspNetCore.Authentication;

namespace FlowPlane.Api.Agents;

internal static class AgentControlApi
{
    public static void MapAgentControl(this WebApplication app)
    {
        var agents = app.MapGroup("/agent/v1").AddEndpointFilter(GuardAgentAsync);
        agents.MapPost("/pairing-claims", async (HttpContext http, AgentControlService service) =>
            AgentHttp.Wire(await service.ClaimAsync(AgentProtocolCodec.Decode<PairingClaimRequest>(await AgentHttp.ReadBody(http)), http.RequestAborted)));
        agents.MapGet("/pairings/{claimId:guid}", async (Guid claimId, HttpContext http, AgentControlService service, IAgentControlStore store) =>
        {
            var authority = Authority(http, AgentControlAction.PairingStatus);
            var grant = await service.PairingAsync(claimId, authority, http.RequestAborted);
            var receipt = await store.ReadGrantAsync(grant.Receipt.GrantId, authority.Certificate, http.RequestAborted);
            return AgentHttp.Wire(AgentControlService.StatusReceipt(receipt, grant.CertificateDer!));
        });
        agents.MapGet("/pairings/{claimId:guid}/claim", async (Guid claimId, HttpContext http, AgentControlService service, IAgentControlStore store) =>
        {
            var authority = Authority(http, AgentControlAction.PairingStatus);
            var grant = await service.PairingAsync(claimId, authority, http.RequestAborted);
            var receipt = await store.ReadGrantAsync(grant.Receipt.GrantId, authority.Certificate, http.RequestAborted);
            if (grant.ClaimReceiptVersion is not { } version) throw new AgentStoreException(AgentStoreError.Unavailable);
            return AgentHttp.Wire(AgentControlService.ClaimReceipt(receipt with { State = AgentGrantState.Claimed, Version = version }, grant.CertificateDer!));
        });
        agents.MapPost("/pairings/{claimId:guid}/proof", async (Guid claimId, HttpContext http, AgentControlService service) =>
            AgentHttp.Wire(await service.ProveAsync(claimId, AgentProtocolCodec.Decode<PairingProofRequest>(await AgentHttp.ReadBody(http)),
                Authority(http, AgentControlAction.PairingProof), http.RequestAborted)));
        agents.MapGet("/session", async (HttpContext http, AgentControlService service) =>
            AgentHttp.Wire(await service.SessionAsync(Authority(http, AgentControlAction.Connect), http.RequestAborted)));
        agents.MapGet("/assignments", async (HttpContext http, AgentControlService service) =>
            AgentHttp.Wire(await service.AssignmentsAsync(Authority(http, AgentControlAction.Connect), http.RequestAborted)));
        agents.MapPost("/sessions", async (HttpContext http, AgentControlService service) =>
            AgentHttp.Wire(await service.ConnectAsync(AgentProtocolCodec.Decode<AgentConnectRequest>(await AgentHttp.ReadBody(http)),
                Authority(http, AgentControlAction.Connect), http.RequestAborted)));
        agents.MapPost("/polls", async (HttpContext http, AgentControlService service) =>
            AgentHttp.Wire(await service.PollAsync(AgentProtocolCodec.Decode<AgentPollRequest>(await AgentHttp.ReadBody(http)),
                Authority(http, AgentControlAction.Poll), http.RequestAborted)));
        agents.MapPost("/rotations", async (HttpContext http, AgentControlService service) =>
            AgentHttp.Wire(await service.RotateAsync(AgentProtocolCodec.Decode<AgentRotationRequest>(await AgentHttp.ReadBody(http)),
                Authority(http, AgentControlAction.Connect), http.RequestAborted)));
        agents.MapPost("/rotations/{grantId:guid}/proof", async (Guid grantId, HttpContext http, AgentControlService service) =>
            AgentHttp.Wire(await service.RotationProofAsync(grantId, AgentProtocolCodec.Decode<PairingProofRequest>(await AgentHttp.ReadBody(http)),
                Authority(http, AgentControlAction.PairingProof), http.RequestAborted)));
        agents.MapGet("/rotations/{grantId:guid}", async (Guid grantId, HttpContext http, AgentControlService service,
            IAgentControlReader reader, IAgentControlStore store) =>
        {
            var authority = http.Features.Get<AgentCredentialAuthority>() ?? throw new AgentStoreException(AgentStoreError.InvalidCredential);
            Authority(http, authority.CredentialState == AgentCredentialState.RetiringReceiptsOnly ? AgentControlAction.RotationReceipt : AgentControlAction.PairingStatus);
            var grant = await reader.ReadGrantAsync(grantId, http.RequestAborted) ?? throw new AgentStoreException(AgentStoreError.NotFound);
            await service.ProviderAsync(grant.ProviderInstanceId, grant.ProviderVersion, http.RequestAborted);
            var receipt = await store.ReadGrantAsync(grantId, authority.Certificate, http.RequestAborted);
            return AgentHttp.Wire(AgentControlService.StatusReceipt(receipt, grant.CertificateDer!));
        });

        var human = app.MapGroup("/agents").RequireAuthorization(OperatorAuthorizationHandler.Policy).AddEndpointFilter(GuardHumanAsync);
        human.MapPost("/pairing-grants", IssueAsync);
        human.MapGet("/{agentId:guid}", HealthAsync);
        human.MapGet("/{agentId:guid}/pairings/{grantId:guid}", ReadHumanGrantAsync);
        human.MapPost("/{agentId:guid}/pairings/{grantId:guid}/confirmation", ConfirmAsync);
        human.MapPost("/{agentId:guid}/revocation", RevokeAsync);
    }

    private static AgentCredentialAuthority Authority(HttpContext http, AgentControlAction action)
    {
        var authority = http.Features.Get<AgentCredentialAuthority>() ?? throw new AgentStoreException(AgentStoreError.InvalidCredential);
        var decision = AgentLifecyclePolicy.Authorize(new(authority.AgentState, authority.CredentialState, authority.ContextCurrent,
            authority.NotBeforeUtc <= authority.DatabaseTimeUtc && authority.NotAfterUtc > authority.DatabaseTimeUtc), action);
        if (!decision.Allowed) throw new AgentStoreException(decision.Error == AgentControlError.ActionRequired ? AgentStoreError.ActionRequired : AgentStoreError.InvalidCredential);
        return authority;
    }

    internal static async ValueTask<object?> GuardAgentAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!http.RequestServices.GetRequiredService<AgentControlOptions>().Enabled) return AgentHttp.Problem(503, "AgentControlDisabled");
        var bootstrap = http.Request.Path == "/agent/v1/pairing-claims";
        if (bootstrap && !http.RequestServices.GetRequiredService<AgentControlLimits>().TryEnrollment())
        { http.Response.Headers.RetryAfter = "60"; return AgentHttp.Problem(429, "AgentCapacity"); }
        if (!bootstrap)
        {
            var pins = http.RequestServices.GetRequiredService<AgentPinRegistry>();
            var generation = pins.Generation;
            if (pins.NeedsRefresh) return AgentHttp.Problem(503, "AgentUnavailable");
            var result = await http.AuthenticateAsync(AgentControlHosting.Scheme);
            var authentication = http.Features.Get<AgentPinAuthentication>();
            if (pins.NeedsRefresh || pins.Generation != generation || authentication is { Unavailable: true } ||
                authentication is not null && authentication.Generation != pins.Generation)
                return AgentHttp.Problem(503, "AgentUnavailable");
            if (!result.Succeeded) return AgentHttp.Problem(403, "AgentAuthenticationDenied");
        }
        else if (await http.Connection.GetClientCertificateAsync(http.RequestAborted) is not null)
            return AgentHttp.Problem(403, "AgentBootstrapRequiresSeparateConnection");
        return await next(context);
    }

    private static async ValueTask<object?> GuardHumanAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        http.Response.Headers.CacheControl = "no-store";
        if (!http.RequestServices.GetRequiredService<AgentControlOptions>().Enabled) return AgentHttp.Problem(503, "AgentControlDisabled");
        if (!http.Request.IsHttps) return AgentHttp.Problem(400, "AgentHttpsRequired");
        try { return await next(context); }
        catch (AgentPreconditionException exception) { return AgentHttp.Problem(exception.Status, "AgentPreconditionRequired"); }
        catch (AgentStoreException exception) { return AgentHttp.Problem(AgentHttp.Status(exception.Code), "Agent" + exception.Code); }
        catch (Exception exception) when (exception is JsonException or AgentProtocolException or FormatException or ArgumentException)
        { return AgentHttp.Problem(400, "AgentRequestRejected"); }
        catch (Exception)
        { return AgentHttp.Problem(503, "AgentUnavailable"); }
    }

    private static AgentActor Actor(HttpContext http) => new(http.User.FindFirstValue("iss")!, http.User.FindFirstValue("sub")!);
    private static async Task<T> Input<T>(HttpContext http) => JsonSerializer.Deserialize<T>(await AgentHttp.ReadBody(http), AgentHttp.HumanJson)
        ?? throw new AgentProtocolException();
    private static async Task<AgentGrantView> HumanGrant(Guid agentId, Guid grantId, HttpContext http, IAgentControlReader reader, AgentControlService service)
    {
        var grant = await reader.ReadGrantAsync(grantId, http.RequestAborted) ?? throw new AgentStoreException(AgentStoreError.NotFound);
        if (grant.Receipt.AgentId != agentId || grant.Actor != Actor(http)) throw new AgentStoreException(AgentStoreError.NotFound);
        await service.ProviderAsync(grant.ProviderInstanceId, grant.ProviderVersion, http.RequestAborted);
        return grant;
    }
    private static object HumanReceipt(AgentGrantView grant, HttpContext http)
    {
        var receipt = grant.Receipt;
        AgentHttp.ETag(http, receipt.Version);
        return new
        {
            receipt.GrantId,
            receipt.AgentId,
            receipt.ProviderInstanceId,
            Stage = receipt.State.ToString(),
            receipt.ExpiresAtUtc,
            Version = AgentHttp.Version(receipt.Version),
            VerificationPhrase = grant.CertificateDer is null || receipt.Nonce is null ? null :
                AgentControlService.StatusReceipt(receipt, grant.CertificateDer).VerificationPhrase
        };
    }

    private static async Task<IResult> IssueAsync(HttpContext http, AgentControlService service, IAgentControlStore store, IAgentControlReader reader)
    {
        var input = await Input<IssueInput>(http);
        await service.ProviderAsync(input.ProviderInstanceId, input.ProviderVersion, http.RequestAborted);
        var code = RandomNumberGenerator.GetBytes(32);
        try
        {
            var hash = SHA256.HashData(code);
            var receipt = await store.IssueGrantAsync(new(new(input.ProviderInstanceId, input.ProviderVersion), Actor(http), input.ClientRequestId,
                input.DisplayName, hash, input.ExistingAgentId, input.ExpectedAgentVersion), http.RequestAborted);
            // A replay cannot reveal a previously issued code; no plaintext code is ever persisted.
            var ownsCode = await reader.MatchesIssuedCodeAsync(receipt.GrantId, hash, http.RequestAborted);
            AgentHttp.ETag(http, receipt.Version);
            return Results.Json(new
            {
                receipt.GrantId,
                receipt.AgentId,
                receipt.ProviderInstanceId,
                Stage = receipt.State.ToString(),
                receipt.ExpiresAtUtc,
                Version = AgentHttp.Version(receipt.Version),
                PairingCode = ownsCode ? AgentPairingSecurity.EncodeToken(code) : null
            });
        }
        finally { CryptographicOperations.ZeroMemory(code); }
    }

    private static async Task<IResult> ReadHumanGrantAsync(Guid agentId, Guid grantId, HttpContext http, IAgentControlReader reader, AgentControlService service)
        => Results.Json(HumanReceipt(await HumanGrant(agentId, grantId, http, reader, service), http));

    private static async Task<IResult> ConfirmAsync(Guid agentId, Guid grantId, HttpContext http, IAgentControlReader reader, IAgentControlStore store, AgentControlService service)
    {
        var version = AgentHttp.IfMatch(http);
        var input = await Input<ConfirmInput>(http);
        var grant = await HumanGrant(agentId, grantId, http, reader, service);
        if (grant.CertificateDer is null || grant.Receipt.Nonce is null ||
            AgentControlService.StatusReceipt(grant.Receipt, grant.CertificateDer).VerificationPhrase != input.VerificationPhrase)
            throw new AgentStoreException(AgentStoreError.Conflict);
        await store.ConfirmAsync(new(grantId, Actor(http), version, grant.Receipt.Nonce), http.RequestAborted);
        await service.RefreshPinsAsync(http.RequestAborted);
        return Results.Json(HumanReceipt(await HumanGrant(agentId, grantId, http, reader, service), http));
    }

    private static async Task<IResult> HealthAsync(Guid agentId, HttpContext http, IAgentControlReader reader, AgentControlService service)
    {
        var health = await reader.ReadHealthAsync(agentId, http.RequestAborted) ?? throw new AgentStoreException(AgentStoreError.NotFound);
        foreach (var provider in health.Providers) await service.ResourceAsync(provider.ProviderInstanceId, http.RequestAborted);
        AgentHttp.ETag(http, health.Version);
        return Results.Json(new
        {
            health.AgentId,
            health.DisplayName,
            State = health.State.ToString(),
            Version = AgentHttp.Version(health.Version),
            AuthorizationVersion = AgentHttp.Version(health.AuthorizationVersion),
            Fence = AgentHttp.Version(health.Fence),
            health.PackageVersion,
            health.ProtocolVersion,
            Readiness = health.Readiness.ToString(),
            health.LastContactAtUtc,
            health.LastHeartbeatAtUtc,
            health.LeaseExpiresAtUtc,
            health.DatabaseTimeUtc,
            health.AuthorityCurrent,
            health.RecoveryRequired,
            Providers = health.Providers.Select(provider => new { provider.ProviderInstanceId, ProviderVersion = AgentHttp.Version(provider.ProviderVersion) }),
            ExecutionReadiness = "Unknown",
            HostObservations = "NotAvailable",
            DataFlowTelemetry = "NotAvailable"
        });
    }

    private static async Task<IResult> RevokeAsync(Guid agentId, HttpContext http, IAgentControlReader reader, IAgentControlStore store, AgentControlService service)
    {
        var version = AgentHttp.IfMatch(http);
        var health = await reader.ReadHealthAsync(agentId, http.RequestAborted) ?? throw new AgentStoreException(AgentStoreError.NotFound);
        foreach (var provider in health.Providers) await service.ResourceAsync(provider.ProviderInstanceId, http.RequestAborted);
        await store.RevokeAsync(new(agentId, Actor(http), version), http.RequestAborted);
        await service.RefreshPinsAsync(http.RequestAborted);
        return Results.NoContent();
    }

    private sealed record IssueInput(Guid ProviderInstanceId, long ProviderVersion, Guid ClientRequestId, string DisplayName,
        Guid? ExistingAgentId = null, long? ExpectedAgentVersion = null);
    private sealed record ConfirmInput(string VerificationPhrase);
}
