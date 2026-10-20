using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace FlowPlane.Providers.Ssis.Discovery;

/// <summary>Disposable sequential spool; memory is independent of the total project count.</summary>
internal sealed class SsisProjectManifest : IAsyncDisposable
{
    private readonly Stream stream;

    public SsisProjectManifest() : this(SsisManifestFile.Open()) { }

    internal SsisProjectManifest(Stream ownedStream)
    {
        ArgumentNullException.ThrowIfNull(ownedStream);
        try
        {
            if (!ownedStream.CanRead || !ownedStream.CanWrite || !ownedStream.CanSeek)
                throw new ArgumentException("The owned manifest stream must be readable, writable and seekable.", nameof(ownedStream));
            stream = ownedStream;
        }
        catch
        {
            try { ownedStream.Dispose(); }
            catch { /* Preserve the constructor failure; cleanup is best effort. */ }
            throw;
        }
    }

    public async Task AppendAsync(SsisProjectManifestEntry entry, CancellationToken token) =>
        await stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry) + "\n"), token);

    public async IAsyncEnumerable<IReadOnlyList<SsisProjectManifestEntry>> ReadBatchesAsync(
        int batchSize, [EnumeratorCancellation] CancellationToken token)
    {
        await stream.FlushAsync(token);
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 16_384, leaveOpen: true);
        var batch = new List<SsisProjectManifestEntry>(batchSize);
        while (await reader.ReadLineAsync(token) is { } line)
        {
            var entry = JsonSerializer.Deserialize<SsisProjectManifestEntry>(line)
                ?? throw new SsisDiscoverySourceChangedException();
            if (entry.ProjectId is null)
                continue;
            batch.Add(entry);
            if (batch.Count == batchSize)
            {
                yield return batch;
                batch = new List<SsisProjectManifestEntry>(batchSize);
            }
        }
        if (batch.Count != 0)
            yield return batch;
    }

    // No buffered writer: abandoning a scan never retries a write or flushes provisional data.
    public ValueTask DisposeAsync() => stream.DisposeAsync();
}
