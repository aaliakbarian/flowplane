using Microsoft.Extensions.Hosting;

using FlowPlane.Agent.Runtime.Journal;
using FlowPlane.Agent.Runtime.Setup;
using FlowPlane.Agent.Windows.Identity;
using FlowPlane.Agent.Windows.Security;
using FlowPlane.Agent.Windows.Setup;

namespace FlowPlane.Agent.Windows;

/// <summary>F2 local-only shell: no polling, network transport, provider or native execution capability.</summary>
internal sealed class AgentWorker : BackgroundService
{
    private WindowsAgentCertificate? identity;
    private IAgentOperationJournal? journal;
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
        catch (Exception exception) when (exception is AgentWindowsException or AgentJournalException)
        {
            WindowsAgentSecurity.VerifyPrivateDirectory(WindowsAgentSecurity.DataDirectory);
            if (File.Exists(Path.Combine(WindowsAgentSecurity.DataDirectory, "install.pending")) &&
                !File.Exists(Path.Combine(WindowsAgentSecurity.DataDirectory, "identity.public")) &&
                !File.Exists(Path.Combine(WindowsAgentSecurity.DataDirectory, "operations.journal"))) startupStatus = LocalSetupStatus.Uninitialized;
        }
        await new LocalSetupPipe().ServeAsync(Handle, stoppingToken);
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
            journal = opened;
            identity = loaded;
        }
        catch { try { opened?.Dispose(); } finally { loaded.Dispose(); } throw; }
    }

    internal async ValueTask<LocalSetupReceipt> Handle(LocalSetupRequest request, CancellationToken cancellationToken)
    {
        const string schema = "flowplane.agent/local-setup-receipt.v1";
        try
        {
            tokenVerifier.Verify();
            if (request.Action == LocalSetupAction.Pair) return new(schema, LocalSetupStatus.PairingUnavailable);
            if (request.Action == LocalSetupAction.Initialize)
            {
                if (startupStatus != LocalSetupStatus.Uninitialized) return new(schema, LocalSetupStatus.ActionRequired);
                await Load(true, cancellationToken);
            }
            if (journal is null || identity is null) return new(schema, startupStatus);
            var summary = await journal.ReadRecoverySummaryAsync(cancellationToken);
            return new(schema, summary.RequiresReconciliation ? LocalSetupStatus.ActionRequired : LocalSetupStatus.ReadyForLocalSetup);
        }
        catch (Exception exception) when (exception is AgentWindowsException or AgentJournalException or IOException or UnauthorizedAccessException)
        { return new(schema, LocalSetupStatus.ActionRequired); }
    }

    public override void Dispose() { try { journal?.Dispose(); identity?.Dispose(); } finally { base.Dispose(); } }
}
