using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

using Microsoft.Win32.SafeHandles;

using FlowPlane.Agent.Runtime.Setup;
using FlowPlane.Agent.Windows.Security;

namespace FlowPlane.Agent.Windows.Setup;

/// <summary>Local admin setup only. There is no command/SQL/shell or remote work endpoint.</summary>
public sealed class LocalSetupPipe : ILocalSetupChannel
{
    private const string PipeName = "FlowPlaneAgent.Setup.v1";
    private const string Schema = "flowplane.agent/local-setup-receipt.v1";

    public Task ServeAsync(Func<LocalSetupRequest, CancellationToken, ValueTask<LocalSetupReceipt>> handle, CancellationToken cancellationToken) =>
        LocalSetupConnectionLoop.ServeAsync(CreateServer, async (pipe, token) =>
    {
        byte[]? body = null;
        try
        {
            body = await ReadFrame(pipe, token);
            var authorized = false;
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent(true);
                authorized = identity is not null && (identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));
            });
            var receipt = authorized ? await handle(LocalSetupCodec.Decode(body), token) : new(Schema, LocalSetupStatus.Denied);
            await WriteFrame(pipe, JsonSerializer.SerializeToUtf8Bytes(receipt), token);
        }
        finally { if (body is not null) CryptographicOperations.ZeroMemory(body); }
    }, cancellationToken);

    public ValueTask<LocalSetupReceipt> PairAsync(LocalSetupRequest request, CancellationToken cancellationToken)
    {
        if (request.Action != LocalSetupAction.Pair) throw new LocalSetupException();
        return SendAsync(request, cancellationToken);
    }

    public async ValueTask<LocalSetupReceipt> SendAsync(LocalSetupRequest request, CancellationToken cancellationToken)
    {
        WindowsAgentSecurity.RequireAdministrator();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var bytes = LocalSetupCodec.Encode(request);
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
            await pipe.ConnectAsync(deadline.Token);
            VerifyServer(pipe);
            await WriteFrame(pipe, bytes, deadline.Token);
            var reply = await ReadFrame(pipe, deadline.Token);
            using var document = JsonDocument.Parse(reply);
            var root = document.RootElement;
            if (root.GetProperty("Schema").GetString() != Schema || !root.GetProperty("Status").TryGetInt32(out var status) ||
                !Enum.IsDefined((LocalSetupStatus)status)) throw new LocalSetupException();
            await LocalSetupConnectionLoop.AcknowledgeReceiptAsync(pipe, deadline.Token);
            return new(Schema, (LocalSetupStatus)status);
        }
        catch (Exception exception) when (exception is IOException or JsonException or KeyNotFoundException or UnauthorizedAccessException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new LocalSetupException();
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static NamedPipeServerStream CreateServer()
    {
        // FILE_FLAG_FIRST_PIPE_INSTANCE, OVERLAPPED and PIPE_REJECT_REMOTE_CLIENTS are native guarantees.
        var descriptor = new RawSecurityDescriptor("D:P(A;;GA;;;SY)(A;;GA;;;BA)");
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = pinned.AddrOfPinnedObject() };
            var handle = NativePipe.CreateNamedPipeW(@"\\.\pipe\" + PipeName, 0x40080003, 0x8, 1, 4096, 4096, 10000, ref attributes);
            if (handle.IsInvalid) { handle.Dispose(); throw new AgentWindowsException(); }
            return new(PipeDirection.InOut, true, false, handle);
        }
        finally { pinned.Free(); }
    }

    private static void VerifyServer(NamedPipeClientStream pipe)
    {
        if (!NativePipe.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var id)) throw new LocalSetupException();
        using var process = NativePipe.OpenProcess(0x1000, false, id);
        if (process.IsInvalid || !NativePipe.OpenProcessToken(process, 8, out var token)) throw new LocalSetupException();
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            if (identity.User != WindowsAgentSecurity.ServiceSid) throw new LocalSetupException();
    }

    private static async Task<byte[]> ReadFrame(Stream stream, CancellationToken cancellationToken)
    {
        var length = new byte[4];
        await stream.ReadExactlyAsync(length, cancellationToken);
        var count = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (count is < 1 or > 4096) throw new LocalSetupException();
        var bytes = new byte[count];
        try { await stream.ReadExactlyAsync(bytes, cancellationToken); return bytes; }
        catch { CryptographicOperations.ZeroMemory(bytes); throw; }
    }

    private static async Task WriteFrame(Stream stream, byte[] bytes, CancellationToken cancellationToken)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        await stream.WriteAsync(length, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    private static class NativePipe
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint instances, uint output, uint input, uint timeout, ref SecurityAttributes attributes);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint id);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint id);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    }
}
