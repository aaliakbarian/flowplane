using FlowPlane.Application.Providers.DiscoveryInventory;

using Microsoft.EntityFrameworkCore;

namespace FlowPlane.Infrastructure.Persistence.Discovery;

internal sealed partial class ProviderDiscoveryInventoryStore(
    DbContextOptions<FlowPlaneDbContext> databaseOptions, DiscoveryInventoryOptions options) : IProviderDiscoveryInventoryStore
{
    internal FlowPlaneDbContext CreateContext() => new(databaseOptions);
    internal DiscoveryInventoryOptions Options => options;

    public async Task<IProviderDiscoveryRefreshSession> BeginRefreshAsync(Guid providerInstanceId, CancellationToken cancellationToken)
    {
        options.Validate();
        try
        {
            await CleanupAsync(providerInstanceId, cancellationToken);
            await using var db = CreateContext();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await DiscoveryDatabase.SetTimeoutsAsync(db, options.ClaimTimeoutSeconds, cancellationToken);
            var provider = await DiscoveryDatabase.LockProviderAsync(db, providerInstanceId, cancellationToken);
            var secretVersion = await DiscoveryDatabase.LockSecretAsync(db, provider, cancellationToken);
            if (!provider.IsEnabled) throw new DiscoveryInventoryException(DiscoveryInventoryError.ProviderUnavailable);
            await DiscoveryDatabase.EnsureStateAsync(db, providerInstanceId, cancellationToken);
            var state = (await DiscoveryDatabase.LockStateAsync(db, providerInstanceId, cancellationToken))!;
            if (state.Lifecycle != DiscoveryStateLifecycle.Active) throw new DiscoveryInventoryException(DiscoveryInventoryError.ProviderUnavailable);
            var now = await DiscoveryDatabase.NowAsync(db, cancellationToken);
            if (state.InFlightGenerationId is { } previousId)
            {
                var previous = await db.DiscoveryGenerations.SingleAsync(row => row.RefreshId == previousId, cancellationToken);
                if (previous.ExpiresAtUtc > now) throw new DiscoveryInventoryException(DiscoveryInventoryError.RefreshInProgress);
                previous.Status = DiscoveryGenerationStatus.Abandoned;
                previous.ExecutionSlot = null;
                previous.PublicationUncertain = false;
                previous.FinishedAtUtc = now;
                previous.Error = DiscoveryInventoryError.Expired;
                previous.Version = checked(previous.Version + 1);
                // Release the partial unique index before adding the replacement generation.
                await db.SaveChangesAsync(cancellationToken);
            }
            state.FenceToken = checked(state.FenceToken + 1);
            var generation = new DiscoveryGeneration
            {
                RefreshId = Guid.CreateVersion7(),
                ProviderInstanceId = providerInstanceId,
                BaseGenerationId = state.CurrentGenerationId,
                ExpectedPublicationVersion = state.PublicationVersion,
                FenceToken = state.FenceToken,
                ProviderVersion = provider.Version,
                ProviderType = provider.ProviderType.Value,
                CredentialKind = provider.SecretReference.Scheme.ToString(),
                ManagedSecretVersion = secretVersion,
                AccessContextId = Guid.CreateVersion7(),
                StartedAtUtc = now,
                ExpiresAtUtc = now + options.RefreshTimeout,
            };
            db.DiscoveryGenerations.Add(generation);
            await db.SaveChangesAsync(cancellationToken);
            state.InFlightGenerationId = generation.RefreshId;
            state.LastAttemptGenerationId = generation.RefreshId;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new DiscoveryRefreshSession(this, provider, generation);
        }
        catch (OperationCanceledException) { throw; }
        catch (DiscoveryInventoryException) { throw; }
        catch (Exception) { throw new DiscoveryInventoryException(DiscoveryInventoryError.StorageUnavailable); }
    }

    public async Task<ProviderDiscoveryRefreshResult> ReconcileAsync(Guid refreshId, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = CreateContext();
            var generation = await db.DiscoveryGenerations.AsNoTracking().SingleOrDefaultAsync(row => row.RefreshId == refreshId, cancellationToken);
            if (generation?.PublishedAtUtc is not null) return new(refreshId, DiscoveryPublicationStatus.Published, null);
            if (generation is not null && generation.Status is DiscoveryGenerationStatus.Failed or DiscoveryGenerationStatus.Cancelled
                    or DiscoveryGenerationStatus.Abandoned or DiscoveryGenerationStatus.Superseded)
                return new(refreshId, DiscoveryPublicationStatus.NotPublished, generation.Error);
            return new(refreshId, DiscoveryPublicationStatus.Uncertain, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new(refreshId, DiscoveryPublicationStatus.Uncertain, DiscoveryInventoryError.StorageUnavailable); }
    }

    internal async Task AbortAsync(Guid providerId, Guid refreshId, long fence, DiscoveryInventoryError reason, CancellationToken token)
    {
        await using var db = CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await DiscoveryDatabase.SetTimeoutsAsync(db, 5, token);
        var state = await DiscoveryDatabase.LockStateAsync(db, providerId, token);
        if (state is null || state.InFlightGenerationId != refreshId || state.FenceToken != fence) return;
        var generation = await db.DiscoveryGenerations.SingleAsync(row => row.RefreshId == refreshId, token);
        if (generation.PublishedAtUtc is not null) return;
        generation.Status = reason switch
        {
            DiscoveryInventoryError.Cancelled => DiscoveryGenerationStatus.Cancelled,
            DiscoveryInventoryError.ContextChanged or DiscoveryInventoryError.Superseded => DiscoveryGenerationStatus.Superseded,
            DiscoveryInventoryError.Expired => DiscoveryGenerationStatus.Abandoned,
            _ => DiscoveryGenerationStatus.Failed,
        };
        generation.Error = reason;
        generation.ExecutionSlot = null;
        generation.PublicationUncertain = false;
        generation.FinishedAtUtc = await DiscoveryDatabase.NowAsync(db, token);
        generation.Version = checked(generation.Version + 1);
        state.InFlightGenerationId = null;
        state.FenceToken = checked(state.FenceToken + 1);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }
}
