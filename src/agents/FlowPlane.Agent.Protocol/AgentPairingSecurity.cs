using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FlowPlane.Agent.Protocol;

/// <summary>Public context checks only; no key creation, transport or authorization.</summary>
public static class AgentPairingSecurity
{
    public static bool IsClientCertificate(ReadOnlyMemory<byte> der, DateTimeOffset now) => AgentCertificateValidation.IsValid(der, now);

    public static string VerificationPhrase(Guid grantId, Guid claimId, string pinSha256, string serverNonce)
    {
        if (grantId == Guid.Empty || claimId == Guid.Empty || pinSha256 is not { Length: 64 } ||
            pinSha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) throw new AgentProtocolException();
        _ = DecodeToken(serverNonce);
        var context = $"flowplane.agent/pairing-phrase.v1\n{grantId:D}\n{claimId:D}\n{pinSha256}\n{serverNonce}";
        var hex = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(context)));
        return $"{hex[..4]}-{hex[4..8]}-{hex[8..12]}-{hex[12..16]}";
    }

    public static string RecoveryDigest(int unresolvedCount, bool requiresReconciliation)
    {
        if (unresolvedCount is < 0 or > 4096) throw new AgentProtocolException();
        var context = "flowplane.agent/recovery.v1|" + unresolvedCount.ToString(CultureInfo.InvariantCulture) + "|" +
            (requiresReconciliation || unresolvedCount > 0 ? "unsettled" : "settled");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(context)));
    }

    public static string EncodeToken(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 32) throw new AgentProtocolException();
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static byte[] DecodeToken(string value)
    {
        if (value is not { Length: 43 } || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) throw new AgentProtocolException();
        try
        {
            var bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + "=");
            if (bytes.Length != 32 || EncodeToken(bytes) != value) throw new AgentProtocolException();
            return bytes;
        }
        catch (FormatException) { throw new AgentProtocolException(); }
    }
}
