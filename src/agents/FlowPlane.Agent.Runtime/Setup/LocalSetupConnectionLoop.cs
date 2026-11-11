using System.IO.Pipes;
using System.Net.Sockets;
using System.Security;

namespace FlowPlane.Agent.Runtime.Setup;

/// <summary>Own the local listening handle; callbacks handle only an already-connected bounded setup exchange.</summary>
public static class LocalSetupConnectionLoop
{
    public static Task ServeAsync(Func<NamedPipeServerStream> create, Func<NamedPipeServerStream, CancellationToken, Task> exchange, CancellationToken cancellationToken) =>
        ServeAsync(() => new PipeConnection(create()), (stream, token) => exchange((NamedPipeServerStream)stream, token), cancellationToken);

    internal static async Task ServeAsync(Func<ILocalSetupConnection> create, Func<Stream, CancellationToken, Task> exchange, CancellationToken cancellationToken)
    {
        ILocalSetupConnection? pipe = null;
        try
        {
            pipe = create();
            while (!cancellationToken.IsCancellationRequested)
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                var recreate = false;
                try
                {
                    // The exchange must finish serializing, writing and flushing its one receipt first.
                    await exchange(pipe.Stream, deadline.Token);
                    var acknowledgement = new byte[1];
                    await pipe.Stream.ReadExactlyAsync(acknowledgement, deadline.Token);
                    if (acknowledgement[0] != 6) throw new LocalSetupException();
                }
                catch (IOException exception) when (IsClientDisconnect(exception)) { }
                catch (LocalSetupException) { }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { }
                finally
                {
                    // Broken is not Connected, but still needs resetting before another accept.
                    try { pipe.Disconnect(); }
                    catch (IOException exception) when (IsClientDisconnect(exception)) { recreate = true; }
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (recreate)
                {
                    // The production factory rechecks FIRST_PIPE_INSTANCE and the same fixed ACL.
                    // If an old client still owns that name, creation fails closed, never rebinds it.
                    pipe.Dispose();
                    pipe = null;
                    pipe = create();
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        { throw new LocalSetupException(); }
        finally { pipe?.Dispose(); }
    }

    /// <summary>Only acknowledges a fully read and validated receipt; it grants no authority or retry permission.</summary>
    public static async Task AcknowledgeReceiptAsync(Stream stream, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(new byte[] { 6 }, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static bool IsClientDisconnect(IOException exception) =>
        exception is EndOfStreamException || exception.HResult is unchecked((int)0x8007006D) or unchecked((int)0x800700E8) or unchecked((int)0x800700E9) ||
        exception.InnerException is SocketException { SocketErrorCode: SocketError.Shutdown or SocketError.ConnectionReset or SocketError.ConnectionAborted };

    private sealed class PipeConnection(NamedPipeServerStream pipe) : ILocalSetupConnection
    {
        public Stream Stream => pipe;
        public Task WaitForConnectionAsync(CancellationToken token) => pipe.WaitForConnectionAsync(token);
        public void Disconnect() => pipe.Disconnect();
        public void Dispose() => pipe.Dispose();
    }
}

internal interface ILocalSetupConnection : IDisposable
{
    Stream Stream { get; }
    Task WaitForConnectionAsync(CancellationToken token);
    void Disconnect();
}
