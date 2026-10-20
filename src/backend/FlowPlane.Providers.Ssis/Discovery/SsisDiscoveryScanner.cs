using System.Text.Json;

using FlowPlane.Application.Providers;

namespace FlowPlane.Providers.Ssis.Discovery;

internal sealed record SsisDiscoveryScanCounts(
    long ObservationsDelivered, long ExecutableCandidates, long IncompatibleDefinitions);

internal static class SsisDiscoveryScanner
{
    public static async Task<SsisDiscoveryScanCounts> ScanAsync(
        Guid instanceId, ISsisDiscoverySession session, IProviderDiscoverySink sink,
        SsisDiscoveryOptions options, CancellationToken token)
    {
        options.Validate();
        long observations = 0, candidates = 0, incompatible = 0;
        async ValueTask DeliverAsync(ProviderDiscoveryObservation observation)
        {
            token.ThrowIfCancellationRequested();
            await sink.AppendAsync(observation, token);
            observations = checked(observations + 1);
            if (observation.ExecutableCandidate is not null)
                candidates = checked(candidates + 1);
            if (SsisDiscoveryMapper.IsIncompatible(observation))
                incompatible = checked(incompatible + 1);
        }

        await using var manifest = new SsisProjectManifest();
        using var initial = new SsisDiscoveryFingerprint();
        long? lastFolder = null;
        await foreach (var entry in session.ReadManifestAsync(token).WithCancellation(token))
        {
            initial.Append(JsonSerializer.Serialize(entry));
            await manifest.AppendAsync(entry, token);
            if (entry.FolderId != lastFolder)
            {
                await DeliverAsync(SsisDiscoveryMapper.MapFolder(instanceId, entry));
                lastFolder = entry.FolderId;
            }
            if (entry.ProjectId is not null)
                await DeliverAsync(SsisDiscoveryMapper.MapProject(instanceId, entry));
        }
        var expectedManifest = initial.Finish();

        await foreach (var batch in manifest.ReadBatchesAsync(options.MaxProjectsPerBatch, token))
        {
            using var read = new SsisDiscoveryFingerprint();
            await foreach (var row in session.ReadProjectBatchAsync(batch, token).WithCancellation(token))
            {
                var observation = SsisDiscoveryMapper.Map(instanceId, row);
                Append(read, observation);
                await DeliverAsync(observation);
            }
            var expected = read.Finish();
            using var verification = new SsisDiscoveryFingerprint();
            await foreach (var row in session.ReadProjectBatchAsync(batch, token).WithCancellation(token))
                Append(verification, SsisDiscoveryMapper.Map(instanceId, row));
            if (!string.Equals(expected, verification.Finish(), StringComparison.Ordinal))
                throw new SsisDiscoverySourceChangedException();
        }

        using var final = new SsisDiscoveryFingerprint();
        await foreach (var entry in session.ReadManifestAsync(token).WithCancellation(token))
            final.Append(JsonSerializer.Serialize(entry));
        if (!string.Equals(expectedManifest, final.Finish(), StringComparison.Ordinal))
            throw new SsisDiscoverySourceChangedException();
        token.ThrowIfCancellationRequested();
        return new(observations, candidates, incompatible);
    }

    private static void Append(SsisDiscoveryFingerprint hash, ProviderDiscoveryObservation observation)
    {
        hash.Append(observation.NativeKey);
        hash.Append(observation.Metadata.SchemaId);
        hash.Append(observation.Metadata.CanonicalJson);
    }
}
