using System.Data;
using System.Runtime.CompilerServices;

using FlowPlane.Application.Providers.DiscoveryInventory;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FlowPlane.Infrastructure.Persistence.Discovery;

internal sealed partial class ProviderDiscoveryInventoryStore
{
    public async Task<IProviderDiscoveryInventoryReader> OpenReaderAsync(Guid providerInstanceId, Guid verifiedRefreshId, CancellationToken cancellationToken)
    {
        var db = CreateContext();
        IDbContextTransaction? transaction = null;
        try
        {
            transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);
            await DiscoveryDatabase.SetTimeoutsAsync(db, options.ReaderTimeoutSeconds, cancellationToken);
            var state = await db.DiscoveryStates.AsNoTracking().SingleOrDefaultAsync(row => row.ProviderInstanceId == providerInstanceId, cancellationToken);
            if (state is null || state.Lifecycle != DiscoveryStateLifecycle.Active || state.CurrentGenerationId != verifiedRefreshId
                || state.LastAttemptGenerationId != verifiedRefreshId || state.InFlightGenerationId is not null)
                throw new DiscoveryInventoryException(DiscoveryInventoryError.ReaderContextUnavailable);
            var generation = await db.DiscoveryGenerations.AsNoTracking().SingleAsync(row => row.RefreshId == verifiedRefreshId, cancellationToken);
            var provider = await db.ProviderInstances.AsNoTracking().SingleAsync(row => row.Id == providerInstanceId, cancellationToken);
            long? secretVersion = null;
            if (provider.SecretReference.TryGetFlowPlaneId(out var secretId))
                secretVersion = await db.SecretRecords.Where(row => row.Id == secretId).Select(row => (long?)row.Version).SingleOrDefaultAsync(cancellationToken);
            if (!provider.IsEnabled || provider.Version != generation.ProviderVersion || secretVersion != generation.ManagedSecretVersion
                || generation.Status != DiscoveryGenerationStatus.Published)
                throw new DiscoveryInventoryException(DiscoveryInventoryError.ReaderContextUnavailable);
            return new DiscoveryInventoryReader(db, transaction, generation, options.ReaderTimeoutSeconds);
        }
        catch (Exception exception)
        {
            try { if (transaction is not null) await transaction.DisposeAsync(); }
            finally { await db.DisposeAsync(); }
            if (exception is OperationCanceledException or DiscoveryInventoryException) throw;
            throw new DiscoveryInventoryException(DiscoveryInventoryError.StorageUnavailable);
        }
    }
}

internal sealed class DiscoveryInventoryReader(
    FlowPlaneDbContext db, IDbContextTransaction transaction, DiscoveryGeneration generation, int timeoutSeconds)
    : IProviderDiscoveryInventoryReader
{
    private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(timeoutSeconds));
    private int reading;
    private bool disposed;
    public Guid GenerationId => generation.RefreshId;

    public async IAsyncEnumerable<StoredDiscoveryObservation> ReadAsync(string? schemaId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Interlocked.Exchange(ref reading, 1) != 0) throw new InvalidOperationException("Inventory reader queries must be sequential.");
        using var token = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        try
        {
            token.Token.ThrowIfCancellationRequested();
            var query = db.DiscoveryObservations.AsNoTracking().Where(row => row.ProviderInstanceId == generation.ProviderInstanceId && row.RefreshId == GenerationId);
            if (schemaId is not null) query = query.Where(row => row.SchemaId == schemaId);
            await using var iterator = query.OrderBy(row => row.NativeKeyDigest).AsAsyncEnumerable().GetAsyncEnumerator(token.Token);
            while (true)
            {
                bool next;
                try { next = await iterator.MoveNextAsync(); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { throw new DiscoveryInventoryException(DiscoveryInventoryError.StorageUnavailable); }
                if (!next) break;
                yield return DiscoveryObservationPersistence.Decode(iterator.Current, generation.ProviderType);
            }
        }
        finally { Volatile.Write(ref reading, 0); }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        try { await transaction.DisposeAsync(); }
        finally { lifetime.Dispose(); await db.DisposeAsync(); }
    }
}
