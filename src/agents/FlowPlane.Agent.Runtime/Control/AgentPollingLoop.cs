using FlowPlane.Agent.Protocol;
using FlowPlane.Agent.Runtime.Journal;
using FlowPlane.Agent.Runtime.Transport;
namespace FlowPlane.Agent.Runtime.Control;

/// <summary>Serial synthetic NoWork polling. No callback can invoke native operations.</summary>
public sealed class AgentPollingLoop(AgentControlStore store, AgentHttpsTransport transport, IAgentOperationJournal journal, string packageVersion, TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    public Guid BootId { get; } = Guid.NewGuid();
    private int inFlight;
    private long? waitStarted;
    private TimeSpan waitDuration;
    private DateTimeOffset serverAnchor;
    private long anchorTimestamp;
    private long? sendTimestamp;

    public async Task<TimeSpan> StepAsync(CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref inFlight, 1, 0) != 0) throw new AgentControlException();
        try
        {
            token.ThrowIfCancellationRequested();
            var enrollment = store.State.Enrollment;
            if (enrollment?.StatusReceipt?.Stage != PairingStage.Redeemed || enrollment.ClaimReceipt is null ||
                store.State.Origin is null || new Uri(store.State.Origin) != transport.Origin) throw new AgentControlException();
            var summary = await journal.ReadRecoverySummaryAsync(token);
            if (summary.RequiresReconciliation || summary.UnresolvedCount != 0) throw new AgentControlException();
            if (store.State.ConnectRequest is { } pending && pending.BootId == BootId && store.State.ConnectReceipt is null)
                return await Connect(pending, enrollment.ClaimReceipt.AgentId, token);
            if (store.State.ConnectReceipt is { } current && current.BootId == BootId)
            {
                if (store.State.PendingPoll is not null) return await Poll(token);
                if (EstimatedServerTime() < (store.State.LatestPoll?.LeaseExpiresAtUtc ?? current.LeaseExpiresAtUtc))
                {
                    var sequence = checked((store.State.LatestPoll?.Sequence ?? 0) + 1);
                    var poll = new AgentPollRequest("flowplane.agent/poll.v1", current.SessionId, current.Fence, sequence, Guid.NewGuid(),
                        AgentAvailability.Available, AgentReadiness.ReadyForControlPlane, 0);
                    store.Save(store.State with { PendingPoll = poll });
                    sendTimestamp = time.GetTimestamp();
                    return await Poll(token);
                }
            }
            if (waitStarted is { } started && time.GetElapsedTime(started) < waitDuration)
                return BoundWait(waitDuration - time.GetElapsedTime(started));
            waitStarted = null;
            var status = await transport.SessionStatusAsync(token);
            ValidateStatus(status, enrollment.ClaimReceipt.AgentId);
            serverAnchor = status.ServerTimeUtc; anchorTimestamp = time.GetTimestamp();
            // Reconcile an interrupted previous boot, retaining immutable evidence before waiting it out.
            var reconciled = false;
            if (store.State.ConnectRequest is { } old && store.State.ConnectReceipt is null)
            {
                await Connect(old, enrollment.ClaimReceipt.AgentId, token, recoveringPreviousBoot: true);
                reconciled = true;
            }
            if (store.State.PendingPoll is not null) { await Poll(token, recoveringPreviousBoot: true); reconciled = true; }
            if (reconciled)
            {
                // The prior request may never have committed. Its exact replay can acquire/renew the old lease.
                status = await transport.SessionStatusAsync(token);
                ValidateStatus(status, enrollment.ClaimReceipt.AgentId);
                serverAnchor = status.ServerTimeUtc; anchorTimestamp = time.GetTimestamp();
            }
            if (status.LeaseExpiresAtUtc is { } lease && lease > status.ServerTimeUtc)
            {
                waitStarted = time.GetTimestamp(); waitDuration = lease - status.ServerTimeUtc;
                return BoundWait(waitDuration);
            }
            var request = new AgentConnectRequest("flowplane.agent/connect.v1", BootId, Guid.NewGuid(), packageVersion,
                AgentProtocol.Version, status.Fence, AgentPairingSecurity.RecoveryDigest(0, false));
            store.Save(store.State with { ConnectRequest = request, ConnectReceipt = null, PendingPoll = null, LatestPoll = null });
            sendTimestamp = time.GetTimestamp();
            return await Connect(request, enrollment.ClaimReceipt.AgentId, token);
        }
        catch (Exception exception) when (exception is OverflowException or ArgumentOutOfRangeException) { throw new AgentControlException(); }
        finally { Volatile.Write(ref inFlight, 0); }
    }

    private async Task<TimeSpan> Connect(AgentConnectRequest request, Guid expectedAgent, CancellationToken token, bool recoveringPreviousBoot = false)
    {
        sendTimestamp ??= time.GetTimestamp();
        var receipt = await transport.ConnectAsync(request, token);
        if (receipt.AgentId != expectedAgent || receipt.BootId != request.BootId || receipt.Fence != checked(request.ExpectedFence + 1) ||
            receipt.RecoveryMode != AgentRecoveryMode.None || receipt.ServerTimeUtc > EstimatedServerTime().AddSeconds(15)) throw new AgentControlException();
        if (receipt.LeaseExpiresAtUtc - receipt.ServerTimeUtc > TimeSpan.FromSeconds(60)) throw new AgentControlException();
        store.Save(store.State with { ConnectReceipt = receipt });
        if (!recoveringPreviousBoot) { serverAnchor = receipt.ServerTimeUtc; anchorTimestamp = sendTimestamp.Value; }
        sendTimestamp = null;
        return IdleDelay(5);
    }

    private async Task<TimeSpan> Poll(CancellationToken token, bool recoveringPreviousBoot = false)
    {
        var request = store.State.PendingPoll ?? throw new AgentControlException();
        var original = store.State.ConnectReceipt ?? throw new AgentControlException();
        sendTimestamp ??= time.GetTimestamp();
        var receipt = await transport.PollAsync(request, token);
        var lastTime = store.State.LatestPoll?.AcknowledgedAtUtc ?? original.ServerTimeUtc;
        if (receipt.SessionId != request.SessionId || receipt.Fence != request.Fence || receipt.Sequence != request.Sequence ||
            request.Sequence != checked((store.State.LatestPoll?.Sequence ?? 0) + 1) || receipt.Work != AgentWorkKind.None ||
            receipt.AcknowledgedAtUtc < lastTime || receipt.AcknowledgedAtUtc > EstimatedServerTime().AddSeconds(15) ||
            receipt.LeaseExpiresAtUtc - receipt.AcknowledgedAtUtc > TimeSpan.FromSeconds(60)) throw new AgentControlException();
        store.Save(store.State with { LatestPoll = receipt, PendingPoll = null });
        if (!recoveringPreviousBoot) { serverAnchor = receipt.AcknowledgedAtUtc; anchorTimestamp = sendTimestamp.Value; }
        sendTimestamp = null;
        return IdleDelay(receipt.NextPollSeconds);
    }

    private DateTimeOffset EstimatedServerTime() => serverAnchor + time.GetElapsedTime(anchorTimestamp);
    private static void ValidateStatus(AgentSessionStatusReceipt status, Guid expectedAgent)
    {
        if (status.AgentId != expectedAgent || status.RecoveryMode != AgentRecoveryMode.None || status.Fence == long.MaxValue ||
            status.LeaseExpiresAtUtc is { } lease && lease - status.ServerTimeUtc > TimeSpan.FromSeconds(60)) throw new AgentControlException();
    }
    private static TimeSpan BoundWait(TimeSpan remaining) => TimeSpan.FromSeconds(Math.Clamp(remaining.TotalSeconds, 1, 15));
    private static TimeSpan IdleDelay(int seconds) => TimeSpan.FromMilliseconds(Math.Min(seconds * 1000, Random.Shared.Next(4500, 5501)));

    public async Task RunAsync(CancellationToken token)
    {
        var backoff = 1;
        while (!token.IsCancellationRequested)
        {
            TimeSpan delay;
            try { delay = await StepAsync(token); backoff = 1; }
            catch (AgentTransportException exception) when (exception.Code == AgentTransportError.Unavailable)
            { delay = TimeSpan.FromSeconds(backoff); backoff = Math.Min(15, backoff * 2); }
            await Task.Delay(delay, time, token);
        }
    }
}
