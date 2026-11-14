using System.Buffers.Binary;
using FlowPlane.Agent.Windows.Security;

namespace FlowPlane.Agent.Windows.Identity;

internal sealed record AgentIdentityPublicRecord(Guid InstallationId, Guid KeyId, bool Platform, byte[] Der)
{
    public static AgentIdentityPublicRecord Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 26 or > 16425) throw new AgentWindowsException();
        var version = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        var offset = version switch { 1 => 21, 2 => 37, _ => throw new AgentWindowsException() };
        if (bytes.Length < offset + 5 || bytes[20] > 1) throw new AgentWindowsException();
        var installation = new Guid(bytes.Slice(4, 16));
        var key = version == 1 ? installation : new Guid(bytes.Slice(21, 16));
        var length = BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
        if (installation == Guid.Empty || key == Guid.Empty || length is < 1 or > 16384 || bytes.Length != offset + 4 + length) throw new AgentWindowsException();
        return new(installation, key, bytes[20] == 1, bytes[(offset + 4)..].ToArray());
    }

    public byte[] Encode()
    {
        if (InstallationId == Guid.Empty || KeyId == Guid.Empty || Der.Length is < 1 or > 16384) throw new AgentWindowsException();
        var bytes = new byte[41 + Der.Length];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 2);
        InstallationId.TryWriteBytes(bytes.AsSpan(4)); bytes[20] = Platform ? (byte)1 : (byte)0;
        KeyId.TryWriteBytes(bytes.AsSpan(21));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(37), Der.Length);
        Der.CopyTo(bytes, 41);
        return bytes;
    }
}
