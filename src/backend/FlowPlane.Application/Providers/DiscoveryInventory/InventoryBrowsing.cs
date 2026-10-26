using System.Text.Json;

using FlowPlane.Domain.Providers;

namespace FlowPlane.Application.Providers.DiscoveryInventory;

public enum InventoryBrowseError
{
    InvalidRequest, InvalidCursor, GenerationChanged, NotPublished, ProviderUnavailable,
    ContextChanged, NativeChanged, NativeDenied, NativeUnavailable, StorageUnavailable,
}

public sealed class InventoryBrowseException(InventoryBrowseError code)
    : Exception($"Inventory browsing could not complete ({code}).")
{
    public InventoryBrowseError Code { get; } = code;
}

/// <summary>Internal context only. Never serialize this credential/version fence as an API response.</summary>
public sealed record InventoryReadContext(Guid ProviderInstanceId, Guid GenerationId,
    long ProviderVersion, long PublicationVersion, long? ManagedSecretVersion, Guid AccessContextId);

public sealed record InventoryPageQuery(string? SchemaId = null, bool CandidatesOnly = false, int Limit = 50)
{
    public void Validate(IReadOnlyList<string> allowedSchemas)
    {
        if (Limit is < 1 or > 100 || allowedSchemas.Count is < 1 or > 16 ||
            SchemaId is not null && !allowedSchemas.Contains(SchemaId, StringComparer.Ordinal))
            throw new InventoryBrowseException(InventoryBrowseError.InvalidRequest);
    }
}

public sealed record InventoryCursor(Guid ProviderInstanceId, Guid GenerationId, string? SchemaId,
    bool CandidatesOnly, int Limit, string AfterDigest, string Ordering = "native-digest-asc.v1");

public interface IInventoryCursorCodec
{
    string Encode(InventoryCursor cursor);
    InventoryCursor Decode(string cursor);
}

public sealed record InventoryStoredPage(IReadOnlyList<StoredDiscoveryObservation> Observations, string? NextDigest,
    StoredDiscoveryObservation? Lookahead = null);

public interface IProviderInventoryPageStore
{
    Task<IProviderInventoryPageReader> OpenPageReaderAsync(Guid providerId, long providerVersion,
        Guid? expectedGenerationId, CancellationToken token);
    Task<bool> IsCurrentAsync(InventoryReadContext context, CancellationToken token);
}

/// <summary>Invocation-scoped short read-only REPEATABLE READ. Dispose before any native IO.</summary>
public interface IProviderInventoryPageReader : IAsyncDisposable
{
    InventoryReadContext Context { get; }
    Task<InventoryStoredPage> ReadPageAsync(InventoryPageQuery query, IReadOnlyList<string> allowedSchemas,
        string? afterDigest, CancellationToken token);
    Task<IReadOnlyList<StoredDiscoveryObservation>> ReadScopeAsync(IReadOnlyList<string> nativeKeys, CancellationToken token);
}

public sealed record InventoryNativeReference(Guid ProviderInstanceId, string ProviderType, string SchemaId, JsonElement Data);
public sealed record InventoryItem(string SchemaId, JsonElement NativeIdentity, JsonElement Metadata,
    InventoryNativeReference? ExecutableReference);
public sealed record InventoryBrowseResult(Guid ProviderInstanceId, Guid GenerationId,
    IReadOnlyList<InventoryItem> Items, string? NextCursor);

/// <summary>Provider-owned codecs, pure parent-key calculation and targeted live read-only checks.</summary>
public interface IProviderInventoryDisclosure
{
    ProviderType ProviderType { get; }
    IReadOnlyList<string> SchemaIds { get; }
    IReadOnlyList<string> RequiredScopeKeys(IReadOnlyList<StoredDiscoveryObservation> observations);
    Task VerifyAsync(ProviderInstance provider, IReadOnlyList<StoredDiscoveryObservation> observations,
        Func<CancellationToken, Task<bool>> isCurrent, CancellationToken token);
    InventoryItem Project(Guid providerId, StoredDiscoveryObservation observation);
}

public sealed class InventoryBrowsingService(IProviderInventoryPageStore store, IInventoryCursorCodec cursors)
{
    public async Task<InventoryBrowseResult> ReadAsync(ProviderInstance provider, InventoryPageQuery query,
        string? cursorText, IProviderInventoryDisclosure disclosure,
        Func<CancellationToken, Task<bool>> authorizeCurrent, CancellationToken token)
    {
        query.Validate(disclosure.SchemaIds);
        if (!provider.IsEnabled || !provider.ProviderType.Equals(disclosure.ProviderType) || !await authorizeCurrent(token))
            throw new InventoryBrowseException(InventoryBrowseError.ProviderUnavailable);
        var cursor = cursorText is null ? null : cursors.Decode(cursorText);
        if (cursor is not null && (cursor.ProviderInstanceId != provider.Id || cursor.SchemaId != query.SchemaId ||
            cursor.CandidatesOnly != query.CandidatesOnly || cursor.Limit != query.Limit || cursor.Ordering != "native-digest-asc.v1"))
            throw new InventoryBrowseException(InventoryBrowseError.InvalidCursor);
        InventoryReadContext context;
        InventoryStoredPage page;
        IReadOnlyList<StoredDiscoveryObservation> selected;
        IReadOnlyList<StoredDiscoveryObservation> scope;
        await using (var reader = await store.OpenPageReaderAsync(provider.Id, provider.Version, cursor?.GenerationId, token))
        {
            context = reader.Context;
            page = await reader.ReadPageAsync(query, disclosure.SchemaIds, cursor?.AfterDigest, token);
            selected = page.Lookahead is null ? page.Observations : page.Observations.Append(page.Lookahead).ToArray();
            scope = await reader.ReadScopeAsync(disclosure.RequiredScopeKeys(selected), token);
        } // The PostgreSQL transaction ends before resolving secrets or contacting SQL Server.
        async Task<bool> Current(CancellationToken ct) => await authorizeCurrent(ct) && await store.IsCurrentAsync(context, ct);
        await disclosure.VerifyAsync(provider, selected.Concat(scope).DistinctBy(row => row.NativeKey, StringComparer.Ordinal).ToArray(), Current, token);
        if (!await Current(token)) throw new InventoryBrowseException(InventoryBrowseError.ContextChanged);
        // Provider-owned, allowlisted projection only, never generic stored JSON serialization.
        var items = page.Observations.Select(row => disclosure.Project(provider.Id, row)).ToArray();
        if (!await Current(token)) throw new InventoryBrowseException(InventoryBrowseError.ContextChanged);
        var next = page.NextDigest is null ? null : cursors.Encode(new(provider.Id, context.GenerationId,
            query.SchemaId, query.CandidatesOnly, query.Limit, page.NextDigest));
        return new(provider.Id, context.GenerationId, items, next);
    }
}
