using FlowPlane.Application.Providers.DiscoveryInventory;

using Microsoft.EntityFrameworkCore;

namespace FlowPlane.Infrastructure.Persistence.Discovery;

internal sealed partial class ProviderDiscoveryInventoryStore
{
    public async Task CleanupAsync(Guid providerInstanceId, CancellationToken cancellationToken)
    {
        try
        {
            // Bound each invocation as well as every transaction; another invocation continues the work.
            for (var batch = 0; batch < 10; batch++)
                if (!await CleanupBatchAsync(providerInstanceId, cancellationToken)) break;
        }
        catch (OperationCanceledException) { throw; }
        catch (DiscoveryInventoryException) { throw; }
        catch (Exception) { throw new DiscoveryInventoryException(DiscoveryInventoryError.StorageUnavailable); }
    }

    private async Task<bool> CleanupBatchAsync(Guid providerInstanceId, CancellationToken token)
    {
        await using var db = CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await DiscoveryDatabase.SetTimeoutsAsync(db, options.PublicationTimeoutSeconds, token);
        var state = await DiscoveryDatabase.LockStateAsync(db, providerInstanceId, token);
        if (state is null) return false;
        var now = await DiscoveryDatabase.NowAsync(db, token);
        if (state.InFlightGenerationId is { } claimed)
        {
            var generation = await db.DiscoveryGenerations.SingleAsync(row => row.RefreshId == claimed, token);
            if (generation.ExpiresAtUtc <= now)
            {
                generation.Status = DiscoveryGenerationStatus.Abandoned;
                generation.FinishedAtUtc = now;
                generation.Error = DiscoveryInventoryError.Expired;
                generation.Version = checked(generation.Version + 1);
                state.InFlightGenerationId = null;
                state.FenceToken = checked(state.FenceToken + 1);
                await db.SaveChangesAsync(token);
            }
        }
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM "ProviderDiscoveryObservations" target
            USING (SELECT "ProviderInstanceId", "RefreshId", "NativeKeyDigest"
                   FROM "ProviderDiscoveryObservations"
                   WHERE "ProviderInstanceId" = {providerInstanceId}
                     AND (CAST({state.CurrentGenerationId} AS uuid) IS NULL OR "RefreshId" <> {state.CurrentGenerationId})
                     AND (CAST({state.InFlightGenerationId} AS uuid) IS NULL OR "RefreshId" <> {state.InFlightGenerationId})
                   ORDER BY "RefreshId", "NativeKeyDigest" LIMIT {options.CleanupBatchSize}) batch
            WHERE target."ProviderInstanceId" = batch."ProviderInstanceId" AND target."RefreshId" = batch."RefreshId"
              AND target."NativeKeyDigest" = batch."NativeKeyDigest"
            """, token);
        // Retain minimal terminal receipts for reconciliation, not historical native payloads.
        var cutoff = now.AddDays(-7);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM "ProviderDiscoveryGenerations" target USING (
                SELECT g."RefreshId" FROM "ProviderDiscoveryGenerations" g
                WHERE g."ProviderInstanceId" = {providerInstanceId} AND g."FinishedAtUtc" < {cutoff}
                  AND g."Status" IN ('Published', 'Failed', 'Cancelled', 'Superseded', 'Abandoned')
                  AND (CAST({state.CurrentGenerationId} AS uuid) IS NULL OR g."RefreshId" <> {state.CurrentGenerationId})
                  AND (CAST({state.InFlightGenerationId} AS uuid) IS NULL OR g."RefreshId" <> {state.InFlightGenerationId})
                  AND (CAST({state.LastAttemptGenerationId} AS uuid) IS NULL OR g."RefreshId" <> {state.LastAttemptGenerationId})
                  AND NOT EXISTS (SELECT 1 FROM "ProviderDiscoveryObservations" o WHERE o."RefreshId" = g."RefreshId")
                ORDER BY g."RefreshId" LIMIT {options.CleanupBatchSize}
            ) batch WHERE target."RefreshId" = batch."RefreshId"
            """, token);
        await transaction.CommitAsync(token);
        return changed > 0;
    }

    internal static async Task RetireAsync(FlowPlaneDbContext db, Guid providerInstanceId, CancellationToken token)
    {
        await DiscoveryDatabase.EnsureStateAsync(db, providerInstanceId, token);
        var state = (await DiscoveryDatabase.LockStateAsync(db, providerInstanceId, token))!;
        if (state.InFlightGenerationId is { } claimed)
        {
            var generation = await db.DiscoveryGenerations.SingleAsync(row => row.RefreshId == claimed, token);
            generation.Status = DiscoveryGenerationStatus.Superseded;
            generation.Error = DiscoveryInventoryError.ProviderUnavailable;
            generation.FinishedAtUtc = await DiscoveryDatabase.NowAsync(db, token);
            generation.Version = checked(generation.Version + 1);
        }
        state.Lifecycle = DiscoveryStateLifecycle.Deleting;
        state.FenceToken = checked(state.FenceToken + 1);
        state.CurrentGenerationId = null;
        state.InFlightGenerationId = null;
        state.LastAttemptGenerationId = null;
        await db.SaveChangesAsync(token);
    }

    internal static async Task PurgeRetiredAsync(FlowPlaneDbContext db, Guid providerInstanceId, CancellationToken token)
    {
        while (true)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            await DiscoveryDatabase.SetTimeoutsAsync(db, 30, token);
            var state = await DiscoveryDatabase.LockStateAsync(db, providerInstanceId, token);
            if (state?.Lifecycle != DiscoveryStateLifecycle.Deleting)
                throw new DiscoveryInventoryException(DiscoveryInventoryError.ContextChanged);
            var removed = await db.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM "ProviderDiscoveryObservations" target USING (
                    SELECT "RefreshId", "NativeKeyDigest" FROM "ProviderDiscoveryObservations"
                    WHERE "ProviderInstanceId" = {providerInstanceId} ORDER BY "RefreshId", "NativeKeyDigest" LIMIT 1000
                ) batch WHERE target."ProviderInstanceId" = {providerInstanceId}
                  AND target."RefreshId" = batch."RefreshId" AND target."NativeKeyDigest" = batch."NativeKeyDigest"
                """, token);
            if (removed == 0)
            {
                removed = await db.Database.ExecuteSqlInterpolatedAsync($"""
                    DELETE FROM "ProviderDiscoveryGenerations" WHERE "RefreshId" IN (
                        SELECT "RefreshId" FROM "ProviderDiscoveryGenerations" WHERE "ProviderInstanceId" = {providerInstanceId}
                        ORDER BY "RefreshId" LIMIT 1000)
                    """, token);
            }
            await transaction.CommitAsync(token);
            if (removed == 0) break;
        }
    }
}
