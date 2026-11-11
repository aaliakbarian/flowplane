using System.IO.Pipes;

using FlowPlane.Agent.Runtime.Setup;

namespace FlowPlane.Agent.Tests;

public sealed class AgentLocalPipeLifecycleTests
{
    [Fact]
    public async Task One_listener_handle_serves_successive_clients_even_while_old_client_handle_is_open()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(15));
        var name = "flowplane-local-test-" + Guid.NewGuid().ToString("N");
        var created = 0;
        var listener = LocalSetupConnectionLoop.ServeAsync(() =>
        {
            Interlocked.Increment(ref created);
            return new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }, async (pipe, token) =>
        {
            var request = new byte[1];
            await pipe.ReadExactlyAsync(request, token);
            await pipe.WriteAsync(request, token);
            await pipe.FlushAsync(token);
        }, stop.Token);
        try
        {
            using var first = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await first.ConnectAsync(stop.Token);
            await first.WriteAsync(new byte[] { 42 }, stop.Token);
            var reply = new byte[1];
            await first.ReadExactlyAsync(reply, stop.Token);
            Assert.Equal(42, reply[0]);
            await LocalSetupConnectionLoop.AcknowledgeReceiptAsync(first, stop.Token);
            using var second = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await second.ConnectAsync(stop.Token);
            await second.WriteAsync(new byte[] { 43 }, stop.Token);
            await second.ReadExactlyAsync(reply, stop.Token);
            Assert.Equal(43, reply[0]);
            await LocalSetupConnectionLoop.AcknowledgeReceiptAsync(second, stop.Token);
            Assert.Equal(1, Volatile.Read(ref created));
        }
        finally
        {
            await stop.CancelAsync();
            try { await listener; } catch (OperationCanceledException) { }
        }
    }
}
