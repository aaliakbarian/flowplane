using FlowPlane.Application.Providers;
using FlowPlane.Domain.Providers;
using FlowPlane.Infrastructure.Secrets;

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
        var instance = await GetRequiredAsync(providerInstanceId, cancellationToken);
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
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var instance = await GetRequiredAsync(providerInstanceId, cancellationToken);
        if (instance.Version != expectedVersion)
        {
            throw new DbUpdateConcurrencyException("The provider instance was modified concurrently.");
        }

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

    private async Task<ProviderInstance> GetRequiredAsync(
        Guid id,
        CancellationToken cancellationToken) =>
        await context.ProviderInstances.SingleOrDefaultAsync(
            item => item.Id == id,
            cancellationToken) ?? throw new KeyNotFoundException("Provider instance was not found.");
}
