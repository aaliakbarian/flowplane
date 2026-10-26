using FlowPlane.Application.Providers;
using FlowPlane.Application.Providers.DiscoveryInventory;
using FlowPlane.Domain.Providers;

using Microsoft.EntityFrameworkCore;

namespace FlowPlane.Infrastructure.Persistence.Discovery;

internal sealed class DiscoveryRefreshSession(
    ProviderDiscoveryInventoryStore store, ProviderInstance instance, DiscoveryGeneration generation)
    : IProviderDiscoveryRefreshSession, IProviderDiscoverySink, IProviderDiscoveryContextGuard
{
    private readonly List<DiscoveryObservationRecord> buffer = [];
    private int bufferedBytes;
    private DiscoveryInventoryError? failure;
    private bool sealedForPublication, finished, commitAttempted;
    public Guid RefreshId => generation.RefreshId;
    public ProviderInstance Instance => instance;
    public IProviderDiscoverySink Sink => this;
    public DiscoveryInventoryError? SinkFailure => failure;

    public async Task<bool> IsCurrentAsync(CancellationToken cancellationToken)
    {
        await using var db = store.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await DiscoveryDatabase.SetTimeoutsAsync(db, store.Options.ClaimTimeoutSeconds, cancellationToken);
        var current = await DiscoveryDatabase.LockProviderAsync(db, instance.Id, cancellationToken);
        var secretVersion = await DiscoveryDatabase.LockSecretAsync(db, current, cancellationToken);
        await DiscoveryDatabase.LockClaimAsync(db, instance.Id, RefreshId, generation.FenceToken,
            DiscoveryGenerationStatus.Staging, cancellationToken, generation.ExecutorId);
        return current.IsEnabled && current.Version == generation.ProviderVersion &&
            current.SecretReference == instance.SecretReference && secretVersion == generation.ManagedSecretVersion;
    }

    public async ValueTask AppendAsync(ProviderDiscoveryObservation observation, CancellationToken cancellationToken)
    {
        try
        {
            if (sealedForPublication || failure is not null) throw new DiscoveryInventoryException(DiscoveryInventoryError.InvalidObservation);
            cancellationToken.ThrowIfCancellationRequested();
            var row = DiscoveryObservationPersistence.Encode(observation, generation, DateTimeOffset.UtcNow);
            var size = DiscoveryObservationPersistence.Size(row);
            if (buffer.Count > 0 && (buffer.Count >= store.Options.WriteBatchSize || size > store.Options.WriteBufferBytes - bufferedBytes))
                await FlushAsync(cancellationToken);
            foreach (var existing in buffer)
                if (existing.NativeKeyDigest.AsSpan().SequenceEqual(row.NativeKeyDigest))
                    DiscoveryObservationPersistence.RejectExistingKey(row, existing.NativeKey);
            buffer.Add(row);
            bufferedBytes = checked(bufferedBytes + size);
            if (buffer.Count >= store.Options.WriteBatchSize || bufferedBytes >= store.Options.WriteBufferBytes)
                await FlushAsync(cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (DiscoveryInventoryException exception) { failure = exception.Code; throw; }
        catch (Exception)
        {
            failure = DiscoveryInventoryError.StorageUnavailable;
            throw new DiscoveryInventoryException(failure.Value);
        }
    }

    private async Task FlushAsync(CancellationToken token)
    {
        if (buffer.Count == 0) return;
        await using var db = store.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await DiscoveryDatabase.SetTimeoutsAsync(db, store.Options.PublicationTimeoutSeconds, token);
        var claim = await DiscoveryDatabase.LockClaimAsync(db, instance.Id, RefreshId, generation.FenceToken, DiscoveryGenerationStatus.Staging, token, generation.ExecutorId);
        foreach (var row in buffer)
        {
            var existing = await db.DiscoveryObservations.AsNoTracking().Where(item => item.ProviderInstanceId == instance.Id
                && item.RefreshId == RefreshId && item.NativeKeyDigest == row.NativeKeyDigest).Select(item => item.NativeKey).SingleOrDefaultAsync(token);
            if (existing is not null) DiscoveryObservationPersistence.RejectExistingKey(row, existing);
        }
        db.DiscoveryObservations.AddRange(buffer);
        claim.Generation.ObservationCount = checked(claim.Generation.ObservationCount + buffer.Count);
        claim.Generation.CandidateCount = checked(claim.Generation.CandidateCount + buffer.LongCount(row => row.CandidateSchemaId is not null));
        claim.Generation.Version = checked(claim.Generation.Version + 1);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        buffer.Clear();
        bufferedBytes = 0;
    }

    public async Task<ProviderDiscoveryRefreshResult> PublishAsync(ProviderDiscoveryScanResult summary, CancellationToken cancellationToken)
    {
        if (finished || sealedForPublication) throw new DiscoveryInventoryException(DiscoveryInventoryError.Superseded);
        sealedForPublication = true;
        if (failure is { } error) throw new DiscoveryInventoryException(error);
        using var prepare = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        prepare.CancelAfter(TimeSpan.FromSeconds(store.Options.PreparationTimeoutSeconds));
        try
        {
            await FlushAsync(prepare.Token);
            await PrepareAsync(summary, prepare.Token);
            using var publication = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            publication.CancelAfter(TimeSpan.FromSeconds(store.Options.PublicationTimeoutSeconds));
            await using var db = store.CreateContext();
            await using var transaction = await db.Database.BeginTransactionAsync(publication.Token);
            await DiscoveryDatabase.SetTimeoutsAsync(db, store.Options.PublicationTimeoutSeconds, publication.Token);
            var current = await DiscoveryDatabase.LockProviderAsync(db, instance.Id, publication.Token);
            var secretVersion = await DiscoveryDatabase.LockSecretAsync(db, current, publication.Token);
            if (!current.IsEnabled || current.Version != generation.ProviderVersion || secretVersion != generation.ManagedSecretVersion
                || current.SecretReference != instance.SecretReference)
                throw new DiscoveryInventoryException(DiscoveryInventoryError.ContextChanged);
            var claim = await DiscoveryDatabase.LockClaimAsync(db, instance.Id, RefreshId, generation.FenceToken, DiscoveryGenerationStatus.Ready, publication.Token, generation.ExecutorId);
            if (claim.State.PublicationVersion != generation.ExpectedPublicationVersion || claim.State.CurrentGenerationId != generation.BaseGenerationId)
                throw new DiscoveryInventoryException(DiscoveryInventoryError.Superseded);
            claim.Generation.Status = DiscoveryGenerationStatus.Published;
            claim.Generation.PublishedAtUtc = claim.Now;
            claim.Generation.FinishedAtUtc = claim.Now;
            claim.Generation.Version = checked(claim.Generation.Version + 1);
            claim.State.CurrentGenerationId = RefreshId;
            claim.State.PublicationVersion = checked(claim.State.PublicationVersion + 1);
            claim.State.InFlightGenerationId = null;
            await db.SaveChangesAsync(publication.Token);
            publication.Token.ThrowIfCancellationRequested();
            commitAttempted = true;
            try { await transaction.CommitAsync(publication.Token); }
            catch (Exception)
            {
                // Dispose the ambiguous connection before reconciling by durable ID on another one.
                await db.DisposeAsync();
                using var reconciliation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                finished = true;
                return await store.ReconcileAsync(RefreshId, reconciliation.Token);
            }
            finished = true;
            return new(RefreshId, DiscoveryPublicationStatus.Published, null);
        }
        catch (Exception) when (commitAttempted)
        {
            finished = true;
            using var reconciliation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { return await store.ReconcileAsync(RefreshId, reconciliation.Token); }
            catch (Exception) { return new(RefreshId, DiscoveryPublicationStatus.Uncertain, DiscoveryInventoryError.StorageUnavailable); }
        }
        catch (OperationCanceledException) { throw; }
        catch (DiscoveryInventoryException) { throw; }
        catch (Exception) { throw new DiscoveryInventoryException(DiscoveryInventoryError.StorageUnavailable); }
    }

    public async Task AbortAsync(DiscoveryInventoryError reason, CancellationToken cancellationToken)
    {
        if (finished || commitAttempted) return; // An uncertain commit must never be overwritten by abort.
        buffer.Clear();
        await store.AbortAsync(instance.Id, RefreshId, generation.FenceToken, reason, cancellationToken);
        finished = true;
    }

    public async ValueTask DisposeAsync()
    {
        buffer.Clear();
        if (finished || commitAttempted) return;
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await AbortAsync(DiscoveryInventoryError.Cancelled, cleanup.Token); }
        catch (Exception) { /* Expiry recovers a claim if storage is unavailable. */ }
    }

    private async Task PrepareAsync(ProviderDiscoveryScanResult summary, CancellationToken token)
    {
        await using (var db = store.CreateContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            await DiscoveryDatabase.SetTimeoutsAsync(db, store.Options.PublicationTimeoutSeconds, token);
            var claim = await DiscoveryDatabase.LockClaimAsync(db, instance.Id, RefreshId, generation.FenceToken, DiscoveryGenerationStatus.Staging, token, generation.ExecutorId);
            if (!DiscoveryPublicationPolicy.CanPublish(summary, claim.Generation.ObservationCount, claim.Generation.CandidateCount))
                throw new DiscoveryInventoryException(DiscoveryInventoryError.IncompleteScan);
            claim.Generation.Status = DiscoveryGenerationStatus.Preparing;
            claim.Generation.ScanCompleted = summary.ScanCompleted;
            claim.Generation.ScanOutcome = summary.Outcome;
            claim.Generation.SourceCompatibility = summary.SourceCompatibility;
            claim.Generation.IncompatibleCount = summary.IncompatibleDefinitions;
            claim.Generation.ScanStartedAtUtc = summary.StartedAtUtc;
            claim.Generation.ScanFinishedAtUtc = summary.FinishedAtUtc;
            claim.Generation.Version = checked(claim.Generation.Version + 1);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }
        var durable = await CompareAsync(token);
        if (generation.BaseGenerationId is { } baseId) await AddNotObservedAsync(baseId, token);
        await using var ready = store.CreateContext();
        await using var readyTransaction = await ready.Database.BeginTransactionAsync(token);
        await DiscoveryDatabase.SetTimeoutsAsync(ready, store.Options.PublicationTimeoutSeconds, token);
        var final = await DiscoveryDatabase.LockClaimAsync(ready, instance.Id, RefreshId, generation.FenceToken, DiscoveryGenerationStatus.Preparing, token, generation.ExecutorId);
        if (!DiscoveryPublicationPolicy.CanPublish(summary, durable.Observations, durable.Candidates)
            || checked(final.Generation.AddedCount + final.Generation.ChangedCount + final.Generation.UnchangedCount) != durable.Observations)
            throw new DiscoveryInventoryException(DiscoveryInventoryError.IncompleteScan);
        final.Generation.Status = DiscoveryGenerationStatus.Ready;
        if (generation.RequestingIssuer is not null) final.Generation.PublicationAttemptedAtUtc = final.Now;
        final.Generation.Version = checked(final.Generation.Version + 1);
        await ready.SaveChangesAsync(token);
        await readyTransaction.CommitAsync(token);
    }

    private static IQueryable<DiscoveryObservationRecord> Page(FlowPlaneDbContext db, Guid provider, Guid refresh, byte[] cursor, int size) =>
        db.DiscoveryObservations.FromSqlInterpolated($"""
            SELECT * FROM "ProviderDiscoveryObservations"
            WHERE "ProviderInstanceId" = {provider} AND "RefreshId" = {refresh}
              AND "Visibility" = 'Observed' AND "NativeKeyDigest" > {cursor}
            ORDER BY "NativeKeyDigest" LIMIT {size}
            """).AsNoTracking();

    private async Task<(long Observations, long Candidates)> CompareAsync(CancellationToken token)
    {
        byte[] cursor = [];
        long observations = 0, candidates = 0;
        while (true)
        {
            await using var db = store.CreateContext();
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            await DiscoveryDatabase.SetTimeoutsAsync(db, store.Options.PublicationTimeoutSeconds, token);
            var claim = await DiscoveryDatabase.LockClaimAsync(db, instance.Id, RefreshId, generation.FenceToken, DiscoveryGenerationStatus.Preparing, token, generation.ExecutorId);
            var rows = await Page(db, instance.Id, RefreshId, cursor, store.Options.WriteBatchSize)
                .Select(row => new { row.NativeKeyDigest, row.NativeKey, row.ContentFingerprint, row.CandidateSchemaId }).ToListAsync(token);
            if (rows.Count == 0) return (observations, candidates);
            foreach (var row in rows)
            {
                var old = await db.DiscoveryObservations.AsNoTracking().Where(item => item.ProviderInstanceId == instance.Id
                    && item.RefreshId == generation.BaseGenerationId && item.NativeKeyDigest == row.NativeKeyDigest)
                    .Select(item => new { item.NativeKey, item.ContentFingerprint, item.ObservedProviderVersion, item.ObservedSecretVersion }).SingleOrDefaultAsync(token);
                if (old is not null && !string.Equals(old.NativeKey, row.NativeKey, StringComparison.Ordinal))
                    throw new DiscoveryInventoryException(DiscoveryInventoryError.NativeKeyCollision);
                if (old?.ContentFingerprint is null || old.ObservedProviderVersion != generation.ProviderVersion || old.ObservedSecretVersion != generation.ManagedSecretVersion)
                    claim.Generation.AddedCount = checked(claim.Generation.AddedCount + 1);
                else if (old.ContentFingerprint.AsSpan().SequenceEqual(row.ContentFingerprint))
                    claim.Generation.UnchangedCount = checked(claim.Generation.UnchangedCount + 1);
                else claim.Generation.ChangedCount = checked(claim.Generation.ChangedCount + 1);
            }
            cursor = rows[^1].NativeKeyDigest;
            claim.Generation.Version = checked(claim.Generation.Version + 1);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            observations = checked(observations + rows.Count);
            candidates = checked(candidates + rows.LongCount(row => row.CandidateSchemaId is not null));
        }
    }

    private async Task AddNotObservedAsync(Guid baseId, CancellationToken token)
    {
        byte[] cursor = [];
        while (true)
        {
            await using var db = store.CreateContext();
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            await DiscoveryDatabase.SetTimeoutsAsync(db, store.Options.PublicationTimeoutSeconds, token);
            var claim = await DiscoveryDatabase.LockClaimAsync(db, instance.Id, RefreshId, generation.FenceToken, DiscoveryGenerationStatus.Preparing, token, generation.ExecutorId);
            // Project only identity/provenance: no previous metadata payload is read into memory.
            var rows = await Page(db, instance.Id, baseId, cursor, store.Options.WriteBatchSize).Select(row => new
            {
                row.NativeKeyDigest,
                row.NativeKey,
                row.SchemaId,
                row.LastObservedAtUtc,
                row.ObservedAccessContextId,
                row.ObservedProviderVersion,
                row.ObservedSecretVersion,
            }).ToListAsync(token);
            if (rows.Count == 0) return;
            foreach (var row in rows)
            {
                var existing = await db.DiscoveryObservations.AsNoTracking().Where(item => item.ProviderInstanceId == instance.Id
                    && item.RefreshId == RefreshId && item.NativeKeyDigest == row.NativeKeyDigest).Select(item => item.NativeKey).SingleOrDefaultAsync(token);
                if (existing is not null)
                {
                    if (!string.Equals(existing, row.NativeKey, StringComparison.Ordinal))
                        throw new DiscoveryInventoryException(DiscoveryInventoryError.NativeKeyCollision);
                    continue;
                }
                db.DiscoveryObservations.Add(new DiscoveryObservationRecord
                {
                    ProviderInstanceId = instance.Id,
                    RefreshId = RefreshId,
                    NativeKeyDigest = row.NativeKeyDigest,
                    NativeKey = row.NativeKey,
                    SchemaId = row.SchemaId,
                    Visibility = DiscoveryObservationVisibility.NotObserved,
                    LastObservedAtUtc = row.LastObservedAtUtc,
                    ObservedAccessContextId = row.ObservedAccessContextId,
                    ObservedProviderVersion = row.ObservedProviderVersion,
                    ObservedSecretVersion = row.ObservedSecretVersion,
                    NotObservedAtUtc = claim.Now,
                });
                claim.Generation.NotObservedCount = checked(claim.Generation.NotObservedCount + 1);
            }
            cursor = rows[^1].NativeKeyDigest;
            claim.Generation.Version = checked(claim.Generation.Version + 1);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }
    }
}
