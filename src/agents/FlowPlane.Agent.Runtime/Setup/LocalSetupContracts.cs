using System.Text.Json;
using System.Text.Json.Serialization;
using FlowPlane.Agent.Protocol;

namespace FlowPlane.Agent.Runtime.Setup;

public enum LocalSetupAction { Initialize, Status, Pair }
public enum LocalSetupStatus { Uninitialized, ReadyForLocalSetup, ActionRequired, PairingUnavailable, InvalidRequest, Denied, PairingPending, Paired }
public sealed class LocalSetupRequest(LocalSetupAction action, string? backendAddress = null, string? pairingCode = null, Guid? grantId = null)
{
    public LocalSetupAction Action { get; } = action;
    public string? BackendAddress { get; } = backendAddress;
    public string? PairingCode { get; private set; } = pairingCode;
    public Guid? GrantId { get; } = grantId;
    internal void ClearPairingCode() => PairingCode = null;
    public override string ToString() => "LocalSetupRequest(redacted)";
}
public sealed record LocalSetupReceipt(string Schema, LocalSetupStatus Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? VerificationPhrase = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PairingStage? PairingStage = null);
public sealed class LocalSetupException() : Exception("The local setup request is invalid or unavailable.");
public interface ILocalSetupChannel
{
    ValueTask<LocalSetupReceipt> PairAsync(LocalSetupRequest request, CancellationToken cancellationToken);
}
public static class LocalSetupCodec
{
    public static byte[] EncodeReceipt(LocalSetupReceipt receipt)
    {
        ValidateReceipt(receipt);
        return JsonSerializer.SerializeToUtf8Bytes(receipt, ReceiptOptions);
    }
    public static LocalSetupReceipt DecodeReceipt(ReadOnlySpan<byte> json)
    {
        if (json.Length is < 1 or > 4096) throw new LocalSetupException();
        try
        {
            var receipt = JsonSerializer.Deserialize<LocalSetupReceipt>(json, ReceiptOptions) ?? throw new LocalSetupException();
            ValidateReceipt(receipt);
            return receipt;
        }
        catch (JsonException) { throw new LocalSetupException(); }
    }

    private static void ValidateReceipt(LocalSetupReceipt receipt)
    {
        if (!Enum.IsDefined(receipt.Status)) throw new LocalSetupException();
        if (receipt.Schema == "flowplane.agent/local-setup-receipt.v1")
        {
            if (receipt.Status > LocalSetupStatus.Denied || receipt.VerificationPhrase is not null || receipt.PairingStage is not null) throw new LocalSetupException();
            return;
        }
        if (receipt.Schema != "flowplane.agent/local-setup-receipt.v2" ||
            receipt.PairingStage is { } stage && !Enum.IsDefined(stage) ||
            receipt.VerificationPhrase is { } phrase && (phrase.Length != 19 || phrase.Where((_, index) => index is not (4 or 9 or 14)).Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) || phrase[4] != '-' || phrase[9] != '-' || phrase[14] != '-') ||
            (receipt.PairingStage is null) != (receipt.VerificationPhrase is null) ||
            receipt.Status == LocalSetupStatus.Paired && receipt.PairingStage != PairingStage.Redeemed) throw new LocalSetupException();
    }
    private sealed record WireRequest(string Schema, string Action, string? BackendAddress, string? PairingCode)
    { public override string ToString() => "LocalSetupWireRequest(redacted)"; }
    private sealed record PairingWireRequest(string Schema, string Action, string BackendAddress, string PairingCode, string GrantId)
    { public override string ToString() => "LocalSetupPairingWireRequest(redacted)"; }
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 4,
    };
    private static readonly JsonSerializerOptions ReceiptOptions = new(Options) { PropertyNamingPolicy = null };

    public static LocalSetupRequest Decode(ReadOnlySpan<byte> json)
    {
        if (json.IsEmpty || json.Length > 4096) throw new LocalSetupException();
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 4 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new LocalSetupException();
            string? schema = null;
            while (reader.Read())
            {
                if (reader.CurrentDepth != 1 || reader.TokenType != JsonTokenType.PropertyName || !reader.ValueTextEquals("schema"u8)) continue;
                if (!reader.Read() || reader.TokenType != JsonTokenType.String) throw new LocalSetupException();
                schema = reader.GetString(); break;
            }
            if (schema == "flowplane.agent/local-setup.v2")
            {
                var pairing = JsonSerializer.Deserialize<PairingWireRequest>(json, Options) ?? throw new LocalSetupException();
                if (pairing.Action != "Pair" || !Guid.TryParseExact(pairing.GrantId, "D", out var grant) || grant.ToString("D") != pairing.GrantId) throw new LocalSetupException();
                var input = new LocalSetupRequest(LocalSetupAction.Pair, pairing.BackendAddress, pairing.PairingCode, grant);
                Validate(input);
                return input;
            }
            var wire = JsonSerializer.Deserialize<WireRequest>(json, Options);
            if (wire is null || wire.Schema != "flowplane.agent/local-setup.v1") throw new LocalSetupException();
            var action = wire.Action switch
            {
                "Initialize" => LocalSetupAction.Initialize,
                "Status" => LocalSetupAction.Status,
                "Pair" => LocalSetupAction.Pair,
                _ => throw new LocalSetupException()
            };
            var request = new LocalSetupRequest(action, wire.BackendAddress, wire.PairingCode);
            Validate(request);
            return request;
        }
        catch (JsonException) { throw new LocalSetupException(); }
    }

    public static byte[] Encode(LocalSetupRequest request)
    {
        Validate(request);
        var json = request.GrantId is { } grant
            ? JsonSerializer.SerializeToUtf8Bytes(new PairingWireRequest("flowplane.agent/local-setup.v2", "Pair", request.BackendAddress!, request.PairingCode!, grant.ToString("D")), Options)
            : JsonSerializer.SerializeToUtf8Bytes(new WireRequest("flowplane.agent/local-setup.v1", request.Action.ToString(), request.BackendAddress, request.PairingCode), Options);
        if (json.Length > 4096) throw new LocalSetupException();
        return json;
    }

    private static void Validate(LocalSetupRequest request)
    {
        if (request is null || !Enum.IsDefined(request.Action)) throw new LocalSetupException();
        if (request.Action != LocalSetupAction.Pair)
        {
            if (request.BackendAddress is not null || request.PairingCode is not null || request.GrantId is not null) throw new LocalSetupException();
            return;
        }
        if (request.GrantId == Guid.Empty || request.BackendAddress is not { Length: <= 2048 } || !Uri.TryCreate(request.BackendAddress, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/" ||
            request.PairingCode is not { Length: 43 } code || code.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new LocalSetupException();
        Span<byte> bytes = stackalloc byte[32];
        if (!Convert.TryFromBase64String(code.Replace('-', '+').Replace('_', '/') + "=", bytes, out var written) || written != 32 ||
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') != code) throw new LocalSetupException();
    }
}
