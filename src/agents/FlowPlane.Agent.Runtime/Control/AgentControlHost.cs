using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FlowPlane.Agent.Protocol;
using FlowPlane.Agent.Runtime.Journal;
using FlowPlane.Agent.Runtime.Setup;
using FlowPlane.Agent.Runtime.Transport;
namespace FlowPlane.Agent.Runtime.Control;
/// <summary>Local enrollment/configuration gate; an unpaired installation never creates an HTTP client.</summary>
public sealed class AgentControlHost : IDisposable
{
    private readonly string statePath;
    private readonly Guid installation;
    private X509Certificate2 certificate;
    private bool ownsCertificate;
    private readonly Func<Guid, X509Certificate2>? stageEnrollment;
    private bool pairingRejected;
    private readonly IAgentOperationJournal journal;
    private readonly Func<string, X509Certificate2, AgentHttpsTransport> createTransport;
    private readonly SemaphoreSlim gate = new(1, 1);
    private AgentControlStore? store;
    private AgentHttpsTransport? transport;
    private AgentPollingLoop? polling;
    private bool actionRequired;
    private int backoff = 1;
    public AgentControlState? State => store?.State;
    public bool NeedsRotation => !actionRequired && store?.State.Enrollment?.StatusReceipt?.Stage == PairingStage.Redeemed &&
        (store.State.Rotation is { } rotation && rotation.PinSha256 != Convert.ToHexStringLower(SHA256.HashData(certificate.RawData)) ||
         certificate.NotAfter.ToUniversalTime() - DateTime.UtcNow <= TimeSpan.FromDays(30) && store.State.PendingPoll is null &&
         (store.State.ConnectRequest is null || store.State.ConnectReceipt is not null));

    public AgentControlHost(string statePath, Guid installation, X509Certificate2 certificate, IAgentOperationJournal journal,
        Func<Guid, X509Certificate2>? stageEnrollment = null)
        : this(statePath, installation, certificate, journal, AgentHttpsTransport.Create, stageEnrollment) { }

    internal AgentControlHost(string statePath, Guid installation, X509Certificate2 certificate, IAgentOperationJournal journal, Func<string, X509Certificate2, AgentHttpsTransport> createTransport,
        Func<Guid, X509Certificate2>? stageEnrollment = null)
    {
        this.statePath = statePath; this.installation = installation; this.certificate = certificate;
        this.stageEnrollment = stageEnrollment;
        this.journal = journal; this.createTransport = createTransport;
        if (File.Exists(statePath) || File.Exists(statePath + ".expected"))
        {
            store = AgentControlStore.Open(statePath, installation);
            var pin = AgentReceiptChecks.Pin(certificate);
            if (store.State.Enrollment is { } enrollment && enrollment.PinSha256 != pin &&
                (enrollment.StatusReceipt?.Stage == PairingStage.Redeemed || store.State.Replacement?.PinSha256 != pin))
            { store.Dispose(); store = null; throw new AgentControlException(); }
        }
    }

    public async Task<LocalSetupReceipt> StatusAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var summary = await journal.ReadRecoverySummaryAsync(token);
            if (actionRequired || pairingRejected || summary.RequiresReconciliation || summary.UnresolvedCount != 0) return new("flowplane.agent/local-setup-receipt.v1", LocalSetupStatus.ActionRequired);
            return Receipt();
        }
        catch (AgentJournalException) { actionRequired = true; return new("flowplane.agent/local-setup-receipt.v1", LocalSetupStatus.ActionRequired); }
        finally { gate.Release(); }
    }

    public async Task<LocalSetupReceipt> PairAsync(LocalSetupRequest request, CancellationToken token)
    {
        if (request.Action != LocalSetupAction.Pair || request.GrantId is null)
        { request.ClearPairingCode(); return new("flowplane.agent/local-setup-receipt.v1", LocalSetupStatus.PairingUnavailable); }
        await gate.WaitAsync(token);
        try
        {
            var validated = LocalSetupCodec.Encode(request);
            CryptographicOperations.ZeroMemory(validated);
            var summary = await journal.ReadRecoverySummaryAsync(token);
            if (actionRequired || summary.RequiresReconciliation || summary.UnresolvedCount != 0) throw new AgentControlException();
            store ??= AgentControlStore.Open(statePath, installation, true);
            if (store.State.Origin is not null && new Uri(store.State.Origin) != AgentHttpsTransport.ValidateOrigin(request.BackendAddress!)) throw new AgentControlException();
            store.Save(store.State); // Verify writable durable evidence before allocating a replacement key.
            if (store.State.Enrollment is { } previous && previous.GrantId != request.GrantId)
            {
                if (previous.StatusReceipt?.Stage == PairingStage.Redeemed || stageEnrollment is null || store.State.Rotation is not null) throw new AgentControlException();
                var next = stageEnrollment(request.GrantId.Value);
                try
                {
                    var pin = AgentReceiptChecks.Pin(next);
                    if (pin == previous.PinSha256) throw new AgentControlException();
                    store.Save(store.State with { Replacement = new(request.GrantId.Value, pin, previous) });
                }
                catch { next.Dispose(); throw; }
                transport?.Dispose(); transport = null; polling = null;
                if (ownsCertificate) certificate.Dispose();
                certificate = next; ownsCertificate = true;
            }
            pairingRejected = false;
            transport ??= createTransport(request.BackendAddress!, certificate);
            await new AgentEnrollmentCoordinator(store, transport, certificate).BeginAsync(request.BackendAddress!, request.GrantId.Value, request.PairingCode!.ToCharArray(), token);
            return Receipt();
        }
        catch (AgentTransportException exception) when (exception.Code == AgentTransportError.Unavailable) { return Receipt(); }
        catch (AgentTransportException exception) when (exception.Code is AgentTransportError.Denied or AgentTransportError.Rejected or AgentTransportError.Conflict)
        { pairingRejected = true; return new("flowplane.agent/local-setup-receipt.v2", LocalSetupStatus.ActionRequired); }
        catch (AgentEnrollmentRejectedException) { pairingRejected = true; return new("flowplane.agent/local-setup-receipt.v2", LocalSetupStatus.ActionRequired); }
        catch (Exception exception) when (exception is AgentControlException or AgentJournalException or AgentTransportException or LocalSetupException or AgentProtocolException)
        { actionRequired = true; return new("flowplane.agent/local-setup-receipt.v2", LocalSetupStatus.ActionRequired); }
        finally { request.ClearPairingCode(); gate.Release(); }
    }

    public async Task<TimeSpan> StepAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (actionRequired || pairingRejected || store?.State.Enrollment is not { } enrollment) return TimeSpan.FromSeconds(5);
            if (enrollment.StatusReceipt?.Stage != PairingStage.Redeemed && store.State.Replacement is { } replacement && replacement.GrantId != enrollment.GrantId) return TimeSpan.FromSeconds(5);
            transport ??= createTransport(store.State.Origin!, certificate);
            if (enrollment.StatusReceipt?.Stage != PairingStage.Redeemed)
            {
                await new AgentEnrollmentCoordinator(store, transport, certificate).RecoverAsync(token);
                return TimeSpan.FromSeconds(5);
            }
            polling ??= new AgentPollingLoop(store, transport, journal, "1.0.0");
            var delay = await polling.StepAsync(token);
            backoff = 1;
            return delay;
        }
        catch (AgentTransportException exception) when (exception.Code == AgentTransportError.Unavailable)
        { var delay = TimeSpan.FromSeconds(backoff); backoff = Math.Min(15, backoff * 2); return delay; }
        catch (AgentEnrollmentRejectedException) { pairingRejected = true; return TimeSpan.FromSeconds(15); }
        catch (AgentTransportException exception) when (store?.State.Enrollment?.StatusReceipt?.Stage != PairingStage.Redeemed && exception.Code is AgentTransportError.Denied or AgentTransportError.Rejected or AgentTransportError.Conflict)
        { pairingRejected = true; return TimeSpan.FromSeconds(15); }
        catch (Exception exception) when (exception is AgentControlException or AgentJournalException or AgentTransportException)
        { actionRequired = true; return TimeSpan.FromSeconds(15); }
        finally { gate.Release(); }
    }

    private LocalSetupReceipt Receipt()
    {
        if (store?.State.Enrollment is not { } enrollment) return new("flowplane.agent/local-setup-receipt.v1", LocalSetupStatus.ReadyForLocalSetup);
        if (enrollment.StatusReceipt?.Stage != PairingStage.Redeemed && store.State.Replacement is { } replacement && replacement.GrantId != enrollment.GrantId) return new("flowplane.agent/local-setup-receipt.v2", LocalSetupStatus.PairingPending);
        var status = enrollment.StatusReceipt;
        return new("flowplane.agent/local-setup-receipt.v2", status?.Stage == PairingStage.Redeemed ? LocalSetupStatus.Paired : LocalSetupStatus.PairingPending,
            status?.VerificationPhrase, status?.Stage);
    }

    public void Dispose() { transport?.Dispose(); store?.Dispose(); if (ownsCertificate) certificate.Dispose(); gate.Dispose(); }
    public async Task<PairingStatusReceipt> RotateAsync(X509Certificate2 pendingCertificate, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (actionRequired || store?.State.Enrollment?.StatusReceipt?.Stage != PairingStage.Redeemed) throw new AgentControlException();
            if (store.State.PendingPoll is not null || store.State.ConnectRequest is not null && store.State.ConnectReceipt is null) throw new AgentControlException();
            transport ??= createTransport(store.State.Origin!, certificate);
            using var pendingTransport = createTransport(store.State.Origin!, pendingCertificate);
            if (store.State.Rotation is { StatusReceipt.Stage: PairingStage.Redeemed } completed &&
                completed.PinSha256 == AgentReceiptChecks.Pin(certificate) &&
                completed.ClaimReceipt!.ExpiresAtUtc.AddHours(1) < DateTimeOffset.UtcNow)
                store.Save(store.State with { Rotation = null });
            var coordinator = new AgentRotationCoordinator(store, transport, pendingTransport, pendingCertificate, journal);
            if (store.State.Rotation is not null) return await coordinator.RecoverAsync(token);
            var assignments = await transport.GetAssignmentsAsync(token);
            if (assignments.AgentId != store.State.Enrollment.ClaimReceipt!.AgentId) throw new AgentControlException();
            var binding = assignments.Providers.OrderBy(value => value.ProviderInstanceId).First();
            return await coordinator.BeginAsync(binding.ProviderInstanceId, binding.ProviderVersion, token);
        }
        finally { gate.Release(); }
    }
}
