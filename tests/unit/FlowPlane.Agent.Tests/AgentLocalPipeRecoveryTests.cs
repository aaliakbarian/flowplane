using FlowPlane.Agent.Runtime.Setup;

namespace FlowPlane.Agent.Tests;

public sealed class AgentLocalPipeRecoveryTests
{
    [Fact]
    public async Task Receipt_consumption_acknowledgement_precedes_disconnect()
    {
        using var stop = Deadline();
        var stream = new ExchangeStream { HoldAcknowledgement = true };
        var connection = new Connection([stream]);
        var listener = Serve(() => connection, WriteReceipt, stop.Token);
        try
        {
            var first = await Task.WhenAny(stream.AcknowledgementRead.Task, connection.NextListen.Task).WaitAsync(stop.Token);
            Assert.Same(stream.AcknowledgementRead.Task, first);
            Assert.Equal(0, connection.Disconnects);
            Assert.False(connection.NextListen.Task.IsCompleted);
            stream.ReleaseAcknowledgement.TrySetResult();
            await connection.NextListen.Task.WaitAsync(stop.Token);
            Assert.Equal(new byte[] { 42 }, stream.Written.ToArray());
            Assert.Equal(1, connection.Disconnects);
        }
        finally { await Shutdown(listener, stop); }
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("flush")]
    [InlineData("ack")]
    public async Task Broken_client_at_each_boundary_does_not_block_next_client(string boundary)
    {
        using var stop = Deadline();
        var broken = new ExchangeStream { Fault = boundary };
        var next = new ExchangeStream();
        var connection = new Connection([broken, next]);
        broken.Broken = () => connection.Connected = false;
        var calls = 0;
        var listener = Serve(() => connection, async (stream, token) =>
        {
            Interlocked.Increment(ref calls);
            if (ReferenceEquals(stream, broken) && boundary == "read")
                await stream.ReadExactlyAsync(new byte[1], token);
            await WriteReceipt(stream, token);
        }, stop.Token);
        try
        {
            await next.AcknowledgementConsumed.Task.WaitAsync(stop.Token);
            Assert.Equal(new byte[] { 42 }, next.Written.ToArray());
            Assert.Equal(2, calls);
            Assert.True(connection.Disconnects >= 1);
        }
        finally { await Shutdown(listener, stop); }
    }

    [Fact]
    public async Task Callback_handling_broken_io_still_requires_reset_before_next_connection()
    {
        using var stop = Deadline();
        var broken = new ExchangeStream { Fault = "write" };
        var next = new ExchangeStream();
        var connection = new Connection([broken, next]);
        broken.Broken = () => connection.Connected = false;
        var listener = Serve(() => connection, async (stream, token) =>
        {
            try { await WriteReceipt(stream, token); }
            catch (IOException) when (ReferenceEquals(stream, broken)) { }
        }, stop.Token);
        try
        {
            var completed = await Task.WhenAny(listener, next.AcknowledgementConsumed.Task).WaitAsync(stop.Token);
            if (ReferenceEquals(completed, listener)) await listener;
            Assert.Equal(new byte[] { 42 }, next.Written.ToArray());
            Assert.True(connection.Disconnects >= 1);
        }
        finally { await Shutdown(listener, stop); }
    }

    [Fact]
    public async Task Lost_acknowledgement_never_replays_the_mutation_and_status_recovery_can_connect()
    {
        using var stop = Deadline();
        var lost = new ExchangeStream { Fault = "ack" };
        var status = new ExchangeStream();
        var connection = new Connection([lost, status]);
        lost.Broken = () => connection.Connected = false;
        var mutations = 0;
        var listener = Serve(() => connection, async (stream, token) =>
        {
            // First client requests initialization; the next explicitly requests read-only status.
            if (ReferenceEquals(stream, lost)) Interlocked.Increment(ref mutations);
            await WriteReceipt(stream, token);
        }, stop.Token);
        try
        {
            await status.AcknowledgementConsumed.Task.WaitAsync(stop.Token);
            Assert.Equal(1, mutations);
            Assert.Equal(new byte[] { 42 }, status.Written.ToArray());
        }
        finally { await Shutdown(listener, stop); }
    }

    [Fact]
    public async Task Known_disconnect_cleanup_failure_recreates_owned_listener_for_next_client()
    {
        using var stop = Deadline();
        var first = new Connection([new ExchangeStream()]) { DisconnectFailure = new IOException("synthetic", unchecked((int)0x800700E9)) };
        var nextStream = new ExchangeStream();
        var second = new Connection([nextStream]);
        var created = 0;
        var listener = Serve(() => ++created == 1 ? first : second, WriteReceipt, stop.Token);
        try
        {
            await nextStream.AcknowledgementConsumed.Task.WaitAsync(stop.Token);
            Assert.True(first.Disposed);
            Assert.Equal(new byte[] { 42 }, nextStream.Written.ToArray());
            Assert.Equal(2, created);
        }
        finally { await Shutdown(listener, stop); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shutdown_disposes_listener_and_cancels_pending_connection_or_acknowledgement(bool accepted)
    {
        using var stop = Deadline();
        var stream = new ExchangeStream { HoldAcknowledgement = true };
        var connection = new Connection(accepted ? [stream] : []);
        var listener = Serve(() => connection, WriteReceipt, stop.Token);
        if (accepted) await stream.AcknowledgementRead.Task.WaitAsync(stop.Token);
        else await connection.NextListen.Task.WaitAsync(stop.Token);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listener);
        Assert.True(connection.Disposed);
        Assert.Equal(accepted ? 1 : 0, connection.Disconnects);
    }

    [Fact]
    public async Task Missing_acknowledgement_expires_then_next_client_can_complete_without_replay()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(15));
        var lost = new ExchangeStream { HoldAcknowledgement = true };
        var status = new ExchangeStream();
        var connection = new Connection([lost, status]);
        var calls = 0;
        var listener = Serve(() => connection, async (stream, token) =>
        {
            Interlocked.Increment(ref calls);
            await WriteReceipt(stream, token);
        }, stop.Token);
        try
        {
            await status.AcknowledgementConsumed.Task.WaitAsync(stop.Token);
            Assert.Equal(2, calls);
            Assert.Equal(new byte[] { 42 }, status.Written.ToArray());
            Assert.Equal(1, lost.Written.Length);
        }
        finally { await Shutdown(listener, stop); }
    }

    [Fact]
    public async Task Failure_to_recreate_exclusive_listener_stops_without_retry_or_raw_diagnostics()
    {
        using var stop = Deadline();
        var connection = new Connection([new ExchangeStream()]) { DisconnectFailure = new IOException("synthetic", unchecked((int)0x800700E9)) };
        var calls = 0;
        var listener = Serve(() => ++calls == 1 ? connection : throw new IOException("synthetic-private-canary"), WriteReceipt, stop.Token);
        var error = await Assert.ThrowsAsync<LocalSetupException>(() => listener);
        Assert.DoesNotContain("synthetic-private-canary", error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        Assert.Equal(2, calls);
        Assert.True(connection.Disposed);
    }

    [Fact]
    public async Task Invalid_acknowledgement_is_not_success_and_does_not_block_next_client()
    {
        using var stop = Deadline();
        var invalid = new ExchangeStream { Acknowledgement = 99 };
        var next = new ExchangeStream();
        var connection = new Connection([invalid, next]);
        var listener = Serve(() => connection, WriteReceipt, stop.Token);
        try
        {
            await next.AcknowledgementConsumed.Task.WaitAsync(stop.Token);
            Assert.False(invalid.AcknowledgementConsumed.Task.IsCompleted);
            Assert.Equal(new byte[] { 42 }, next.Written.ToArray());
        }
        finally { await Shutdown(listener, stop); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unexpected_infrastructure_or_security_failure_stops_safely_without_raw_diagnostics(bool cleanup)
    {
        using var stop = Deadline();
        const string canary = "synthetic-private-canary";
        var connection = new Connection([new ExchangeStream()]);
        if (cleanup) connection.DisconnectFailure = new IOException(canary, unchecked((int)0x80070070));
        var listener = Serve(() => connection, cleanup ? WriteReceipt : (_, _) => throw new UnauthorizedAccessException(canary), stop.Token);
        var error = await Assert.ThrowsAsync<LocalSetupException>(() => listener);
        Assert.DoesNotContain(canary, error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        Assert.True(connection.Disposed);
    }

    private static Task Serve(Func<ILocalSetupConnection> create, Func<Stream, CancellationToken, Task> exchange, CancellationToken token) =>
        LocalSetupConnectionLoop.ServeAsync(create, exchange, token);

    private static async Task WriteReceipt(Stream stream, CancellationToken token)
    {
        await stream.WriteAsync(new byte[] { 42 }, token);
        await stream.FlushAsync(token);
    }

    private static CancellationTokenSource Deadline()
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        source.CancelAfter(TimeSpan.FromSeconds(5));
        return source;
    }

    private static async Task Shutdown(Task listener, CancellationTokenSource stop)
    {
        await stop.CancelAsync();
        try { await listener; } catch (OperationCanceledException) { }
    }

    // Windows-only broken-state/reset failures are injected below the real connection lifecycle.
    private sealed class Connection(ExchangeStream[] streams) : ILocalSetupConnection
    {
        private int accepted;
        private bool needsReset;
        public bool Connected { get; set; }
        public Stream Stream => streams[accepted - 1];
        public int Disconnects { get; private set; }
        public bool Disposed { get; private set; }
        public IOException? DisconnectFailure { get; set; }
        public TaskCompletionSource NextListen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WaitForConnectionAsync(CancellationToken token)
        {
            if (needsReset) throw new InvalidOperationException("Previous broken connection was not reset.");
            if (accepted >= streams.Length)
            {
                NextListen.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            accepted++;
            needsReset = true;
            Connected = true;
        }

        public void Disconnect()
        {
            Disconnects++;
            if (DisconnectFailure is not null) throw DisconnectFailure;
            Connected = false;
            needsReset = false;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class ExchangeStream : Stream
    {
        public string? Fault { get; init; }
        public Action? Broken { get; set; }
        public bool HoldAcknowledgement { get; init; }
        public byte Acknowledgement { get; init; } = 6;
        public MemoryStream Written { get; } = new();
        public TaskCompletionSource AcknowledgementRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AcknowledgementConsumed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseAcknowledgement { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override Task FlushAsync(CancellationToken token) { Fail("flush"); return Task.CompletedTask; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            Fail("read");
            Fail("ack");
            AcknowledgementRead.TrySetResult();
            if (HoldAcknowledgement) await ReleaseAcknowledgement.Task.WaitAsync(token);
            buffer.Span[0] = Acknowledgement;
            if (Acknowledgement == 6) AcknowledgementConsumed.TrySetResult();
            return 1;
        }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            Fail("write");
            Written.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
        private void Fail(string boundary)
        {
            if (Fault != boundary) return;
            Broken?.Invoke();
            throw new IOException("synthetic-client-disconnected", unchecked((int)0x8007006D));
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
