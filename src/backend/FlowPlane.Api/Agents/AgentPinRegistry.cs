using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;

using FlowPlane.Agent.Protocol;
using FlowPlane.Application.Agents;

namespace FlowPlane.Api.Agents;

internal sealed class AgentPinRegistry
{
    private Dictionary<string, byte[]> snapshot = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim refresh = new(1, 1);
    private readonly object publication = new();
    private long requested;
    internal long Generation => Volatile.Read(ref requested);
    private int needsRefresh = 1;
    internal bool NeedsRefresh => Volatile.Read(ref needsRefresh) != 0;
    internal async Task RefreshAsync(Func<CancellationToken, Task<IReadOnlyList<AgentPublicPin>>> read, CancellationToken token)
    {
        long request;
        lock (publication)
        {
            request = ++requested;
            Close(); // Cancellation while waiting must also invalidate any older in-flight read.
        }
        await refresh.WaitAsync(token);
        try
        {
            var next = await read(token);
            token.ThrowIfCancellationRequested();
            lock (publication)
                if (request == requested) Replace(next);
        }
        catch
        {
            lock (publication) Close();
            throw;
        }
        finally { refresh.Release(); }
    }

    private void Close()
    {
        Volatile.Write(ref snapshot, new(StringComparer.Ordinal));
        Volatile.Write(ref needsRefresh, 1);
    }

    public void Replace(IReadOnlyList<AgentPublicPin> pins)
    {
        // Publish a complete immutable snapshot, or deny everything. Never retain a partial refresh.
        var next = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            if (pins.Count > 4096) throw new InvalidOperationException("Agent pin capacity exceeded.");
            foreach (var pin in pins)
            {
                if (pin.AgentId == Guid.Empty || pin.CredentialId == Guid.Empty || pin.CertificateDer.Length is < 1 or > AgentProtocol.MaxCertificateDerBytes)
                    throw new InvalidOperationException("Invalid agent pin snapshot.");
                next.Add(Convert.ToHexString(SHA256.HashData(pin.CertificateDer)), pin.CertificateDer.ToArray());
            }
        }
        catch
        {
            Volatile.Write(ref snapshot, new(StringComparer.Ordinal));
            Volatile.Write(ref needsRefresh, 1);
            throw;
        }
        Volatile.Write(ref snapshot, next);
        Volatile.Write(ref needsRefresh, 0);
    }

    public bool Accepts(X509Certificate2 certificate, DateTimeOffset now)
    {
        var der = certificate.RawData;
        return Volatile.Read(ref snapshot).TryGetValue(Convert.ToHexString(SHA256.HashData(der)), out var expected) &&
            CryptographicOperations.FixedTimeEquals(expected, der) && AgentPairingSecurity.IsClientCertificate(der, now);
    }
}
