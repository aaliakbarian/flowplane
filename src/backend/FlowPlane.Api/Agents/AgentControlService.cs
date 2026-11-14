using System.Security.Cryptography;

using FlowPlane.Agent.Protocol;
using FlowPlane.Application.Agents;
using FlowPlane.Application.Providers;
using FlowPlane.Providers.Ssis.Configuration;

namespace FlowPlane.Api.Agents;

// Transport coordination only. The F3 store independently serializes every mutation against current authority.
internal sealed class AgentControlService(IAgentControlStore store, IAgentControlReader reader, AgentPinRegistry pins,
    IProviderInstanceStore providers, SsisProviderConfigurationCodec codec, SsisEndpointPolicy endpoints,
    IHostEnvironment environment)
{
    internal async Task ResourceAsync(Guid id, CancellationToken token)
    {
        // Revocation and safe health remain available even if the provider was disabled or removed from endpoint policy.
        if (await providers.FindAsync(id, token) is null) throw new AgentStoreException(AgentStoreError.NotFound);
    }
    internal async Task ProviderAsync(Guid id, long? expectedVersion, CancellationToken token)
    {
        var provider = await providers.FindAsync(id, token);
        if (provider is null || !provider.IsEnabled || provider.ProviderType != SsisProviderConfigurationCodec.SsisProviderType)
            throw new AgentStoreException(AgentStoreError.NotFound);
        if (expectedVersion is { } version && provider.Version != version) throw new AgentStoreException(AgentStoreError.ContextChanged);
        SsisProviderConfiguration configuration;
        try { configuration = codec.Decode(provider.Configuration); }
        catch (ProviderConfigurationException) { throw new AgentStoreException(AgentStoreError.InvalidCredential); }
        if (!endpoints.Allows(configuration) || configuration.TrustServerCertificate && !environment.IsDevelopment())
            throw new AgentStoreException(AgentStoreError.InvalidCredential);
    }

    internal Task RefreshPinsAsync(CancellationToken token) => RefreshAsync(token);
    internal async Task<AgentAssignmentsReceipt> AssignmentsAsync(AgentCredentialAuthority authority, CancellationToken token)
    {
        await CurrentProvidersAsync(authority.Certificate.AgentId, token);
        var health = await reader.ReadHealthAsync(authority.Certificate.AgentId, token) ?? throw new AgentStoreException(AgentStoreError.NotFound);
        return new("flowplane.agent/assignments.v1", authority.Certificate.AgentId,
            health.Providers.OrderBy(provider => provider.ProviderInstanceId).Select(provider => new AgentProviderBinding(provider.ProviderInstanceId, provider.ProviderVersion)).ToArray());
    }
    private async Task RefreshAsync(CancellationToken token)
    {
        try { await pins.RefreshAsync(reader.ReadPinsAsync, token); }
        catch { throw new AgentStoreException(AgentStoreError.Unavailable); }
    }

    internal async Task<PairingClaimReceipt> ClaimAsync(PairingClaimRequest request, CancellationToken token)
    {
        var der = Convert.FromBase64String(request.PublicCertificateDerBase64);
        if (!AgentPairingSecurity.IsClientCertificate(der, DateTimeOffset.UtcNow)) throw new AgentStoreException(AgentStoreError.InvalidCredential);
        var grant = await reader.ReadGrantAsync(request.GrantId, token) ?? throw new AgentStoreException(AgentStoreError.NotFound);
        await ProviderAsync(grant.ProviderInstanceId, grant.ProviderVersion, token);
        var code = AgentPairingSecurity.DecodeToken(request.PairingCode);
        try
        {
            var receipt = await store.ClaimAsync(new(request.GrantId, request.ClaimId, SHA256.HashData(code), der), token);
            await RefreshAsync(token);
            return ClaimReceipt(receipt, der);
        }
        finally { CryptographicOperations.ZeroMemory(code); }
    }

    internal async Task<AgentGrantView> PairingAsync(Guid claimId, AgentCredentialAuthority authority, CancellationToken token)
    {
        var grant = await reader.FindClaimAsync(claimId, authority.Certificate.CredentialId, token)
            ?? throw new AgentStoreException(AgentStoreError.NotFound);
        await ProviderAsync(grant.ProviderInstanceId, grant.ProviderVersion, token);
        return grant;
    }

    internal async Task<PairingStatusReceipt> ProveAsync(Guid claimId, PairingProofRequest request,
        AgentCredentialAuthority authority, CancellationToken token)
    {
        if (claimId != request.ClaimId) throw new AgentStoreException(AgentStoreError.InvalidRequest);
        var grant = await PairingAsync(claimId, authority, token);
        var receipt = await store.RecordProofAsync(new(grant.Receipt.GrantId, claimId, request.ProofRequestId,
            AgentPairingSecurity.DecodeToken(request.ServerNonce), authority.Certificate), token);
        return StatusReceipt(receipt, grant.CertificateDer!);
    }

    internal async Task<PairingClaimReceipt> RotateAsync(AgentRotationRequest request, AgentCredentialAuthority authority, CancellationToken token)
    {
        await ProviderAsync(request.ProviderInstanceId, request.ProviderVersion, token);
        var der = Convert.FromBase64String(request.PublicCertificateDerBase64);
        if (!AgentPairingSecurity.IsClientCertificate(der, DateTimeOffset.UtcNow)) throw new AgentStoreException(AgentStoreError.InvalidCredential);
        var grant = await store.BeginRotationAsync(new(new(request.ProviderInstanceId, request.ProviderVersion),
            authority.Certificate, authority.AuthorizationVersion, request.RequestId), token);
        var receipt = await store.ClaimAsync(new(grant.GrantId, request.ClaimId, ReadOnlyMemory<byte>.Empty, der, authority.Certificate), token);
        await RefreshAsync(token);
        return ClaimReceipt(receipt, der);
    }

    internal async Task<PairingStatusReceipt> RotationProofAsync(Guid grantId, PairingProofRequest request,
        AgentCredentialAuthority authority, CancellationToken token)
    {
        var grant = await reader.ReadGrantAsync(grantId, token) ?? throw new AgentStoreException(AgentStoreError.NotFound);
        await ProviderAsync(grant.ProviderInstanceId, grant.ProviderVersion, token);
        var input = new ProveAgentGrant(grantId, request.ClaimId, request.ProofRequestId,
            AgentPairingSecurity.DecodeToken(request.ServerNonce), authority.Certificate);
        await store.RecordProofAsync(input, token);
        var receipt = await store.ActivateRotationAsync(input, token);
        await RefreshAsync(token);
        return StatusReceipt(receipt, grant.CertificateDer!);
    }

    internal async Task<FlowPlane.Agent.Protocol.AgentSessionStatusReceipt> SessionAsync(AgentCredentialAuthority authority, CancellationToken token)
    {
        await CurrentProvidersAsync(authority.Certificate.AgentId, token);
        var receipt = await store.ReadSessionAsync(authority.Certificate, token);
        return new("flowplane.agent/session-status.v1", authority.Certificate.AgentId, receipt?.Fence ?? authority.Fence,
            receipt?.DatabaseTimeUtc ?? authority.DatabaseTimeUtc, receipt?.LeaseExpiresAtUtc,
            receipt?.RecoveryRequired == true || authority.RecoveryRequired ? AgentRecoveryMode.ActionRequired : AgentRecoveryMode.None);
    }

    internal async Task<FlowPlane.Agent.Protocol.AgentSessionReceipt> ConnectAsync(AgentConnectRequest request,
        AgentCredentialAuthority authority, CancellationToken token)
    {
        await CurrentProvidersAsync(authority.Certificate.AgentId, token);
        var settled = request.JournalRecoveryDigest == AgentPairingSecurity.RecoveryDigest(0, false);
        var outcome = await store.ConnectAsync(new(authority.Certificate, request.BootId, request.ConnectRequestId,
            request.ExpectedFence, authority.AuthorizationVersion, request.ProtocolVersion, request.PackageVersion, settled, settled ? 0 : 1), token);
        if (outcome.Error is { } error) throw new AgentStoreException(error);
        var receipt = outcome.Receipt ?? throw new AgentStoreException(AgentStoreError.Unavailable);
        return new("flowplane.agent/session-receipt.v1", receipt.AgentId, receipt.SessionId, receipt.BootId, receipt.Fence,
            receipt.AuthorizationVersion, receipt.AcceptedAtUtc, receipt.LeaseExpiresAtUtc, AgentRecoveryMode.None);
    }

    internal async Task<FlowPlane.Agent.Protocol.AgentPollReceipt> PollAsync(AgentPollRequest request,
        AgentCredentialAuthority authority, CancellationToken token)
    {
        await CurrentProvidersAsync(authority.Certificate.AgentId, token);
        var ready = request.Readiness == FlowPlane.Agent.Protocol.AgentReadiness.ReadyForControlPlane &&
            request.Availability == AgentAvailability.Available && request.UnresolvedEvidenceCount == 0;
        var outcome = await store.PollAsync(new(authority.Certificate, request.SessionId, request.Fence,
            authority.AuthorizationVersion, request.RequestId, request.Sequence,
            ready ? FlowPlane.Application.Agents.AgentReadiness.ReadyForControlPlane : FlowPlane.Application.Agents.AgentReadiness.ActionRequired,
            ready, request.UnresolvedEvidenceCount), token);
        if (outcome.Error is { } error) throw new AgentStoreException(error);
        var receipt = outcome.Receipt ?? throw new AgentStoreException(AgentStoreError.Unavailable);
        return new("flowplane.agent/poll-receipt.v1", receipt.SessionId, receipt.Fence, receipt.Sequence, receipt.AcknowledgedAtUtc,
            receipt.LeaseExpiresAtUtc, receipt.NextPollSeconds, AgentWorkKind.None);
    }

    private async Task CurrentProvidersAsync(Guid agentId, CancellationToken token)
    {
        var health = await reader.ReadHealthAsync(agentId, token) ?? throw new AgentStoreException(AgentStoreError.NotFound);
        if (health.Providers.Length == 0) throw new AgentStoreException(AgentStoreError.ContextChanged);
        foreach (var provider in health.Providers) await ProviderAsync(provider.ProviderInstanceId, provider.ProviderVersion, token);
    }

    internal static PairingClaimReceipt ClaimReceipt(AgentGrantReceipt receipt, byte[] der) => new("flowplane.agent/pairing-claim-receipt.v1",
        receipt.GrantId, receipt.ClaimId!.Value, receipt.AgentId, Convert.ToHexStringLower(SHA256.HashData(der)),
        AgentPairingSecurity.EncodeToken(receipt.Nonce!), receipt.ExpiresAtUtc, (PairingStage)receipt.State, receipt.Version);

    internal static PairingStatusReceipt StatusReceipt(AgentGrantReceipt receipt, byte[] der) => new("flowplane.agent/pairing-status.v1",
        receipt.GrantId, receipt.ClaimId!.Value, (PairingStage)receipt.State, receipt.ExpiresAtUtc,
        AgentPairingSecurity.VerificationPhrase(receipt.GrantId, receipt.ClaimId.Value, Convert.ToHexStringLower(SHA256.HashData(der)),
            AgentPairingSecurity.EncodeToken(receipt.Nonce!)), receipt.Version);
}
