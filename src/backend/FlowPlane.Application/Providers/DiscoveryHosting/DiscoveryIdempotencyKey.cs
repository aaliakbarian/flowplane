using System.Globalization;

namespace FlowPlane.Application.Providers.DiscoveryHosting;

public static class DiscoveryIdempotencyKey
{
    public static Guid Parse(string? value)
    {
        if (value is null || !Guid.TryParseExact(value, "D", out var key) || key.ToString("D") != value ||
            value[14] != '7' || value[19] is not ('8' or '9' or 'a' or 'b'))
            throw new HostedDiscoveryException(HostedDiscoveryError.InvalidKey);
        return key;
    }

    public static void ValidateFresh(Guid key, DateTimeOffset databaseNow)
    {
        var text = key.ToString("D");
        Parse(text);
        var milliseconds = long.Parse(text[..8] + text.Substring(9, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var now = databaseNow.ToUnixTimeMilliseconds();
        if (milliseconds < now - 300_000L || milliseconds > now + 300_000L)
            throw new HostedDiscoveryException(HostedDiscoveryError.InvalidKey);
    }
}
