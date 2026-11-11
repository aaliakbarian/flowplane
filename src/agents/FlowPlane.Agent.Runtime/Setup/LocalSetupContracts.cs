using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowPlane.Agent.Runtime.Setup;

public enum LocalSetupAction { Initialize, Status, Pair }
public enum LocalSetupStatus { Uninitialized, ReadyForLocalSetup, ActionRequired, PairingUnavailable, InvalidRequest, Denied }
public sealed class LocalSetupRequest(LocalSetupAction action, string? backendAddress = null, string? pairingCode = null)
{
    public LocalSetupAction Action { get; } = action;
    public string? BackendAddress { get; } = backendAddress;
    public string? PairingCode { get; } = pairingCode;
    public override string ToString() => "LocalSetupRequest(redacted)";
}
public sealed record LocalSetupReceipt(string Schema, LocalSetupStatus Status);
public sealed class LocalSetupException() : Exception("The local setup request is invalid or unavailable.");
public interface ILocalSetupChannel
{
    ValueTask<LocalSetupReceipt> PairAsync(LocalSetupRequest request, CancellationToken cancellationToken);
}
public static class LocalSetupCodec
{
    private sealed record WireRequest(string Schema, string Action, string? BackendAddress, string? PairingCode);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 4,
    };

    public static LocalSetupRequest Decode(ReadOnlySpan<byte> json)
    {
        if (json.IsEmpty || json.Length > 4096) throw new LocalSetupException();
        try
        {
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
        var json = JsonSerializer.SerializeToUtf8Bytes(new WireRequest("flowplane.agent/local-setup.v1", request.Action.ToString(), request.BackendAddress, request.PairingCode), Options);
        if (json.Length > 4096) throw new LocalSetupException();
        return json;
    }

    private static void Validate(LocalSetupRequest request)
    {
        if (request is null || !Enum.IsDefined(request.Action)) throw new LocalSetupException();
        if (request.Action != LocalSetupAction.Pair)
        {
            if (request.BackendAddress is not null || request.PairingCode is not null) throw new LocalSetupException();
            return;
        }
        if (request.BackendAddress is not { Length: <= 2048 } || !Uri.TryCreate(request.BackendAddress, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/" ||
            request.PairingCode is not { Length: 43 } code || code.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new LocalSetupException();
        Span<byte> bytes = stackalloc byte[32];
        if (!Convert.TryFromBase64String(code.Replace('-', '+').Replace('_', '/') + "=", bytes, out var written) || written != 32 ||
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') != code) throw new LocalSetupException();
    }
}
