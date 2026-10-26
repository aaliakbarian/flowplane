using System.Security.Cryptography;
using System.Text.Json;

using FlowPlane.Application.Providers.DiscoveryInventory;

using Microsoft.AspNetCore.WebUtilities;

namespace FlowPlane.Api.Providers;

/// <summary>
/// Authenticated opaque continuation, not an authorization token. Process-local signing
/// avoids an optional root-key dependency; restart invalidates cursors, requiring page one.
/// No credential/key-ring operation is performed by this codec.
/// </summary>
internal sealed class InventoryCursorCodec(TimeProvider? timeProvider = null) : IInventoryCursorCodec, IDisposable
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
    private sealed record Envelope(InventoryCursor Cursor, long Expires);

    public string Encode(InventoryCursor cursor)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Envelope(cursor, clock.GetUtcNow().AddMinutes(30).ToUnixTimeSeconds()));
        return WebEncoders.Base64UrlEncode(payload) + "." + WebEncoders.Base64UrlEncode(HMACSHA256.HashData(key, payload));
    }

    public InventoryCursor Decode(string cursor)
    {
        try
        {
            if (cursor.Length is < 40 or > 2048) throw new FormatException();
            var parts = cursor.Split('.');
            if (parts.Length != 2) throw new FormatException();
            var payload = WebEncoders.Base64UrlDecode(parts[0]);
            var mac = WebEncoders.Base64UrlDecode(parts[1]);
            if (!CryptographicOperations.FixedTimeEquals(mac, HMACSHA256.HashData(key, payload)) ||
                WebEncoders.Base64UrlEncode(payload) != parts[0] || WebEncoders.Base64UrlEncode(mac) != parts[1])
                throw new FormatException();
            var envelope = JsonSerializer.Deserialize<Envelope>(payload) ?? throw new FormatException();
            if (envelope.Expires <= clock.GetUtcNow().ToUnixTimeSeconds() ||
                envelope.Cursor.AfterDigest.Length != 64 || envelope.Cursor.AfterDigest.Any(c => !Uri.IsHexDigit(c)))
                throw new FormatException();
            return envelope.Cursor;
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException or NullReferenceException)
        { throw new InventoryBrowseException(InventoryBrowseError.InvalidCursor); }
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(key);
}
