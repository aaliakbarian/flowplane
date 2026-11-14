using System.Security.Cryptography;
using System.Text;

using FlowPlane.Application.Secrets;
using FlowPlane.Domain.Providers;
using FlowPlane.Infrastructure.Persistence;
using FlowPlane.Infrastructure.Persistence.Discovery;
using FlowPlane.Infrastructure.Persistence.Agents;

using Microsoft.EntityFrameworkCore;

namespace FlowPlane.Infrastructure.Secrets;

internal sealed class ManagedSecretStore(
    FlowPlaneDbContext context,
    ISecretProtector protector) : IManagedSecretStore
{
    public async ValueTask<SecretReference> CreateAsync(
        ReadOnlyMemory<char> secret,
        CancellationToken cancellationToken = default)
    {
        ValidateSecret(secret);
        var id = Guid.CreateVersion7();
        var ciphertext = Protect(id, secret.Span);
        context.SecretRecords.Add(new SecretRecord(id, ciphertext, DateTimeOffset.UtcNow));
        await context.SaveChangesAsync(cancellationToken);
        return SecretReference.FlowPlane(id);
    }

    public async ValueTask ReplaceAsync(
        SecretReference reference,
        ReadOnlyMemory<char> secret,
        CancellationToken cancellationToken = default)
    {
        // Direct replacement must serialize through the actual owners, not just the ciphertext row.
        if (context.Database.CurrentTransaction is not null)
            throw new SecretResolutionException(SecretErrorCode.ConcurrencyConflict, "The secret replacement context is unavailable.");
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await DiscoveryDatabase.SetTimeoutsAsync(context, 30, cancellationToken);
        var ownerIds = await context.ProviderInstances.AsNoTracking().Where(p => p.SecretReference == reference)
            .OrderBy(p => p.Id).Select(p => p.Id).Take(33).ToListAsync(cancellationToken);
        if (ownerIds.Count > 32) throw new SecretResolutionException(SecretErrorCode.ConcurrencyConflict, "The secret ownership context is unavailable.");
        foreach (var ownerId in ownerIds)
        {
            var owner = await DiscoveryDatabase.LockProviderAsync(context, ownerId, cancellationToken);
            if (owner.SecretReference != reference) throw new SecretResolutionException(SecretErrorCode.ConcurrencyConflict, "The secret ownership context changed.");
            owner.MarkCredentialChanged(owner.Version, DateTimeOffset.UtcNow);
        }
        var secretId = GetManagedSecretId(reference);
        _ = await context.SecretRecords.FromSqlInterpolated($"SELECT * FROM \"FlowPlaneSecrets\" WHERE \"Id\" = {secretId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken) ?? throw NotFound();
        var currentOwnerIds = await context.ProviderInstances.AsNoTracking().Where(p => p.SecretReference == reference)
            .OrderBy(p => p.Id).Select(p => p.Id).Take(33).ToListAsync(cancellationToken);
        if (!ownerIds.SequenceEqual(currentOwnerIds))
            throw new SecretResolutionException(SecretErrorCode.ConcurrencyConflict, "The secret ownership context changed.");
        await AgentDatabase.InvalidateProvidersAsync(context, ownerIds, cancellationToken);
        await ReplaceLockedAsync(reference, secret, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // Only ProviderInstanceStore/direct ReplaceAsync call this after provider -> secret -> agent locking.
    internal async ValueTask ReplaceLockedAsync(SecretReference reference, ReadOnlyMemory<char> secret, CancellationToken cancellationToken)
    {
        ValidateSecret(secret);
        var id = GetManagedSecretId(reference);
        var record = await context.SecretRecords.SingleOrDefaultAsync(
            item => item.Id == id,
            cancellationToken) ?? throw NotFound();
        record.Replace(Protect(id, secret.Span), DateTimeOffset.UtcNow);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask DeleteAsync(
        SecretReference reference,
        CancellationToken cancellationToken = default)
    {
        if (await context.ProviderInstances.AnyAsync(
                instance => instance.SecretReference == reference,
                cancellationToken))
        {
            throw new SecretResolutionException(
                SecretErrorCode.ConcurrencyConflict,
                "The managed secret is still owned by a provider instance.");
        }

        await DeleteOwnedAsync(reference, cancellationToken);
    }

    internal async ValueTask DeleteOwnedAsync(
        SecretReference reference,
        CancellationToken cancellationToken)
    {
        var id = GetManagedSecretId(reference);
        var record = await context.SecretRecords.SingleOrDefaultAsync(
            item => item.Id == id,
            cancellationToken) ?? throw NotFound();
        context.SecretRecords.Remove(record);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask<TResult> UseAsync<TResult>(
        SecretReference reference,
        Func<ReadOnlyMemory<char>, CancellationToken, ValueTask<TResult>> use,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(use);
        var id = GetManagedSecretId(reference);
        var record = await context.SecretRecords.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == id,
            cancellationToken) ?? throw NotFound();
        var plaintext = protector.Unprotect(id, record.Ciphertext);
        var characters = Encoding.UTF8.GetChars(plaintext);
        CryptographicOperations.ZeroMemory(plaintext);
        try
        {
            return await use(characters, cancellationToken);
        }
        finally
        {
            Array.Clear(characters);
        }
    }

    private byte[] Protect(Guid id, ReadOnlySpan<char> secret)
    {
        var plaintext = new byte[Encoding.UTF8.GetByteCount(secret)];
        Encoding.UTF8.GetBytes(secret, plaintext);
        try
        {
            return protector.Protect(id, plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static Guid GetManagedSecretId(SecretReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!reference.TryGetFlowPlaneId(out var id))
        {
            throw new SecretResolutionException(
                SecretErrorCode.UnsupportedReference,
                "The managed secret store accepts only FlowPlane-managed references.");
        }

        return id;
    }

    private static void ValidateSecret(ReadOnlyMemory<char> secret)
    {
        if (secret.IsEmpty)
        {
            throw new ArgumentException("Secret material must not be empty.", nameof(secret));
        }
    }

    private static SecretResolutionException NotFound() => new(
        SecretErrorCode.SecretNotFound,
        "The managed secret does not exist.");
}
