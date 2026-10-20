using FlowPlane.Application.Providers;
using FlowPlane.Application.Providers.DiscoveryInventory;
using FlowPlane.Domain.Providers;
using FlowPlane.Infrastructure.Secrets;
using FlowPlane.Infrastructure.Persistence.Discovery;

using Microsoft.EntityFrameworkCore;

namespace FlowPlane.Infrastructure.Persistence;

internal sealed class ProviderInstanceStore(
    FlowPlaneDbContext context,
    ManagedSecretStore secretStore) : IProviderInstanceStore
{
    public async Task<ProviderInstance> CreateWithManagedSecretAsync(
        Guid id,
        ProviderType providerType,
        string displayName,
        bool isEnabled,
        ProviderConfigurationDocument configuration,
        ReadOnlyMemory<char> secret,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var reference = await secretStore.CreateAsync(secret, cancellationToken);
        var instance = ProviderInstance.Create(
            id,
            providerType,
            displayName,
            isEnabled,
            configuration,
            reference,
            now);
        context.ProviderInstances.Add(instance);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return instance;
    }

    public async Task ReplaceManagedSecretAsync(
        Guid providerInstanceId,
        long expectedVersion,
        ReadOnlyMemory<char> secret,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await DiscoveryDatabase.SetTimeoutsAsync(context, 30, cancellationToken);
        var instance = await DiscoveryDatabase.LockProviderAsync(context, providerInstanceId, cancellationToken);
        var retirement = await context.DiscoveryStates.AsNoTracking().SingleOrDefaultAsync(row => row.ProviderInstanceId == providerInstanceId, cancellationToken);
        if (retirement?.Lifecycle == DiscoveryStateLifecycle.Deleting)
            throw new DiscoveryInventoryException(DiscoveryInventoryError.ProviderUnavailable);
        instance.MarkCredentialChanged(expectedVersion, now);
        await secretStore.ReplaceAsync(instance.SecretReference, secret, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteAsync(
        Guid providerInstanceId,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        // Retirement is durable. Interrupted deletion can be resumed without resurrecting inventory.
        await using (var retirement = await context.Database.BeginTransactionAsync(cancellationToken))
        {
            await DiscoveryDatabase.SetTimeoutsAsync(context, 30, cancellationToken);
            var retiring = await DiscoveryDatabase.LockProviderAsync(context, providerInstanceId, cancellationToken);
            if (retiring.Version != expectedVersion)
                throw new DbUpdateConcurrencyException("The provider instance was modified concurrently.");
            await DiscoveryDatabase.LockSecretAsync(context, retiring, cancellationToken);
            await ProviderDiscoveryInventoryStore.RetireAsync(context, providerInstanceId, cancellationToken);
            await retirement.CommitAsync(cancellationToken);
        }
        context.ChangeTracker.Clear();
        await ProviderDiscoveryInventoryStore.PurgeRetiredAsync(context, providerInstanceId, cancellationToken);
        context.ChangeTracker.Clear();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await DiscoveryDatabase.SetTimeoutsAsync(context, 30, cancellationToken);
        var instance = await DiscoveryDatabase.LockProviderAsync(context, providerInstanceId, cancellationToken);
        await DiscoveryDatabase.LockSecretAsync(context, instance, cancellationToken);
        if (instance.Version != expectedVersion)
            throw new DbUpdateConcurrencyException("The provider instance was modified concurrently.");
        if (instance.SecretReference.Scheme == SecretReferenceScheme.FlowPlane)
        {
            context.ProviderInstances.Remove(instance);
            await context.SaveChangesAsync(cancellationToken);
            await secretStore.DeleteOwnedAsync(instance.SecretReference, cancellationToken);
        }
        else
        {
            context.ProviderInstances.Remove(instance);
            await context.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

}
