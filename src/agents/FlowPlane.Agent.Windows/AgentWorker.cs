using Microsoft.Extensions.Hosting;

using FlowPlane.Agent.Runtime.Journal;
using FlowPlane.Agent.Runtime.Control;
using FlowPlane.Agent.Runtime.Transport;
using FlowPlane.Agent.Runtime.Setup;
using FlowPlane.Agent.Windows.Identity;
using FlowPlane.Agent.Windows.Security;
using FlowPlane.Agent.Windows.Setup;

namespace FlowPlane.Agent.Windows;

/// <summary>Local setup and outbound typed control-plane polling only; no provider/native execution capability.</summary>
internal sealed class AgentWorker : BackgroundService
{
    private WindowsAgentCertificate? identity;
    private IAgentOperationJournal? journal;
    private AgentControlHost? control;
    private bool controlFailure;
    private readonly SemaphoreSlim controlGate = new(1, 1);
    private LocalSetupStatus startupStatus = LocalSetupStatus.ActionRequired;
    private readonly IWindowsAgentIdentity identityStore;
    private readonly Func<Guid, bool, IAgentOperationJournal> openJournal;
    private readonly Action completeInitialization;
    private readonly IServiceTokenVerifier tokenVerifier;

    public AgentWorker() : this(new WindowsAgentIdentity(WindowsAgentSecurity.DataDirectory),
        (id, initialize) => AgentOperationJournal.Open(Path.Combine(WindowsAgentSecurity.DataDirectory, "operations.journal"), id, initialize),
        () => File.Delete(Path.Combine(WindowsAgentSecurity.DataDirectory, "install.pending")), new ServiceTokenVerifier())
    { }

    internal AgentWorker(IWindowsAgentIdentity identityStore, Func<Guid, bool, IAgentOperationJournal> openJournal,
        Action completeInitialization, IServiceTokenVerifier tokenVerifier)
    {
        this.identityStore = identityStore; this.openJournal = openJournal;
        this.completeInitialization = completeInitialization; this.tokenVerifier = tokenVerifier;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Load(false, stoppingToken); }
        catch (Exception exception) when (exception is AgentWindowsException or AgentJournalException or AgentControlException)
        {
            WindowsAgentSecurity.VerifyPrivateDirectory(WindowsAgentSecurity.DataDirectory);
            if (File.Exists(Path.Combine(WindowsAgentSecurity.DataDirectory, "install.pending")) &&
                !File.Exists(Path.Combine(WindowsAgentSecurity.DataDirectory, "identity.public")) &&
                !File.Exists(Path.Combine(WindowsAgentSecurity.DataDirectory, "operations.journal"))) startupStatus = LocalSetupStatus.Uninitialized;
        }
        await Task.WhenAll(new LocalSetupPipe().ServeAsync(Handle, stoppingToken), RunControl(stoppingToken));
    }

    internal async ValueTask Load(bool initialize, CancellationToken cancellationToken)
    {
        if (identity is not null) throw new AgentWindowsException();
        var loaded = await identityStore.LoadOrCreateAsync(initialize, cancellationToken);
        IAgentOperationJournal? opened = null;
        try
        {
            opened = openJournal(loaded.InstallationId, initialize);
            if (initialize) completeInitialization();
            var host = CreateControl(loaded, opened);
            journal = opened;
            identity = loaded;
            control = host;
        }
        catch { try { opened?.Dispose(); } finally { loaded.Dispose(); } throw; }
    }

    internal async ValueTask<LocalSetupReceipt> Handle(LocalSetupRequest request, CancellationToken cancellationToken)
    {
        const string schema = "flowplane.agent/local-setup-receipt.v1";
        await controlGate.WaitAsync(cancellationToken);
        try
        {
            tokenVerifier.Verify();
            if (request.Action == LocalSetupAction.Pair)
                return control is null || controlFailure ? new(schema, controlFailure ? LocalSetupStatus.ActionRequired : LocalSetupStatus.PairingUnavailable) : await control.PairAsync(request, cancellationToken);
            if (request.Action == LocalSetupAction.Initialize)
            {
                if (startupStatus != LocalSetupStatus.Uninitialized) return new(schema, LocalSetupStatus.ActionRequired);
                await Load(true, cancellationToken);
            }
            if (journal is null || identity is null) return new(schema, startupStatus);
            if (controlFailure) return new(schema, LocalSetupStatus.ActionRequired);
            if (control is not null) return await control.StatusAsync(cancellationToken);
            var summary = await journal.ReadRecoverySummaryAsync(cancellationToken);
            return new(schema, summary.RequiresReconciliation ? LocalSetupStatus.ActionRequired : LocalSetupStatus.ReadyForLocalSetup);
        }
        catch (Exception exception) when (exception is AgentWindowsException or AgentJournalException or AgentControlException or IOException or UnauthorizedAccessException)
        { return new(schema, LocalSetupStatus.ActionRequired); }
        finally { controlGate.Release(); }
    }

    private async Task RunControl(CancellationToken token)
    {
        var backoff = 1;
        while (!token.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(5);
            await controlGate.WaitAsync(token);
            try
            {
                if (!controlFailure && control is not null && identity is not null && journal is not null)
                {
                    tokenVerifier.Verify();
                    if (control.NeedsRotation)
                    {
                        var rotation = new WindowsAgentRotationIdentity(WindowsAgentSecurity.DataDirectory);
                        using var pending = rotation.Stage(WindowsAgentIdentity.ReadRecord(WindowsAgentSecurity.DataDirectory));
                        await control.RotateAsync(pending.Certificate, token);
                        rotation.Promote(control.State!);
                        // Close only HTTP/control handles; operation journal and both persisted CNG keys remain.
                        control.Dispose(); control = null;
                        var renewed = await identityStore.LoadOrCreateAsync(false, token);
                        identity.Dispose(); identity = renewed;
                        control = CreateControl(renewed, journal);
                    }
                    else
                    {
                        delay = await control.StepAsync(token);
                        if (control.State is { Replacement: { } replacement, Enrollment.StatusReceipt.Stage: FlowPlane.Agent.Protocol.PairingStage.Redeemed } state &&
                            replacement.PinSha256 == state.Enrollment.PinSha256 &&
                            state.Enrollment.PinSha256 != Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(WindowsAgentIdentity.ReadRecord(WindowsAgentSecurity.DataDirectory).Der)))
                        {
                            new WindowsAgentRotationIdentity(WindowsAgentSecurity.DataDirectory).Promote(state, enrollmentGrant: replacement.GrantId);
                            control.Dispose(); control = null;
                            var renewed = await identityStore.LoadOrCreateAsync(false, token);
                            identity.Dispose(); identity = renewed;
                            control = CreateControl(renewed, journal);
                        }
                    }
                    backoff = 1;
                }
            }
            catch (AgentTransportException exception) when (exception.Code == AgentTransportError.Unavailable)
            { delay = TimeSpan.FromSeconds(backoff); backoff = Math.Min(15, backoff * 2); }
            catch (Exception exception) when (exception is AgentWindowsException or AgentJournalException or AgentControlException or AgentTransportException or IOException or UnauthorizedAccessException)
            { controlFailure = true; delay = TimeSpan.FromSeconds(15); }
            finally { controlGate.Release(); }
            await Task.Delay(delay, token);
        }
    }

    private static AgentControlHost CreateControl(WindowsAgentCertificate identity, IAgentOperationJournal journal)
        => new(Path.Combine(WindowsAgentSecurity.DataDirectory, "control.state"), identity.InstallationId, identity.Certificate, journal,
            grant => new WindowsAgentRotationIdentity(WindowsAgentSecurity.DataDirectory).Stage(WindowsAgentIdentity.ReadRecord(WindowsAgentSecurity.DataDirectory), grant).Certificate);

    public override void Dispose() { try { control?.Dispose(); journal?.Dispose(); identity?.Dispose(); } finally { base.Dispose(); } }
}
