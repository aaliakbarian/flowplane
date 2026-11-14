using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using FlowPlane.Agent.Protocol;
using FlowPlane.Application.Agents;

namespace FlowPlane.Api.Agents;

internal static class AgentHttp
{
    internal static readonly JsonSerializerOptions HumanJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = AgentProtocol.MaxDepth,
        AllowDuplicateProperties = false,
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString,
    };

    internal static IResult Problem(int status, string code) => Results.Problem(statusCode: status,
        title: "The agent operation could not be completed safely.", extensions: new Dictionary<string, object?> { ["diagnosticCode"] = code });
    internal static int Status(AgentStoreError code) => code switch
    {
        AgentStoreError.InvalidRequest => 400,
        AgentStoreError.InvalidCredential or AgentStoreError.Revoked => 403,
        AgentStoreError.NotFound => 404,
        AgentStoreError.ContextChanged => 412,
        AgentStoreError.Capacity => 429,
        AgentStoreError.Unavailable or AgentStoreError.CommitUncertain => 503,
        _ => 409,
    };

    internal static async Task<byte[]> ReadBody(HttpContext context)
    {
        if (context.Request.ContentLength > AgentProtocol.MaxPayloadBytes) throw new AgentProtocolException();
        if (context.Request.ContentType?.Split(';')[0] != "application/json") throw new AgentProtocolException();
        using var stream = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) != 0)
        {
            if (stream.Length + count > AgentProtocol.MaxPayloadBytes) throw new AgentProtocolException();
            stream.Write(buffer, 0, count);
        }
        return stream.ToArray();
    }

    internal static IResult Wire<T>(T value) where T : class => Results.Bytes(AgentProtocolCodec.Encode(value), "application/json");
    internal static string Version(long version) => version.ToString(CultureInfo.InvariantCulture);
    internal static void ETag(HttpContext context, long version) => context.Response.Headers.ETag = '"' + Version(version) + '"';
    internal static long IfMatch(HttpContext context)
    {
        var raw = context.Request.Headers.IfMatch.ToString();
        if (raw.Length == 0) throw new AgentPreconditionException(428);
        if (raw.Length < 3 || raw[0] != '"' || raw[^1] != '"' ||
            !long.TryParse(raw.AsSpan(1, raw.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var version) ||
            version <= 0 || raw != '"' + Version(version) + '"') throw new AgentPreconditionException(400);
        return version;
    }
}

internal sealed class AgentPreconditionException(int status) : Exception("The agent precondition is invalid.")
{ internal int Status { get; } = status; }
