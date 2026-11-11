using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using FlowPlane.Agent.Runtime.Journal;
using FlowPlane.Agent.Runtime.Setup;
using FlowPlane.Agent.Windows.Identity;
using FlowPlane.Agent.Windows.Security;

namespace FlowPlane.Agent.Windows.Tests;

// This test injects local I/O/token boundaries; it does not claim a real Windows token or CNG proof.
public sealed class WorkerInitializationTests
{
    [Fact]
    [Trait("Category", "PortableBoundary")]
    public async Task Failed_initialization_cleanup_never_publishes_ready_state_or_leaks_journal_ownership()
    {
        var directory = Path.Combine(Path.GetTempPath(), "flowplane-init-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "operation.journal");
        var id = Guid.NewGuid();
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Synthetic Initialization", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        try
        {
            using var worker = new AgentWorker(new SyntheticIdentity(id, certificate),
                (installation, initialize) => AgentOperationJournal.Open(path, installation, initialize),
                () => throw new IOException("synthetic-private-cleanup-error"), new SyntheticToken());
            await Assert.ThrowsAsync<IOException>(async () => await worker.Load(true, TestContext.Current.CancellationToken));
            var status = await worker.Handle(new(LocalSetupAction.Status), TestContext.Current.CancellationToken);
            Assert.Equal(LocalSetupStatus.ActionRequired, status.Status);
            using var reopened = AgentOperationJournal.Open(path, id);
            Assert.Equal(0, (await reopened.ReadRecoverySummaryAsync(TestContext.Current.CancellationToken)).UnresolvedCount);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class SyntheticIdentity(Guid id, X509Certificate2 certificate) : IWindowsAgentIdentity
    {
        public ValueTask<WindowsAgentCertificate> LoadOrCreateAsync(bool explicitInstall, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new WindowsAgentCertificate(id, certificate));
    }
    private sealed class SyntheticToken : IServiceTokenVerifier { public void Verify() { } }
}
