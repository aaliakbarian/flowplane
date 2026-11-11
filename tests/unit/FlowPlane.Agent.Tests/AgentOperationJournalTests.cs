using FlowPlane.Agent.Protocol;
using FlowPlane.Agent.Runtime.Journal;

namespace FlowPlane.Agent.Tests;

public sealed class AgentOperationJournalTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "flowplane-journal-" + Guid.NewGuid().ToString("N"));
    private readonly Guid installation = Guid.NewGuid();
    private string JournalPath => Path.Combine(directory, "operations.journal");
    private static AgentOperationKey Key() => new(Guid.NewGuid(), Guid.NewGuid(), 1, 1, new string('a', 64));

    public AgentOperationJournalTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task Reopen_preserves_exact_native_result_and_immutable_acknowledgements()
    {
        var key = Key();
        var result = new AgentOperationResult(Guid.NewGuid(), 9_223_372_036_854_775_806L, DateTimeOffset.Parse("2026-11-10T12:00:00Z"));
        AgentJournalEntry accepted;
        using (var journal = AgentOperationJournal.Open(JournalPath, installation, initialize: true))
        {
            accepted = await journal.AppendAcceptedAsync(key, TestContext.Current.CancellationToken);
            Assert.Equal(accepted, await journal.AppendAcceptedAsync(key, TestContext.Current.CancellationToken));
            await journal.AppendAdmissionAsync(key, TestContext.Current.CancellationToken);
            await journal.MarkMayHaveInvokedAsync(key, TestContext.Current.CancellationToken);
            await journal.RecordResultAsync(key, result, TestContext.Current.CancellationToken);
        }
        using var reopened = AgentOperationJournal.Open(JournalPath, installation);
        Assert.Equal(accepted, await reopened.AppendAcceptedAsync(key, TestContext.Current.CancellationToken));
        Assert.Equal(result, (await reopened.RecordResultAsync(key, result, TestContext.Current.CancellationToken)).Result);
        Assert.Equal(1, (await reopened.ReadRecoverySummaryAsync(TestContext.Current.CancellationToken)).UnresolvedCount);
    }

    [Fact]
    public async Task Conflicting_identity_digest_fence_result_and_reordered_phases_fail_closed()
    {
        var key = Key();
        using var journal = AgentOperationJournal.Open(JournalPath, installation, initialize: true);
        await Assert.ThrowsAsync<AgentJournalException>(async () => await journal.AppendAdmissionAsync(key, TestContext.Current.CancellationToken));
        await journal.AppendAcceptedAsync(key, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<AgentJournalException>(async () => await journal.AppendAcceptedAsync(key with { Fence = 2 }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<AgentJournalException>(async () => await journal.AppendAcceptedAsync(key with { PayloadDigest = new string('b', 64) }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<AgentJournalException>(async () => await journal.MarkMayHaveInvokedAsync(key, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<AgentJournalException>(async () => await journal.AppendAcceptedAsync(key with { OperationId = Guid.NewGuid(), Ordinal = 2 }, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(35)]
    public async Task Truncated_tail_is_preserved_and_never_resumed(int missingBytes)
    {
        using (var journal = AgentOperationJournal.Open(JournalPath, installation, initialize: true))
            await journal.AppendAcceptedAsync(Key(), TestContext.Current.CancellationToken);
        using (var file = new FileStream(JournalPath, FileMode.Open, FileAccess.Write))
            file.SetLength(file.Length - missingBytes);
        var before = File.ReadAllBytes(JournalPath);
        Assert.Throws<AgentJournalException>(() => AgentOperationJournal.Open(JournalPath, installation));
        Assert.Equal(before, File.ReadAllBytes(JournalPath));
    }

    [Fact]
    public async Task Corruption_wrong_installation_and_missing_evidence_cannot_initialize_or_repair()
    {
        Assert.Throws<AgentJournalException>(() => AgentOperationJournal.Open(JournalPath, installation));
        using (var journal = AgentOperationJournal.Open(JournalPath, installation, initialize: true))
            await journal.AppendAcceptedAsync(Key(), TestContext.Current.CancellationToken);
        Assert.Throws<AgentJournalException>(() => AgentOperationJournal.Open(JournalPath, Guid.NewGuid()));
        Assert.Throws<AgentJournalException>(() => AgentOperationJournal.Open(JournalPath, installation, initialize: true));
        var bytes = File.ReadAllBytes(JournalPath);
        bytes[^1] ^= 1;
        File.WriteAllBytes(JournalPath, bytes);
        Assert.Throws<AgentJournalException>(() => AgentOperationJournal.Open(JournalPath, installation));
        Assert.Equal(bytes, File.ReadAllBytes(JournalPath));
    }

    [Fact]
    public async Task Exclusive_writer_and_cancellation_do_not_discard_evidence()
    {
        using var journal = AgentOperationJournal.Open(JournalPath, installation, initialize: true);
        Assert.Throws<AgentJournalException>(() => AgentOperationJournal.Open(JournalPath, installation));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await journal.AppendAcceptedAsync(Key(), cancelled.Token));
        Assert.Equal(0, (await journal.ReadRecoverySummaryAsync(TestContext.Current.CancellationToken)).UnresolvedCount);
        await journal.AppendAcceptedAsync(Key(), TestContext.Current.CancellationToken);
        Assert.Equal(1, (await journal.ReadRecoverySummaryAsync(TestContext.Current.CancellationToken)).UnresolvedCount);
    }

    [Fact]
    public async Task Compaction_retains_unsettled_evidence_and_prevents_settled_operation_reuse()
    {
        var settled = Key();
        var pending = Key();
        using (var journal = AgentOperationJournal.Open(JournalPath, installation, initialize: true))
        {
            await journal.AppendAcceptedAsync(settled, TestContext.Current.CancellationToken);
            await journal.AppendAdmissionAsync(settled, TestContext.Current.CancellationToken);
            await journal.MarkMayHaveInvokedAsync(settled, TestContext.Current.CancellationToken);
            await journal.RecordResultAsync(settled, new(Guid.NewGuid(), null, null), TestContext.Current.CancellationToken);
            await journal.MarkCorePersistedAsync(settled, TestContext.Current.CancellationToken);
            await journal.AppendAcceptedAsync(pending, TestContext.Current.CancellationToken);
            await journal.AppendAdmissionAsync(pending, TestContext.Current.CancellationToken);
            var before = new FileInfo(JournalPath).Length;
            await journal.CompactSettledAsync(TestContext.Current.CancellationToken);
            Assert.True(new FileInfo(JournalPath).Length < before);
        }
        using var reopened = AgentOperationJournal.Open(JournalPath, installation);
        var summary = await reopened.ReadRecoverySummaryAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, summary.UnresolvedCount);
        Assert.True(summary.RequiresReconciliation);
        await Assert.ThrowsAsync<AgentJournalException>(async () => await reopened.AppendAcceptedAsync(settled, TestContext.Current.CancellationToken));
        await reopened.MarkMayHaveInvokedAsync(pending, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Frame_payload_contains_only_typed_evidence_and_diagnostics_are_sanitized()
    {
        using var journal = AgentOperationJournal.Open(JournalPath, installation, initialize: true);
        var exception = await Assert.ThrowsAsync<AgentJournalException>(async () => await journal.AppendAcceptedAsync(
            Key() with { PayloadDigest = "synthetic-sensitive-invalid-input" }, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("synthetic-sensitive", exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
        await journal.AppendAcceptedAsync(Key(), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("synthetic-sensitive", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(JournalPath)), StringComparison.Ordinal);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
