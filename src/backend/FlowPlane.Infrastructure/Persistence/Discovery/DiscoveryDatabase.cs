using FlowPlane.Application.Providers.DiscoveryInventory;
using FlowPlane.Domain.Providers;

using Microsoft.EntityFrameworkCore;

namespace FlowPlane.Infrastructure.Persistence.Discovery;

internal static class DiscoveryDatabase
{
    public static Task<DateTimeOffset> NowAsync(FlowPlaneDbContext db, CancellationToken token) =>
        db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(token);

    public static async Task SetTimeoutsAsync(FlowPlaneDbContext db, int seconds, CancellationToken token)
    {
        var milliseconds = checked(seconds * 1000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('statement_timeout', {milliseconds}, true), set_config('transaction_timeout', {milliseconds}, true), set_config('lock_timeout', '5000', true)", token);
    }

    public static async Task<ProviderInstance> LockProviderAsync(FlowPlaneDbContext db, Guid id, CancellationToken token)
    {
        var provider = await db.ProviderInstances.FromSqlInterpolated(
            $"SELECT * FROM \"ProviderInstances\" WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(token);
        if (provider is null) throw new DiscoveryInventoryException(DiscoveryInventoryError.ProviderUnavailable);
        await db.Entry(provider).ReloadAsync(token);
        return provider;
    }

    public static async Task<long?> LockSecretAsync(FlowPlaneDbContext db, ProviderInstance instance, CancellationToken token)
    {
        if (!instance.SecretReference.TryGetFlowPlaneId(out var secretId)) return null;
        var secret = await db.SecretRecords.FromSqlInterpolated(
            $"SELECT * FROM \"FlowPlaneSecrets\" WHERE \"Id\" = {secretId} FOR UPDATE").SingleOrDefaultAsync(token);
        if (secret is null) throw new DiscoveryInventoryException(DiscoveryInventoryError.ContextChanged);
        await db.Entry(secret).ReloadAsync(token);
        return secret.Version;
    }

    public static Task<DiscoveryState?> LockStateAsync(FlowPlaneDbContext db, Guid id, CancellationToken token) =>
        db.DiscoveryStates.FromSqlInterpolated(
            $"SELECT * FROM \"ProviderDiscoveryStates\" WHERE \"ProviderInstanceId\" = {id} FOR UPDATE").SingleOrDefaultAsync(token);

    public static async Task EnsureStateAsync(FlowPlaneDbContext db, Guid id, CancellationToken token) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ProviderDiscoveryStates" ("ProviderInstanceId", "PublicationVersion", "FenceToken", "Lifecycle")
            VALUES ({id}, 0, 0, 'Active') ON CONFLICT ("ProviderInstanceId") DO NOTHING
            """, token);

    public static async Task<(DiscoveryState State, DiscoveryGeneration Generation, DateTimeOffset Now)> LockClaimAsync(
        FlowPlaneDbContext db, Guid providerId, Guid refreshId, long fence, DiscoveryGenerationStatus phase, CancellationToken token)
    {
        var state = await LockStateAsync(db, providerId, token);
        if (state is null || state.Lifecycle != DiscoveryStateLifecycle.Active || state.InFlightGenerationId != refreshId || state.FenceToken != fence)
            throw new DiscoveryInventoryException(DiscoveryInventoryError.Superseded);
        var generation = await db.DiscoveryGenerations.SingleAsync(row => row.RefreshId == refreshId, token);
        var now = await NowAsync(db, token);
        if (generation.ExpiresAtUtc <= now) throw new DiscoveryInventoryException(DiscoveryInventoryError.Expired);
        if (generation.Status != phase) throw new DiscoveryInventoryException(DiscoveryInventoryError.Superseded);
        return (state, generation, now);
    }
}
