using System.Security.Cryptography;
using System.Text;

using FlowPlane.Agent.Protocol;

namespace FlowPlane.Agent.Runtime.Journal;

/// <summary>Local evidence, never a native-call permit. Every acknowledgement follows a durable flush.</summary>
public sealed class AgentOperationJournal : IAgentOperationJournal
{
    private const int FrameBytes = 186;
    private const int MaximumOperations = 4096;
    private const long MaximumBytes = 64 * 1024 * 1024;
    private readonly IJournalStorage storage;
    private readonly byte[] header;
    private readonly Dictionary<Guid, List<AgentJournalEntry>> operations = [];
    private readonly object gate = new();
    private byte[] chain;
    private long sequence;
    private bool faulted;
    private bool disposed;

    private AgentOperationJournal(IJournalStorage storage, Guid installationId, bool initialize)
    {
        this.storage = storage;
        header = [.. Encoding.ASCII.GetBytes("FPJRNL01"), .. installationId.ToByteArray()];
        chain = SHA256.HashData(header);
        if (initialize) { storage.Stream.Write(header); storage.Flush(); }
        else Load();
    }

    public static AgentOperationJournal Open(string path, Guid installationId, bool initialize = false)
    {
        if (installationId == Guid.Empty || !Path.IsPathFullyQualified(path)) throw Error(AgentJournalError.InvalidEvidence);
        IJournalStorage? storage = null;
        try
        {
            storage = new FileJournalStorage(path, initialize);
            return new(storage, installationId, initialize);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or AgentJournalException)
        {
            storage?.Dispose();
            throw exception is AgentJournalException known ? known : Error(AgentJournalError.StorageUnavailable);
        }
    }

    internal static AgentOperationJournal Open(IJournalStorage storage, Guid installationId)
    {
        try { return new(storage, installationId, true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw Error(AgentJournalError.StorageUnavailable); }
    }

    public ValueTask<AgentJournalEntry> AppendAcceptedAsync(AgentOperationKey key, CancellationToken cancellationToken = default) => Append(key, AgentAcknowledgementPhase.AgentAccepted, null, cancellationToken);
    public ValueTask<AgentJournalEntry> AppendAdmissionAsync(AgentOperationKey key, CancellationToken cancellationToken = default) => Append(key, AgentAcknowledgementPhase.NativeCallAdmitted, null, cancellationToken);
    public ValueTask<AgentJournalEntry> MarkMayHaveInvokedAsync(AgentOperationKey key, CancellationToken cancellationToken = default) => Append(key, AgentAcknowledgementPhase.MayHaveInvoked, null, cancellationToken);
    public ValueTask<AgentJournalEntry> RecordResultAsync(AgentOperationKey key, AgentOperationResult result, CancellationToken cancellationToken = default) => Append(key, AgentAcknowledgementPhase.NativeResultKnown, result, cancellationToken);
    public ValueTask<AgentJournalEntry> MarkCorePersistedAsync(AgentOperationKey key, CancellationToken cancellationToken = default) => Append(key, AgentAcknowledgementPhase.CoreResultCommitted, null, cancellationToken);

    private ValueTask<AgentJournalEntry> Append(AgentOperationKey key, AgentAcknowledgementPhase phase, AgentOperationResult? result, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            EnsureReady(cancellationToken);
            ValidateKey(key);
            if (operations.TryGetValue(key.OperationId, out var entries))
            {
                if (entries[0].Operation != key) throw Error(AgentJournalError.Conflict);
                var previous = entries.Find(entry => entry.Phase == phase);
                if (previous is not null)
                {
                    if (phase == AgentAcknowledgementPhase.NativeResultKnown && previous.Result != result) throw Error(AgentJournalError.Conflict);
                    return ValueTask.FromResult(previous);
                }
                if (entries[^1].Phase == AgentAcknowledgementPhase.CoreResultCommitted) throw Error(AgentJournalError.Settled);
                if ((int)phase != (int)entries[^1].Phase + 1) throw Error(AgentJournalError.OutOfOrder);
                if (phase == AgentAcknowledgementPhase.CoreResultCommitted) result = entries[^1].Result;
            }
            else
            {
                if (phase != AgentAcknowledgementPhase.AgentAccepted) throw Error(AgentJournalError.OutOfOrder);
                var context = operations.Values.Where(value => value[0].Operation.ContextId == key.ContextId).ToArray();
                if (key.Ordinal != (context.Length == 0 ? 1 : context.Max(value => value[0].Operation.Ordinal) + 1) ||
                    context.Any(value => value[^1].Phase != AgentAcknowledgementPhase.CoreResultCommitted)) throw Error(AgentJournalError.OutOfOrder);
                // Reserve four follow-up records for every accepted operation; never evict uncertainty.
                var unsettled = operations.Values.Count(value => value[^1].Phase != AgentAcknowledgementPhase.CoreResultCommitted);
                if (operations.Count >= MaximumOperations || storage.Stream.Length + ((long)unsettled + 1) * 4 * FrameBytes + FrameBytes > MaximumBytes)
                    throw Error(AgentJournalError.CapacityExceeded);
            }
            ValidateResult(phase, result);
            if (storage.Stream.Length + FrameBytes > MaximumBytes || sequence == long.MaxValue) throw Error(AgentJournalError.CapacityExceeded);
            var entry = new AgentJournalEntry(sequence + 1, key, phase, result);
            try
            {
                var bytes = Frame(entry, chain);
                storage.Stream.Position = storage.Stream.Length;
                storage.Stream.Write(bytes);
                storage.Flush();
                chain = bytes[^32..];
                sequence = entry.Sequence;
                if (entries is null) operations.Add(key.OperationId, entries = []);
                entries.Add(entry);
                return ValueTask.FromResult(entry);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                faulted = true;
                throw Error(AgentJournalError.StorageUnavailable);
            }
        }
    }

    public ValueTask<AgentRecoverySummary> ReadRecoverySummaryAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            EnsureReady(cancellationToken);
            var count = operations.Values.Count(value => value[^1].Phase != AgentAcknowledgementPhase.CoreResultCommitted);
            return ValueTask.FromResult(new AgentRecoverySummary(count, count != 0, Convert.ToHexStringLower(chain)));
        }
    }

    public ValueTask CompactSettledAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            EnsureReady(cancellationToken);
            // Keep a settled tombstone. Compaction is not permission to reuse a native operation ID.
            var retained = operations.Values.SelectMany(value => value[^1].Phase == AgentAcknowledgementPhase.CoreResultCommitted
                ? [value[^1]] : value).OrderBy(entry => entry.Sequence).ToArray();
            using var replacement = new MemoryStream();
            replacement.Write(header);
            var digest = SHA256.HashData(header);
            foreach (var entry in retained)
            {
                var frame = Frame(entry, digest);
                replacement.Write(frame);
                digest = frame[^32..];
            }
            try
            {
                storage.Replace(replacement.ToArray());
                chain = digest;
                foreach (var value in operations.Values.Where(value => value[^1].Phase == AgentAcknowledgementPhase.CoreResultCommitted))
                    value.RemoveRange(0, value.Count - 1);
                return ValueTask.CompletedTask;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                faulted = true;
                throw Error(AgentJournalError.StorageUnavailable);
            }
        }
    }

    private void Load()
    {
        var stream = storage.Stream;
        if (stream.Length < header.Length || stream.Length > MaximumBytes) throw Error(AgentJournalError.InvalidEvidence);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (!reader.ReadBytes(header.Length).AsSpan().SequenceEqual(header)) throw Error(AgentJournalError.InvalidEvidence);
        while (stream.Position < stream.Length)
        {
            if (stream.Length - stream.Position < FrameBytes) throw Error(AgentJournalError.InvalidEvidence);
            var bytes = reader.ReadBytes(FrameBytes);
            if (!SHA256.HashData(bytes.AsSpan(4, 150)).AsSpan().SequenceEqual(bytes.AsSpan(154, 32))) throw Error(AgentJournalError.InvalidEvidence);
            using var frame = new BinaryReader(new MemoryStream(bytes));
            if (frame.ReadInt32() != 182 || frame.ReadByte() != 1) throw Error(AgentJournalError.InvalidEvidence);
            var next = frame.ReadInt64();
            var key = new AgentOperationKey(new Guid(frame.ReadBytes(16)), new Guid(frame.ReadBytes(16)), frame.ReadInt32(), frame.ReadInt64(), Convert.ToHexStringLower(frame.ReadBytes(32)));
            var phase = (AgentAcknowledgementPhase)frame.ReadByte();
            var receipt = new Guid(frame.ReadBytes(16));
            var nativeId = frame.ReadInt64();
            var ticks = frame.ReadInt64();
            if (next <= sequence || !Enum.IsDefined(phase) || !frame.ReadBytes(32).AsSpan().SequenceEqual(chain)) throw Error(AgentJournalError.InvalidEvidence);
            ValidateKey(key);
            if (ticks < 0 || ticks > DateTimeOffset.MaxValue.Ticks) throw Error(AgentJournalError.InvalidEvidence);
            var result = receipt == Guid.Empty ? null : new AgentOperationResult(receipt, nativeId == 0 ? null : nativeId,
                ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero));
            if (receipt == Guid.Empty && (nativeId != 0 || ticks != 0)) throw Error(AgentJournalError.InvalidEvidence);
            ValidateResult(phase, result);
            if (!operations.TryGetValue(key.OperationId, out var entries))
            {
                if (phase is not (AgentAcknowledgementPhase.AgentAccepted or AgentAcknowledgementPhase.CoreResultCommitted) || operations.Count >= MaximumOperations)
                    throw Error(AgentJournalError.InvalidEvidence);
                operations.Add(key.OperationId, entries = []);
            }
            else if (entries[0].Operation != key || (int)phase != (int)entries[^1].Phase + 1 ||
                phase == AgentAcknowledgementPhase.CoreResultCommitted && result != entries[^1].Result)
                throw Error(AgentJournalError.InvalidEvidence);
            entries.Add(new(next, key, phase, result));
            sequence = next;
            chain = bytes[^32..];
        }
    }

    private static byte[] Frame(AgentJournalEntry entry, byte[] previous)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true);
        writer.Write(182); writer.Write((byte)1); writer.Write(entry.Sequence);
        writer.Write(entry.Operation.ContextId.ToByteArray()); writer.Write(entry.Operation.OperationId.ToByteArray());
        writer.Write(entry.Operation.Ordinal); writer.Write(entry.Operation.Fence); writer.Write(Convert.FromHexString(entry.Operation.PayloadDigest));
        writer.Write((byte)entry.Phase); writer.Write((entry.Result?.ReceiptId ?? Guid.Empty).ToByteArray());
        writer.Write(entry.Result?.NativeExecutionId ?? 0); writer.Write(entry.Result?.CreatedAtUtc?.Ticks ?? 0); writer.Write(previous);
        writer.Flush();
        var payload = bytes.ToArray();
        return [.. payload, .. SHA256.HashData(payload.AsSpan(4))];
    }

    private static void ValidateKey(AgentOperationKey key)
    {
        try { _ = AgentProtocolCodec.Fingerprint(key); }
        catch (AgentProtocolException) { throw Error(AgentJournalError.InvalidEvidence); }
    }

    private static void ValidateResult(AgentAcknowledgementPhase phase, AgentOperationResult? result)
    {
        if (phase < AgentAcknowledgementPhase.NativeResultKnown ? result is not null : result is null || result.ReceiptId == Guid.Empty ||
            (result.NativeExecutionId is null) != (result.CreatedAtUtc is null) || result.NativeExecutionId is <= 0 ||
            result.CreatedAtUtc is { } time && (time.Offset != TimeSpan.Zero || time <= DateTimeOffset.UnixEpoch))
            throw Error(AgentJournalError.InvalidEvidence);
    }

    private void EnsureReady(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (faulted || disposed) throw Error(AgentJournalError.StorageUnavailable);
    }

    private static AgentJournalException Error(AgentJournalError code) => new(code);
    public void Dispose() { lock (gate) { if (!disposed) { disposed = true; storage.Dispose(); } } }
}
