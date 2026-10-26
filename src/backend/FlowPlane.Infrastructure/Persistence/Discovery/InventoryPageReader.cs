using System.Data;
using System.Security.Cryptography;
using System.Text;

using FlowPlane.Application.Providers.DiscoveryInventory;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using Npgsql;
using NpgsqlTypes;

namespace FlowPlane.Infrastructure.Persistence.Discovery;

internal sealed partial class ProviderDiscoveryInventoryStore : IProviderInventoryPageStore
{
    public async Task<IProviderInventoryPageReader> OpenPageReaderAsync(Guid providerId, long providerVersion,
        Guid? expectedGenerationId, CancellationToken token)
    {
        var db = CreateContext();
        IDbContextTransaction? transaction = null;
        try
        {
            transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
            await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", token);
            await DiscoveryDatabase.SetTimeoutsAsync(db, options.ReaderTimeoutSeconds, token);
            var state = await db.DiscoveryStates.AsNoTracking().SingleOrDefaultAsync(row => row.ProviderInstanceId == providerId, token);
            if (expectedGenerationId is not null && state?.CurrentGenerationId != expectedGenerationId)
                throw new InventoryBrowseException(InventoryBrowseError.GenerationChanged);
            if (state?.CurrentGenerationId is not { } generationId)
                throw new InventoryBrowseException(InventoryBrowseError.NotPublished);
            if (state.Lifecycle != DiscoveryStateLifecycle.Active || state.LastAttemptGenerationId != generationId || state.InFlightGenerationId is not null)
                throw new InventoryBrowseException(InventoryBrowseError.ContextChanged);
            var generation = await db.DiscoveryGenerations.AsNoTracking().SingleAsync(row => row.RefreshId == generationId && row.ProviderInstanceId == providerId, token);
            var provider = await db.ProviderInstances.AsNoTracking().SingleAsync(row => row.Id == providerId, token);
            long? secretVersion = null;
            if (provider.SecretReference.TryGetFlowPlaneId(out var secretId))
                secretVersion = await db.SecretRecords.Where(row => row.Id == secretId).Select(row => (long?)row.Version).SingleOrDefaultAsync(token);
            if (!provider.IsEnabled || provider.Version != providerVersion || generation.ProviderVersion != provider.Version ||
                secretVersion != generation.ManagedSecretVersion || generation.Status != DiscoveryGenerationStatus.Published || !generation.ScanCompleted)
                throw new InventoryBrowseException(InventoryBrowseError.ContextChanged);
            return new InventoryPageReader(db, transaction, generation, state.PublicationVersion);
        }
        catch (Exception error)
        {
            try { if (transaction is not null) await transaction.DisposeAsync(); }
            finally { await db.DisposeAsync(); }
            if (error is OperationCanceledException or InventoryBrowseException) throw;
            throw new InventoryBrowseException(InventoryBrowseError.StorageUnavailable);
        }
    }

    public async Task<bool> IsCurrentAsync(InventoryReadContext context, CancellationToken token)
    {
        try
        {
            await using var reader = await OpenPageReaderAsync(context.ProviderInstanceId, context.ProviderVersion, context.GenerationId, token);
            return reader.Context == context;
        }
        catch (InventoryBrowseException error) when (error.Code != InventoryBrowseError.StorageUnavailable) { return false; }
    }
}

internal sealed class InventoryPageReader(FlowPlaneDbContext db, IDbContextTransaction transaction,
    DiscoveryGeneration generation, long publicationVersion) : IProviderInventoryPageReader
{
    private bool disposed;
    public InventoryReadContext Context { get; } = new(generation.ProviderInstanceId, generation.RefreshId,
        generation.ProviderVersion, publicationVersion, generation.ManagedSecretVersion, generation.AccessContextId);

    public async Task<InventoryStoredPage> ReadPageAsync(InventoryPageQuery query, IReadOnlyList<string> allowedSchemas,
        string? afterDigest, CancellationToken token)
    {
        query.Validate(allowedSchemas);
        byte[] after = [];
        if (afterDigest is not null)
        {
            if (afterDigest.Length != 64 || afterDigest.Any(c => !Uri.IsHexDigit(c)))
                throw new InventoryBrowseException(InventoryBrowseError.InvalidCursor);
            after = Convert.FromHexString(afterDigest);
        }
        var schemas = query.SchemaId is null ? allowedSchemas.ToArray() : [query.SchemaId];
        var rows = await ReadAsync("""
            SELECT * FROM "ProviderDiscoveryObservations"
            WHERE "ProviderInstanceId" = @provider AND "RefreshId" = @generation
              AND "Visibility" = 'Observed' AND "SchemaId" = ANY(@schemas)
              AND (NOT @candidates OR "CandidateSchemaId" IS NOT NULL)
              AND "NativeKeyDigest" > @after
            ORDER BY "NativeKeyDigest" LIMIT @take
            """, token,
            new NpgsqlParameter("schemas", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = schemas },
            new NpgsqlParameter("candidates", query.CandidatesOnly),
            new NpgsqlParameter("after", NpgsqlDbType.Bytea) { Value = after },
            new NpgsqlParameter("take", checked(query.Limit + 1)));
        var more = rows.Count > query.Limit;
        var visible = rows.Take(query.Limit).ToArray();
        return new(visible.Select(Decode).ToArray(), more ? Convert.ToHexString(visible[^1].NativeKeyDigest) : null,
            more ? Decode(rows[^1]) : null);
    }

    public async Task<IReadOnlyList<StoredDiscoveryObservation>> ReadScopeAsync(IReadOnlyList<string> nativeKeys, CancellationToken token)
    {
        if (nativeKeys.Count > 303) throw new InventoryBrowseException(InventoryBrowseError.InvalidRequest);
        if (nativeKeys.Count == 0) return [];
        var keys = nativeKeys.Distinct(StringComparer.Ordinal).ToArray();
        var digests = keys.Select(key => SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToArray();
        var rows = await ReadAsync("""
            SELECT * FROM "ProviderDiscoveryObservations"
            WHERE "ProviderInstanceId" = @provider AND "RefreshId" = @generation
              AND "Visibility" = 'Observed' AND "NativeKeyDigest" = ANY(@digests)
            LIMIT @take
            """, token,
            new NpgsqlParameter("digests", NpgsqlDbType.Array | NpgsqlDbType.Bytea) { Value = digests },
            new NpgsqlParameter("take", keys.Length));
        if (rows.Count != keys.Length || rows.Any(row => !keys.Contains(row.NativeKey, StringComparer.Ordinal)))
            throw new InventoryBrowseException(InventoryBrowseError.NativeChanged);
        return rows.Select(Decode).ToArray();
    }

    private StoredDiscoveryObservation Decode(DiscoveryObservationRecord row)
    {
        if (row.ObservedAccessContextId != Context.AccessContextId || row.ObservedProviderVersion != Context.ProviderVersion ||
            row.ObservedSecretVersion != Context.ManagedSecretVersion ||
            !SHA256.HashData(Encoding.UTF8.GetBytes(row.NativeKey)).AsSpan().SequenceEqual(row.NativeKeyDigest))
            throw new InventoryBrowseException(InventoryBrowseError.ContextChanged);
        return DiscoveryObservationPersistence.Decode(row, generation.ProviderType);
    }

    private async Task<List<DiscoveryObservationRecord>> ReadAsync(string sql, CancellationToken token, params NpgsqlParameter[] parameters)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        try
        {
            var all = new NpgsqlParameter[] { new("provider", Context.ProviderInstanceId), new("generation", Context.GenerationId) }.Concat(parameters).Cast<object>().ToArray();
            return await db.DiscoveryObservations.FromSqlRaw(sql, all).AsNoTracking().ToListAsync(token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw new InventoryBrowseException(InventoryBrowseError.StorageUnavailable); }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try { await transaction.DisposeAsync(); }
        finally { await db.DisposeAsync(); }
    }
}
