using FlowPlane.Application.Providers.DiscoveryHosting;
using FlowPlane.Application.Providers.DiscoveryInventory;

using Microsoft.EntityFrameworkCore;

namespace FlowPlane.Infrastructure.Persistence.Discovery;

internal enum HostedRecoveryAction { PreserveLive, ReleaseTerminal, AbandonExpired }

internal sealed class HostedDiscoveryStore(DbContextOptions<FlowPlaneDbContext> databaseOptions,
    DiscoveryInventoryOptions inventoryOptions, DiscoveryHostingOptions hostingOptions) : IHostedDiscoveryStore
{
    private FlowPlaneDbContext Context() => new(databaseOptions);
    private ProviderDiscoveryInventoryStore Inventory() => new(databaseOptions, inventoryOptions);

    // Global admission/recovery serialization, followed by the established inward lock order.
    private static Task SlotLockAsync(FlowPlaneDbContext db, CancellationToken token) =>
        db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7311541202)", token);

    public async Task<HostedDiscoveryReceipt> AdmitAsync(HostedDiscoveryAdmission request, CancellationToken token)
    {
        hostingOptions.Validate();
        inventoryOptions.Validate();
        if (!hostingOptions.Enabled) throw new HostedDiscoveryException(HostedDiscoveryError.Unavailable);
        if (request.ProviderVersion < 1 || string.IsNullOrWhiteSpace(request.Actor.Issuer) || request.Actor.Issuer.Length > 2048 ||
            string.IsNullOrWhiteSpace(request.Actor.Subject) || request.Actor.Subject.Length > 512 ||
            request.Actor.Issuer.Any(char.IsControl) || request.Actor.Subject.Any(char.IsControl))
            throw new HostedDiscoveryException(HostedDiscoveryError.KeyConflict);
        await using var db = Context();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await DiscoveryDatabase.SetTimeoutsAsync(db, inventoryOptions.ClaimTimeoutSeconds, token);
        await SlotLockAsync(db, token);
        var now = await DiscoveryDatabase.NowAsync(db, token);
        var existing = await db.DiscoveryGenerations.AsNoTracking().SingleOrDefaultAsync(row => row.RefreshId == request.Key, token);
        if (existing is not null)
        {
            if (existing.RequestingIssuer != request.Actor.Issuer || existing.RequestingSubject != request.Actor.Subject ||
                existing.ProviderInstanceId != request.ProviderId || existing.ProviderVersion != request.ProviderVersion ||
                existing.ReceiptExpiresAtUtc is null || existing.ReceiptExpiresAtUtc <= now)
                throw new HostedDiscoveryException(HostedDiscoveryError.KeyConflict);
            return Receipt(existing, now);
        }
        DiscoveryIdempotencyKey.ValidateFresh(request.Key, now);
        var occupied = await db.DiscoveryGenerations.AsNoTracking().SingleOrDefaultAsync(row => row.ExecutionSlot != null, token);
        if (occupied is not null)
        {
            if (occupied.LeaseExpiresAtUtc > now && occupied.ExpiresAtUtc > now)
                throw new HostedDiscoveryException(HostedDiscoveryError.SlotOccupied);
            if (!await RecoverAsync(db, occupied, token))
                throw new HostedDiscoveryException(HostedDiscoveryError.SlotOccupied);
        }
        var provider = await DiscoveryDatabase.LockProviderAsync(db, request.ProviderId, token);
        var secretVersion = await DiscoveryDatabase.LockSecretAsync(db, provider, token);
        if (!provider.IsEnabled) throw new HostedDiscoveryException(HostedDiscoveryError.Unavailable);
        if (provider.Version != request.ProviderVersion) throw new HostedDiscoveryException(HostedDiscoveryError.VersionChanged);
        await DiscoveryDatabase.EnsureStateAsync(db, provider.Id, token);
        var state = (await DiscoveryDatabase.LockStateAsync(db, provider.Id, token))!;
        if (state.Lifecycle != DiscoveryStateLifecycle.Active) throw new HostedDiscoveryException(HostedDiscoveryError.NotFound);
        if (state.InFlightGenerationId is not null) throw new HostedDiscoveryException(HostedDiscoveryError.SlotOccupied);
        state.FenceToken = checked(state.FenceToken + 1);
        var generation = new DiscoveryGeneration
        {
            RefreshId = request.Key,
            ProviderInstanceId = provider.Id,
            ProviderType = provider.ProviderType.Value,
            ProviderVersion = provider.Version,
            ManagedSecretVersion = secretVersion,
            CredentialKind = provider.SecretReference.Scheme.ToString(),
            AccessContextId = Guid.CreateVersion7(),
            BaseGenerationId = state.CurrentGenerationId,
            ExpectedPublicationVersion = state.PublicationVersion,
            FenceToken = state.FenceToken,
            StartedAtUtc = now,
            ExpiresAtUtc = now + inventoryOptions.RefreshTimeout,
            Status = DiscoveryGenerationStatus.Accepted,
            RequestingIssuer = request.Actor.Issuer,
            RequestingSubject = request.Actor.Subject,
            ExecutionSlot = 1,
            // Unstarted intent has no executor to renew it; preserve it through restart until
            // the operation deadline. Acquisition starts the shorter rolling executor lease.
            LeaseExpiresAtUtc = now + inventoryOptions.RefreshTimeout,
            ReceiptExpiresAtUtc = now.AddDays(7),
        };
        db.DiscoveryGenerations.Add(generation);
        await db.SaveChangesAsync(token);
        state.InFlightGenerationId = generation.RefreshId;
        state.LastAttemptGenerationId = generation.RefreshId;
        await db.SaveChangesAsync(token);
        // Lost acknowledgement is retried with the same key, never assumed to be a rollback.
        await transaction.CommitAsync(token);
        return Receipt(generation, now);
    }

    public async Task<HostedDiscoveryReceipt?> FindAsync(Guid providerId, Guid refreshId, CancellationToken token)
    {
        await using var db = Context();
        var now = await DiscoveryDatabase.NowAsync(db, token);
        var row = await db.DiscoveryGenerations.AsNoTracking().SingleOrDefaultAsync(item => item.RefreshId == refreshId &&
            item.ProviderInstanceId == providerId && item.RequestingIssuer != null && item.ReceiptExpiresAtUtc > now, token);
        return row is null ? null : Receipt(row, now);
    }

    public async Task<HostedDiscoveryWork?> TryAcquireAsync(Guid executorId, CancellationToken token)
    {
        if (executorId == Guid.Empty) throw new ArgumentException("Executor identity is required.", nameof(executorId));
        await using var db = Context();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await DiscoveryDatabase.SetTimeoutsAsync(db, inventoryOptions.ClaimTimeoutSeconds, token);
        await SlotLockAsync(db, token);
        var pending = await db.DiscoveryGenerations.AsNoTracking().SingleOrDefaultAsync(row => row.ExecutionSlot != null, token);
        if (pending is null)
        {
            await transaction.CommitAsync(token);
            await transaction.DisposeAsync();
            await db.DisposeAsync();
            await CleanupIdleAsync(token);
            return null;
        }
        var now = await DiscoveryDatabase.NowAsync(db, token);
        if (pending.PublishedAtUtc is not null || pending.LeaseExpiresAtUtc <= now || pending.ExpiresAtUtc <= now)
        {
            await RecoverAsync(db, pending, token);
            await transaction.CommitAsync(token);
            return null;
        }
        if (pending.Status != DiscoveryGenerationStatus.Accepted) return null; // Never resume a started scan.
        var provider = await DiscoveryDatabase.LockProviderAsync(db, pending.ProviderInstanceId, token);
        var secretVersion = await DiscoveryDatabase.LockSecretAsync(db, provider, token);
        var state = await DiscoveryDatabase.LockStateAsync(db, provider.Id, token);
        var generation = await db.DiscoveryGenerations.SingleAsync(row => row.RefreshId == pending.RefreshId, token);
        now = await DiscoveryDatabase.NowAsync(db, token);
        if (state is null || state.Lifecycle != DiscoveryStateLifecycle.Active || state.InFlightGenerationId != generation.RefreshId ||
            state.FenceToken != generation.FenceToken || provider.Version != generation.ProviderVersion ||
            !provider.IsEnabled || secretVersion != generation.ManagedSecretVersion || generation.ExpiresAtUtc <= now ||
            generation.LeaseExpiresAtUtc <= now)
        {
            Abandon(state, generation, now, DiscoveryInventoryError.ContextChanged);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return null;
        }
        generation.ExecutorId = executorId;
        generation.LeaseExpiresAtUtc = LeaseEnd(generation, now);
        generation.Status = DiscoveryGenerationStatus.Staging;
        generation.Version = checked(generation.Version + 1);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(Receipt(generation, now), new(generation.RequestingIssuer!, generation.RequestingSubject!),
            new DiscoveryRefreshSession(Inventory(), provider, generation), generation.ExpiresAtUtc - now);
    }

    public async Task<bool> RenewAsync(Guid refreshId, Guid executorId, CancellationToken token)
    {
        await using var db = Context();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await DiscoveryDatabase.SetTimeoutsAsync(db, 5, token);
        var pending = await db.DiscoveryGenerations.AsNoTracking().SingleOrDefaultAsync(row => row.RefreshId == refreshId, token);
        if (pending is null) return false;
        var state = await DiscoveryDatabase.LockStateAsync(db, pending.ProviderInstanceId, token);
        var row = await db.DiscoveryGenerations.SingleAsync(item => item.RefreshId == refreshId, token);
        var now = await DiscoveryDatabase.NowAsync(db, token);
        if (state?.InFlightGenerationId != refreshId || state.FenceToken != row.FenceToken || row.ExecutorId != executorId ||
            row.ExecutionSlot != 1 || row.LeaseExpiresAtUtc <= now || row.ExpiresAtUtc <= now || row.PublishedAtUtc is not null)
            return false;
        row.LeaseExpiresAtUtc = LeaseEnd(row, now);
        row.Version = checked(row.Version + 1);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return true;
    }

    public async Task CompleteAsync(Guid refreshId, Guid executorId, ProviderDiscoveryRefreshResult result, CancellationToken token)
    {
        await using var db = Context();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await DiscoveryDatabase.SetTimeoutsAsync(db, 5, token);
        var pending = await db.DiscoveryGenerations.AsNoTracking().SingleOrDefaultAsync(row => row.RefreshId == refreshId, token);
        if (pending is null) return;
        var state = await DiscoveryDatabase.LockStateAsync(db, pending.ProviderInstanceId, token);
        var row = await db.DiscoveryGenerations.SingleAsync(item => item.RefreshId == refreshId, token);
        if (row.ExecutorId != executorId) return;
        if (row.PublishedAtUtc is not null)
        {
            row.ExecutionSlot = null;
            row.PublicationUncertain = false;
        }
        else if (row.ExecutionSlot != 1 || state?.InFlightGenerationId != refreshId || state.FenceToken != row.FenceToken)
            return; // A fenced/stopped executor cannot mutate a recovered terminal receipt.
        else if (result.Status == DiscoveryPublicationStatus.Uncertain && row.PublicationAttemptedAtUtc is not null)
            row.PublicationUncertain = true; // Retain the slot until durable reconciliation/fencing.
        else if (row.Status is DiscoveryGenerationStatus.Staging or DiscoveryGenerationStatus.Preparing or DiscoveryGenerationStatus.Ready)
            Abandon(state, row, await DiscoveryDatabase.NowAsync(db, token), result.Error ?? DiscoveryInventoryError.StorageUnavailable);
        else row.ExecutionSlot = null;
        row.Version = checked(row.Version + 1);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    internal static HostedRecoveryAction RecoveryAction(DiscoveryGeneration row, DateTimeOffset now)
    {
        if (row.ExecutionSlot is null || row.PublishedAtUtc is not null || row.Status is DiscoveryGenerationStatus.Published
            or DiscoveryGenerationStatus.Failed or DiscoveryGenerationStatus.Cancelled or DiscoveryGenerationStatus.Superseded or DiscoveryGenerationStatus.Abandoned)
            return HostedRecoveryAction.ReleaseTerminal;
        return row.LeaseExpiresAtUtc > now && row.ExpiresAtUtc > now
            ? HostedRecoveryAction.PreserveLive : HostedRecoveryAction.AbandonExpired;
    }

    private async Task CleanupIdleAsync(CancellationToken token)
    {
        // One bounded batch per idle runner poll; no transaction spans this call or native IO.
        // The short cancellation budget prevents cleanup occupying the runner through an admission lease.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Min(5, hostingOptions.LeaseSeconds / 3)));
        await using var db = Context();
        var now = await DiscoveryDatabase.NowAsync(db, budget.Token);
        var cutoff = now.AddDays(-7);
        var provider = await db.DiscoveryStates.AsNoTracking().Where(state =>
            db.DiscoveryObservations.Any(row => row.ProviderInstanceId == state.ProviderInstanceId &&
                (state.CurrentGenerationId == null || row.RefreshId != state.CurrentGenerationId) &&
                (state.InFlightGenerationId == null || row.RefreshId != state.InFlightGenerationId)) ||
            db.DiscoveryGenerations.Any(row => row.ProviderInstanceId == state.ProviderInstanceId && row.FinishedAtUtc < cutoff &&
                (state.CurrentGenerationId == null || row.RefreshId != state.CurrentGenerationId) &&
                (state.InFlightGenerationId == null || row.RefreshId != state.InFlightGenerationId) &&
                (state.LastAttemptGenerationId == null || row.RefreshId != state.LastAttemptGenerationId)))
            .OrderBy(state => state.ProviderInstanceId).Select(state => (Guid?)state.ProviderInstanceId).FirstOrDefaultAsync(budget.Token);
        if (provider is { } id) await Inventory().CleanupBatchAsync(id, budget.Token);
    }

    private async Task<bool> RecoverAsync(FlowPlaneDbContext db, DiscoveryGeneration previous, CancellationToken token)
    {
        await DiscoveryDatabase.LockProviderAsync(db, previous.ProviderInstanceId, token);
        var state = await DiscoveryDatabase.LockStateAsync(db, previous.ProviderInstanceId, token);
        var row = await db.DiscoveryGenerations.SingleAsync(item => item.RefreshId == previous.RefreshId, token);
        // State lock waits out any concurrent atomic publication transaction. Inspect durable ID, not a prior acknowledgement.
        var action = RecoveryAction(row, await DiscoveryDatabase.NowAsync(db, token));
        if (action == HostedRecoveryAction.PreserveLive) return false;
        if (action == HostedRecoveryAction.ReleaseTerminal)
        {
            row.ExecutionSlot = null;
            row.PublicationUncertain = false;
            row.Version = checked(row.Version + 1);
        }
        else Abandon(state, row, await DiscoveryDatabase.NowAsync(db, token), DiscoveryInventoryError.Expired);
        await db.SaveChangesAsync(token);
        return true;
    }

    private DateTimeOffset LeaseEnd(DiscoveryGeneration row, DateTimeOffset now) =>
        now.AddSeconds(hostingOptions.LeaseSeconds) < row.ExpiresAtUtc ? now.AddSeconds(hostingOptions.LeaseSeconds) : row.ExpiresAtUtc;

    private static void Abandon(DiscoveryState? state, DiscoveryGeneration row, DateTimeOffset now, DiscoveryInventoryError error)
    {
        if (state?.InFlightGenerationId == row.RefreshId && state.FenceToken == row.FenceToken)
        {
            state.InFlightGenerationId = null;
            state.FenceToken = checked(state.FenceToken + 1);
        }
        row.Status = DiscoveryGenerationStatus.Abandoned;
        row.FinishedAtUtc = now;
        row.Error = error;
        row.ExecutionSlot = null;
        row.PublicationUncertain = false;
        row.Version = checked(row.Version + 1);
    }

    internal static HostedDiscoveryReceipt Receipt(DiscoveryGeneration row, DateTimeOffset now)
    {
        var phase = row.Status switch
        {
            DiscoveryGenerationStatus.Accepted => HostedDiscoveryPhase.Accepted,
            DiscoveryGenerationStatus.Staging => HostedDiscoveryPhase.Scanning,
            DiscoveryGenerationStatus.Preparing => HostedDiscoveryPhase.Preparing,
            DiscoveryGenerationStatus.Ready => row.PublicationUncertain || row.LeaseExpiresAtUtc <= now
                ? HostedDiscoveryPhase.PublicationUncertain : HostedDiscoveryPhase.Publishing,
            DiscoveryGenerationStatus.Published => HostedDiscoveryPhase.Published,
            DiscoveryGenerationStatus.Abandoned or DiscoveryGenerationStatus.Cancelled or DiscoveryGenerationStatus.Superseded => HostedDiscoveryPhase.Abandoned,
            _ => row.Error is DiscoveryInventoryError.StorageUnavailable or DiscoveryInventoryError.TimedOut or DiscoveryInventoryError.ProviderUnavailable
                ? HostedDiscoveryPhase.Unavailable : HostedDiscoveryPhase.Failed,
        };
        return new(row.RefreshId, row.ProviderInstanceId, phase, row.StartedAtUtc, row.ExpiresAtUtc,
            row.ObservationCount, row.CandidateCount, row.ScanCompleted, row.Error);
    }
}
