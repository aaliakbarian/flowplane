using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FlowPlane.Providers.Ssis.Discovery;

internal sealed class SsisDiscoveryFingerprint : IDisposable
{
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public void Append(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    public string Finish() => Convert.ToHexString(hash.GetHashAndReset());

    public void Dispose() => hash.Dispose();
}
