using System.Net;
using FlowPlane.Agent.Protocol;
using FlowPlane.Agent.Runtime.Control;
using FlowPlane.Agent.Runtime.Journal;
using FlowPlane.Agent.Runtime.Transport;

namespace FlowPlane.Agent.Tests;

public sealed class AgentPollingTests
{
    [Fact]
    public async Task Lost_connect_and_poll_acks_replay_durable_requests_and_keep_original_receipt()
    {
        using var fixture = new AgentControlFixture();
        PreparePaired(fixture);
        using var journal = AgentOperationJournal.Open(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(fixture.Path)!, "operations.journal"), fixture.Installation, true);
        var clock = new ManualClock();
        AgentConnectRequest? connect = null;
        AgentPollRequest? poll = null;
        var session = Guid.NewGuid();
        var connects = 0;
        var polls = 0;
        using var transport = fixture.Transport((_, _) => throw new InvalidOperationException(), async (request, token) =>
        {
            if (request.Method == HttpMethod.Get) return AgentControlFixture.Reply(new AgentSessionStatusReceipt("flowplane.agent/session-status.v1", fixture.Agent, 1, AgentControlFixture.Now, null, AgentRecoveryMode.None));
            if (request.RequestUri!.AbsolutePath.EndsWith("sessions", StringComparison.Ordinal))
            {
                var current = AgentProtocolCodec.Decode<AgentConnectRequest>(await request.Content!.ReadAsByteArrayAsync(token));
                Assert.Equal(current, fixture.Store.State.ConnectRequest);
                if (connects++ == 0) { connect = current; throw new HttpRequestException("lost-connect"); }
                Assert.Equal(connect, current);
                return AgentControlFixture.Reply(new AgentSessionReceipt("flowplane.agent/session-receipt.v1", fixture.Agent, session, current.BootId, 2, 1,
                    AgentControlFixture.Now, AgentControlFixture.Now.AddSeconds(60), AgentRecoveryMode.None));
            }
            var input = AgentProtocolCodec.Decode<AgentPollRequest>(await request.Content!.ReadAsByteArrayAsync(token));
            Assert.Equal(input, fixture.Store.State.PendingPoll);
            if (polls++ == 0) { poll = input; throw new HttpRequestException("lost-poll"); }
            Assert.Equal(poll, input);
            return AgentControlFixture.Reply(new AgentPollReceipt("flowplane.agent/poll-receipt.v1", session, 2, input.Sequence,
                AgentControlFixture.Now.AddSeconds(5), AgentControlFixture.Now.AddSeconds(65), 5, AgentWorkKind.None));
        });
        var loop = new AgentPollingLoop(fixture.Store, transport, journal, "1.0.0", clock);
        await Assert.ThrowsAsync<AgentTransportException>(() => loop.StepAsync(TestContext.Current.CancellationToken));
        await loop.StepAsync(TestContext.Current.CancellationToken);
        var original = fixture.Store.State.ConnectReceipt;
        await Assert.ThrowsAsync<AgentTransportException>(() => loop.StepAsync(TestContext.Current.CancellationToken));
        var delay = await loop.StepAsync(TestContext.Current.CancellationToken);
        Assert.InRange(delay.TotalSeconds, 1, 15);
        Assert.Equal(1, fixture.Store.State.LatestPoll!.Sequence);
        Assert.Null(fixture.Store.State.PendingPoll);
        Assert.Equal(original, fixture.Store.State.ConnectReceipt);
        fixture.Reopen();
        Assert.Equal(original, fixture.Store.State.ConnectReceipt);
        Assert.Equal(1, fixture.Store.State.LatestPoll!.Sequence);
    }

    [Fact]
    public async Task Restart_waits_using_server_remaining_lease_and_monotonic_time()
    {
        using var fixture = new AgentControlFixture();
        PreparePaired(fixture);
        using var journal = AgentOperationJournal.Open(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(fixture.Path)!, "operations.journal"), fixture.Installation, true);
        var clock = new ManualClock();
        var requests = 0;
        using var transport = fixture.Transport((_, _) => throw new InvalidOperationException(), (request, _) =>
        {
            requests++;
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(AgentControlFixture.Reply(new AgentSessionStatusReceipt("flowplane.agent/session-status.v1", fixture.Agent, 3,
                AgentControlFixture.Now, AgentControlFixture.Now.AddSeconds(60), AgentRecoveryMode.None)));
        });
        var loop = new AgentPollingLoop(fixture.Store, transport, journal, "1.0.0", clock);
        Assert.Equal(15, (await loop.StepAsync(TestContext.Current.CancellationToken)).TotalSeconds);
        clock.Advance(TimeSpan.FromSeconds(14));
        Assert.Equal(15, (await loop.StepAsync(TestContext.Current.CancellationToken)).TotalSeconds);
        Assert.Equal(1, requests);
        Assert.Null(fixture.Store.State.ConnectRequest);
    }

    [Fact]
    public async Task Restart_rechecks_lease_after_an_old_connect_was_not_previously_committed()
    {
        using var fixture = new AgentControlFixture();
        PreparePaired(fixture);
        var oldBoot = Guid.NewGuid();
        var oldRequest = new AgentConnectRequest("flowplane.agent/connect.v1", oldBoot, Guid.NewGuid(), "1.0.0", 1, 1, AgentPairingSecurity.RecoveryDigest(0, false));
        fixture.Store.Save(fixture.Store.State with { ConnectRequest = oldRequest });
        using var journal = AgentOperationJournal.Open(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(fixture.Path)!, "operations.journal"), fixture.Installation, true);
        var acquired = false;
        var connects = 0;
        using var transport = fixture.Transport((_, _) => throw new InvalidOperationException(), async (request, token) =>
        {
            if (request.Method == HttpMethod.Get) return AgentControlFixture.Reply(new AgentSessionStatusReceipt("flowplane.agent/session-status.v1", fixture.Agent, acquired ? 2 : 1,
                AgentControlFixture.Now, acquired ? AgentControlFixture.Now.AddSeconds(60) : null, AgentRecoveryMode.None));
            var connect = AgentProtocolCodec.Decode<AgentConnectRequest>(await request.Content!.ReadAsByteArrayAsync(token));
            Assert.Equal(oldRequest, connect);
            connects++; acquired = true;
            return AgentControlFixture.Reply(new AgentSessionReceipt("flowplane.agent/session-receipt.v1", fixture.Agent, Guid.NewGuid(), oldBoot, 2, 1, AgentControlFixture.Now, AgentControlFixture.Now.AddSeconds(60), AgentRecoveryMode.None));
        });
        var loop = new AgentPollingLoop(fixture.Store, transport, journal, "1.0.0", new ManualClock());
        Assert.Equal(15, (await loop.StepAsync(TestContext.Current.CancellationToken)).TotalSeconds);
        Assert.Equal(1, connects);
        Assert.Equal(oldBoot, fixture.Store.State.ConnectReceipt!.BootId);
        Assert.NotEqual(oldBoot, loop.BootId);
    }

    [Fact]
    public async Task Unresolved_journal_never_emits_false_readiness()
    {
        using var fixture = new AgentControlFixture();
        PreparePaired(fixture);
        using var journal = AgentOperationJournal.Open(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(fixture.Path)!, "operations.journal"), fixture.Installation, true);
        await journal.AppendAcceptedAsync(new(Guid.NewGuid(), Guid.NewGuid(), 1, 1, new string('a', 64)), TestContext.Current.CancellationToken);
        using var transport = fixture.Transport((_, _) => throw new InvalidOperationException(), (_, _) => throw new InvalidOperationException("must-not-transmit-unsettled"));
        await Assert.ThrowsAsync<AgentControlException>(() => new AgentPollingLoop(fixture.Store, transport, journal, "1.0.0").StepAsync(TestContext.Current.CancellationToken));
        Assert.Null(fixture.Store.State.ConnectRequest);
    }

    [Fact]
    public async Task A_second_step_is_refused_while_first_request_is_in_flight()
    {
        using var fixture = new AgentControlFixture();
        PreparePaired(fixture);
        using var journal = AgentOperationJournal.Open(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(fixture.Path)!, "operations.journal"), fixture.Installation, true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = fixture.Transport((_, _) => throw new InvalidOperationException(), async (request, token) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
                return AgentControlFixture.Reply(new AgentSessionStatusReceipt("flowplane.agent/session-status.v1", fixture.Agent, 1, AgentControlFixture.Now, null, AgentRecoveryMode.None));
            }
            var connect = AgentProtocolCodec.Decode<AgentConnectRequest>(await request.Content!.ReadAsByteArrayAsync(token));
            return AgentControlFixture.Reply(new AgentSessionReceipt("flowplane.agent/session-receipt.v1", fixture.Agent, Guid.NewGuid(), connect.BootId, 2, 1, AgentControlFixture.Now, AgentControlFixture.Now.AddSeconds(60), AgentRecoveryMode.None));
        });
        var loop = new AgentPollingLoop(fixture.Store, transport, journal, "1.0.0", new ManualClock());
        var first = loop.StepAsync(TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        try { await Assert.ThrowsAsync<AgentControlException>(() => loop.StepAsync(TestContext.Current.CancellationToken)); }
        finally { release.SetResult(); }
        await first;
        Assert.NotNull(fixture.Store.State.ConnectReceipt);
    }

    [Theory]
    [InlineData("sequence")]
    [InlineData("session")]
    [InlineData("fence")]
    [InlineData("future-time")]
    public async Task Changed_or_future_ack_never_advances_durable_sequence(string change)
    {
        using var fixture = new AgentControlFixture();
        PreparePaired(fixture);
        using var journal = AgentOperationJournal.Open(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(fixture.Path)!, "operations.journal"), fixture.Installation, true);
        var session = Guid.NewGuid();
        using var transport = fixture.Transport((_, _) => throw new InvalidOperationException(), async (request, token) =>
        {
            if (request.Method == HttpMethod.Get) return AgentControlFixture.Reply(new AgentSessionStatusReceipt("flowplane.agent/session-status.v1", fixture.Agent, 1, AgentControlFixture.Now, null, AgentRecoveryMode.None));
            if (request.RequestUri!.AbsolutePath.EndsWith("sessions", StringComparison.Ordinal))
            {
                var connect = AgentProtocolCodec.Decode<AgentConnectRequest>(await request.Content!.ReadAsByteArrayAsync(token));
                return AgentControlFixture.Reply(new AgentSessionReceipt("flowplane.agent/session-receipt.v1", fixture.Agent, session, connect.BootId, 2, 1, AgentControlFixture.Now, AgentControlFixture.Now.AddSeconds(60), AgentRecoveryMode.None));
            }
            return AgentControlFixture.Reply(new AgentPollReceipt("flowplane.agent/poll-receipt.v1", change == "session" ? Guid.NewGuid() : session,
                change == "fence" ? 3 : 2, change == "sequence" ? 2 : 1,
                change == "future-time" ? AgentControlFixture.Now.AddDays(1) : AgentControlFixture.Now.AddSeconds(5),
                change == "future-time" ? AgentControlFixture.Now.AddDays(1).AddSeconds(60) : AgentControlFixture.Now.AddSeconds(65), 5, AgentWorkKind.None));
        });
        var loop = new AgentPollingLoop(fixture.Store, transport, journal, "1.0.0", new ManualClock());
        await loop.StepAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<AgentControlException>(() => loop.StepAsync(TestContext.Current.CancellationToken));
        Assert.Null(fixture.Store.State.LatestPoll);
        Assert.NotNull(fixture.Store.State.PendingPoll);
    }

    internal static void PreparePaired(AgentControlFixture fixture)
    {
        var claim = fixture.Claim(Guid.NewGuid(), Guid.NewGuid());
        fixture.Store.Save(fixture.Store.State with { Origin = "https://localhost/", Enrollment = new(claim.GrantId, claim.ClaimId, Guid.NewGuid(), claim.PinSha256, claim, fixture.Status(claim, PairingStage.Redeemed)) });
    }

    [Fact]
    public async Task Failure_to_persist_poll_ack_retains_the_exact_pending_request()
    {
        using var fixture = new AgentControlFixture();
        PreparePaired(fixture);
        using var journal = AgentOperationJournal.Open(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(fixture.Path)!, "operations.journal"), fixture.Installation, true);
        var session = Guid.NewGuid();
        AgentPollRequest? sent = null;
        using var transport = fixture.Transport((_, _) => throw new InvalidOperationException(), async (request, token) =>
        {
            if (request.Method == HttpMethod.Get) return AgentControlFixture.Reply(new AgentSessionStatusReceipt("flowplane.agent/session-status.v1", fixture.Agent, 1, AgentControlFixture.Now, null, AgentRecoveryMode.None));
            if (request.RequestUri!.AbsolutePath.EndsWith("sessions", StringComparison.Ordinal))
            {
                var connect = AgentProtocolCodec.Decode<AgentConnectRequest>(await request.Content!.ReadAsByteArrayAsync(token));
                return AgentControlFixture.Reply(new AgentSessionReceipt("flowplane.agent/session-receipt.v1", fixture.Agent, session, connect.BootId, 2, 1, AgentControlFixture.Now, AgentControlFixture.Now.AddSeconds(60), AgentRecoveryMode.None));
            }
            sent = AgentProtocolCodec.Decode<AgentPollRequest>(await request.Content!.ReadAsByteArrayAsync(token));
            Directory.CreateDirectory(fixture.Path + ".next");
            return AgentControlFixture.Reply(new AgentPollReceipt("flowplane.agent/poll-receipt.v1", session, 2, 1, AgentControlFixture.Now.AddSeconds(5), AgentControlFixture.Now.AddSeconds(65), 5, AgentWorkKind.None));
        });
        var loop = new AgentPollingLoop(fixture.Store, transport, journal, "1.0.0", new ManualClock());
        await loop.StepAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<AgentControlException>(() => loop.StepAsync(TestContext.Current.CancellationToken));
        Assert.Equal(sent, fixture.Store.State.PendingPoll);
        Assert.Null(fixture.Store.State.LatestPoll);
    }

    internal sealed class ManualClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public override DateTimeOffset GetUtcNow() => AgentControlFixture.Now.AddTicks(timestamp);
        public void Advance(TimeSpan duration) => timestamp += duration.Ticks;
    }
}
