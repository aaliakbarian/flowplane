using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowPlane.Agent.Protocol;

/// <summary>Closed wire format only. Validation conveys neither authenticated identity nor operation authority.</summary>
public static class AgentProtocolCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        AllowDuplicateProperties = false,
        MaxDepth = AgentProtocol.MaxDepth,
        Converters =
        {
            new CanonicalGuidConverter(), new DecimalInt64Converter(),
            new ExactEnumConverter<PairingStage>(), new ExactEnumConverter<AgentAvailability>(),
            new ExactEnumConverter<AgentReadiness>(), new ExactEnumConverter<AgentRecoveryMode>(),
            new ExactEnumConverter<AgentWorkKind>(), new ExactEnumConverter<AgentAcknowledgementPhase>(),
        },
    };

    public static PairingClaimRequest DecodePairingClaim(ReadOnlySpan<byte> json) => Decode<PairingClaimRequest>(json);
    public static AgentPollRequest DecodePoll(ReadOnlySpan<byte> json) => Decode<AgentPollRequest>(json);
    public static byte[] EncodePairingReceipt(PairingClaimReceipt receipt) => Encode(receipt);

    public static T Decode<T>(ReadOnlySpan<byte> json) where T : class
    {
        var schema = Schema(typeof(T));
        if (json.IsEmpty || json.Length > AgentProtocol.MaxPayloadBytes) throw new AgentProtocolException();
        try
        {
            var message = JsonSerializer.Deserialize<T>(json, Options);
            Validate(message, schema);
            return message!;
        }
        catch (JsonException)
        {
            // Do not retain serializer paths, input snippets or inner exceptions containing transient secrets.
            throw new AgentProtocolException();
        }
    }

    public static byte[] Encode<T>(T message) where T : class
    {
        var schema = Schema(typeof(T));
        Validate(message, schema);
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(message, Options);
            if (json.Length > AgentProtocol.MaxPayloadBytes) throw new AgentProtocolException();
            return json;
        }
        catch (JsonException)
        {
            throw new AgentProtocolException();
        }
    }

    /// <summary>SHA-256 of a canonical, versioned, non-secret operation context; not a signature or permit.</summary>
    public static string Fingerprint(AgentOperationKey key)
    {
        if (!ValidOperation(key)) throw new AgentProtocolException();
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new { Schema = "flowplane.agent/operation-key.v1", Key = key }, Options);
        return Convert.ToHexStringLower(SHA256.HashData(canonical));
    }

    private static string Schema(Type type) => type switch
    {
        _ when type == typeof(PairingClaimRequest) => "flowplane.agent/pairing-claim.v1",
        _ when type == typeof(PairingClaimReceipt) => "flowplane.agent/pairing-claim-receipt.v1",
        _ when type == typeof(PairingProofRequest) => "flowplane.agent/pairing-proof.v1",
        _ when type == typeof(PairingStatusReceipt) => "flowplane.agent/pairing-status.v1",
        _ when type == typeof(AgentConnectRequest) => "flowplane.agent/connect.v1",
        _ when type == typeof(AgentSessionStatusReceipt) => "flowplane.agent/session-status.v1",
        _ when type == typeof(AgentSessionReceipt) => "flowplane.agent/session-receipt.v1",
        _ when type == typeof(AgentPollRequest) => "flowplane.agent/poll.v1",
        _ when type == typeof(AgentPollReceipt) => "flowplane.agent/poll-receipt.v1",
        _ when type == typeof(AgentRotationRequest) => "flowplane.agent/rotation.v1",
        _ when type == typeof(AgentAssignmentsReceipt) => "flowplane.agent/assignments.v1",
        _ when type == typeof(AgentOperationAcknowledgement) => "flowplane.agent/operation-acknowledgement.v1",
        _ => throw new AgentProtocolException(),
    };

    private static void Validate(object? message, string schema)
    {
        var valid = message switch
        {
            PairingClaimRequest m => m.Schema == schema && Id(m.ClaimId) && Id(m.GrantId) &&
                Token(m.PairingCode) && CertificateBytes(m.PublicCertificateDerBase64),
            PairingClaimReceipt m => m.Schema == schema && Id(m.GrantId) && Id(m.ClaimId) && Id(m.AgentId) &&
                Digest(m.PinSha256) && Token(m.ServerNonce) && Utc(m.ExpiresAtUtc) && Enum.IsDefined(m.Stage) && m.ReceiptVersion > 0,
            PairingProofRequest m => m.Schema == schema && Id(m.ClaimId) && Id(m.ProofRequestId) && Token(m.ServerNonce),
            PairingStatusReceipt m => m.Schema == schema && Id(m.GrantId) && Id(m.ClaimId) && Enum.IsDefined(m.Stage) &&
                Utc(m.ExpiresAtUtc) && Phrase(m.VerificationPhrase) && m.ReceiptVersion > 0,
            AgentConnectRequest m => m.Schema == schema && Id(m.BootId) && Id(m.ConnectRequestId) &&
                PackageVersion(m.PackageVersion) && m.ProtocolVersion == AgentProtocol.Version && m.ExpectedFence > 0 && Digest(m.JournalRecoveryDigest),
            AgentSessionStatusReceipt m => m.Schema == schema && Id(m.AgentId) && m.Fence > 0 && Utc(m.ServerTimeUtc) &&
                (m.LeaseExpiresAtUtc is null || Utc(m.LeaseExpiresAtUtc.Value)) && Enum.IsDefined(m.RecoveryMode),
            AgentSessionReceipt m => m.Schema == schema && Id(m.AgentId) && Id(m.SessionId) && Id(m.BootId) &&
                m.Fence > 0 && m.AuthorizationVersion > 0 && Lease(m.ServerTimeUtc, m.LeaseExpiresAtUtc) && Enum.IsDefined(m.RecoveryMode),
            AgentPollRequest m => m.Schema == schema && Id(m.SessionId) && m.Fence > 0 && m.Sequence > 0 && Id(m.RequestId) &&
                Enum.IsDefined(m.Availability) && Enum.IsDefined(m.Readiness) && m.UnresolvedEvidenceCount >= 0,
            AgentPollReceipt m => m.Schema == schema && Id(m.SessionId) && m.Fence > 0 && m.Sequence > 0 &&
                Lease(m.AcknowledgedAtUtc, m.LeaseExpiresAtUtc) && m.NextPollSeconds is >= 1 and <= 15 && m.Work == AgentWorkKind.None,
            AgentRotationRequest m => m.Schema == schema && Id(m.ProviderInstanceId) && m.ProviderVersion > 0 &&
                Id(m.RequestId) && Id(m.ClaimId) && CertificateBytes(m.PublicCertificateDerBase64),
            AgentAssignmentsReceipt m => m.Schema == schema && Id(m.AgentId) && m.Providers is { Length: >= 1 and <= 32 } &&
                m.Providers.All(provider => provider is not null && Id(provider.ProviderInstanceId) && provider.ProviderVersion > 0) &&
                m.Providers.Select(provider => provider.ProviderInstanceId).Distinct().Count() == m.Providers.Length,
            AgentOperationAcknowledgement m => m.Schema == schema && ValidOperation(m.Operation) && Enum.IsDefined(m.Phase) &&
                Id(m.ReceiptId) && Utc(m.RecordedAtUtc),
            _ => false,
        };
        if (!valid) throw new AgentProtocolException();
    }

    private static bool Id(Guid value) => value != Guid.Empty;
    private static bool Utc(DateTimeOffset value) => value.Offset == TimeSpan.Zero && value > DateTimeOffset.UnixEpoch;
    private static bool Lease(DateTimeOffset start, DateTimeOffset end) => Utc(start) && Utc(end) && end > start;
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(Hex);
    private static bool Hex(char value) => value is >= '0' and <= '9' or >= 'a' and <= 'f';
    private static bool ValidOperation(AgentOperationKey? key) => key is not null && Id(key.ContextId) && Id(key.OperationId) &&
        key.Ordinal is >= 1 and <= 4 && key.Fence > 0 && Digest(key.PayloadDigest);

    private static bool PackageVersion(string? value) => value is { Length: >= 3 and <= 64 } &&
        Version.TryParse(value, out var version) && version.ToString() == value;

    private static bool Phrase(string? value) => value is { Length: 19 } && value.Where((_, index) => index is not (4 or 9 or 14)).All(Hex) &&
        value[4] == '-' && value[9] == '-' && value[14] == '-';

    private static bool Token(string? value)
    {
        if (value is not { Length: 43 } || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return false;
        Span<byte> bytes = stackalloc byte[32];
        return Convert.TryFromBase64String(value.Replace('-', '+').Replace('_', '/') + "=", bytes, out var written) && written == 32 &&
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') == value;
    }

    private static bool CertificateBytes(string? value)
    {
        if (value is null || value.Length is 0 || value.Length > 4 * ((AgentProtocol.MaxCertificateDerBytes + 2) / 3)) return false;
        Span<byte> bytes = stackalloc byte[AgentProtocol.MaxCertificateDerBytes];
        // Structural bound only: the future TLS/profile boundary must validate the actual X.509 certificate.
        return Convert.TryFromBase64String(value, bytes, out var written) && written > 0 &&
            Convert.ToBase64String(bytes[..written]) == value;
    }

    private sealed class CanonicalGuidConverter : JsonConverter<Guid>
    {
        public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException();
            var text = reader.GetString();
            if (!Guid.TryParseExact(text, "D", out var value) || !Id(value) || value.ToString("D") != text) throw new JsonException();
            return value;
        }

        public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString("D"));
    }

    private sealed class DecimalInt64Converter : JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException();
            var text = reader.GetString();
            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
                value.ToString(CultureInfo.InvariantCulture) != text) throw new JsonException();
            return value;
        }

        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }

    private sealed class ExactEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException();
            var text = reader.GetString();
            if (!Enum.TryParse<T>(text, out var value) || !Enum.IsDefined(value) || value.ToString() != text) throw new JsonException();
            return value;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            if (!Enum.IsDefined(value)) throw new JsonException();
            writer.WriteStringValue(value.ToString());
        }
    }
}
