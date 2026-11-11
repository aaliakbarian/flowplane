using FlowPlane.Agent.Protocol;

namespace FlowPlane.Agent.Runtime.Journal;

public enum AgentJournalError { InvalidEvidence, StorageUnavailable, CapacityExceeded, Conflict, OutOfOrder, Settled }
public sealed class AgentJournalException(AgentJournalError code) : Exception("The local operation evidence is unavailable or inconsistent.")
{
    public AgentJournalError Code { get; } = code;
}

// No arbitrary payload, parameter values, diagnostics or command text can enter the journal.
public sealed record AgentOperationResult(Guid ReceiptId, long? NativeExecutionId, DateTimeOffset? CreatedAtUtc);
public sealed record AgentJournalEntry(long Sequence, AgentOperationKey Operation, AgentAcknowledgementPhase Phase, AgentOperationResult? Result);
public sealed record AgentRecoverySummary(int UnresolvedCount, bool RequiresReconciliation, string Digest);

public interface IAgentOperationJournal : IDisposable
{
    ValueTask<AgentJournalEntry> AppendAcceptedAsync(AgentOperationKey key, CancellationToken cancellationToken = default);
    ValueTask<AgentJournalEntry> AppendAdmissionAsync(AgentOperationKey key, CancellationToken cancellationToken = default);
    ValueTask<AgentJournalEntry> MarkMayHaveInvokedAsync(AgentOperationKey key, CancellationToken cancellationToken = default);
    ValueTask<AgentJournalEntry> RecordResultAsync(AgentOperationKey key, AgentOperationResult result, CancellationToken cancellationToken = default);
    ValueTask<AgentJournalEntry> MarkCorePersistedAsync(AgentOperationKey key, CancellationToken cancellationToken = default);
    ValueTask<AgentRecoverySummary> ReadRecoverySummaryAsync(CancellationToken cancellationToken = default);
    ValueTask CompactSettledAsync(CancellationToken cancellationToken = default);
}
