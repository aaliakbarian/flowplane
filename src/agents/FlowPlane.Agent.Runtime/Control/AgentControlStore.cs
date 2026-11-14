using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlowPlane.Agent.Protocol;
using FlowPlane.Agent.Runtime.Transport;

namespace FlowPlane.Agent.Runtime.Control;

/// <summary>Exclusive bounded snapshot. Failure poisons this owner; reopening never resets evidence.</summary>
public sealed class AgentControlStore : IDisposable
{
    private const int MaxBytes = 65_536;
    private const string Schema = "flowplane.agent/control-state.v1";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 16,
        Converters = { new ProtocolConverterFactory() },
    };
    private readonly string path;
    private readonly FileStream ownership;
    private bool failed;
    private bool disposed;
    public AgentControlState State { get; private set; }
    private AgentControlStore(string path, FileStream ownership, AgentControlState state)
    { this.path = path; this.ownership = ownership; State = state; }

    public static AgentControlStore Open(string path, Guid installationId, bool explicitInitialize = false)
    {
        FileStream? ownership = null;
        try
        {
            if (installationId == Guid.Empty || !Path.IsPathFullyQualified(path)) throw new AgentControlException();
            RejectLinks(path); RejectLinks(path + ".lock"); RejectLinks(path + ".expected"); RejectLinks(path + ".next");
            ownership = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (File.Exists(path + ".next")) throw new AgentControlException();
            if (explicitInitialize)
            {
                if (File.Exists(path) || File.Exists(path + ".expected")) throw new AgentControlException();
                using var marker = new FileStream(path + ".expected", FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough);
                marker.WriteByte(1); marker.Flush(true);
                var store = new AgentControlStore(path, ownership, new(Schema, installationId, null, null, null, null, null, null, null));
                store.Save(store.State);
                return store;
            }
            if (!File.Exists(path + ".expected")) throw new AgentControlException();
            using (var marker = new FileStream(path + ".expected", FileMode.Open, FileAccess.Read, FileShare.Read))
                if (marker.Length != 1 || marker.ReadByte() != 1) throw new AgentControlException();
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is < 37 or > MaxBytes + 36) throw new AgentControlException();
            var bytes = new byte[(int)input.Length];
            input.ReadExactly(bytes);
            if (BinaryPrimitives.ReadInt32LittleEndian(bytes) != bytes.Length - 36 ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes.AsSpan(36)), bytes.AsSpan(4, 32))) throw new AgentControlException();
            var state = JsonSerializer.Deserialize<AgentControlState>(bytes.AsSpan(36), Options) ?? throw new AgentControlException();
            Validate(state, installationId);
            return new(path, ownership, state);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or AgentControlException or AgentProtocolException or AgentTransportException)
        { ownership?.Dispose(); throw new AgentControlException(); }
    }

    public void Save(AgentControlState state)
    {
        try
        {
            if (failed || disposed) throw new AgentControlException();
            Validate(state, State.InstallationId);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state, Options);
            if (bytes.Length > MaxBytes) throw new AgentControlException();
            RejectLinks(path); RejectLinks(path + ".next");
            using (var output = new FileStream(path + ".next", FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                Span<byte> header = stackalloc byte[36];
                BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
                SHA256.HashData(bytes, header[4..]);
                output.Write(header); output.Write(bytes); output.Flush(true);
            }
            File.Move(path + ".next", path, true);
            State = state;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or AgentProtocolException or AgentControlException or AgentTransportException)
        { failed = true; throw new AgentControlException(); }
    }

    private static void Validate(AgentControlState state, Guid installation)
    {
        if (state.Schema != Schema || state.InstallationId != installation || installation == Guid.Empty) throw new AgentControlException();
        if (state.Origin is not null) _ = AgentHttpsTransport.ValidateOrigin(state.Origin);
        if (state.Enrollment is { } enrollment)
        {
            if (state.Origin is null || enrollment.GrantId == Guid.Empty || enrollment.ClaimId == Guid.Empty || enrollment.ProofRequestId == Guid.Empty ||
                enrollment.PinSha256 is not { Length: 64 } || enrollment.PinSha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) throw new AgentControlException();
            if (enrollment.ClaimReceipt is { } claim && (claim.GrantId != enrollment.GrantId || claim.ClaimId != enrollment.ClaimId || claim.PinSha256 != enrollment.PinSha256)) throw new AgentControlException();
            if (enrollment.StatusReceipt is { } status)
            {
                if (enrollment.ClaimReceipt is null) throw new AgentControlException();
                AgentReceiptChecks.Status(status, enrollment.ClaimReceipt, null);
            }
        }
        if (state.Replacement is { } replacement)
        {
            if (replacement.GrantId == Guid.Empty || replacement.GrantId == replacement.PreviousEnrollment.GrantId ||
                replacement.PinSha256 is not { Length: 64 } || replacement.PinSha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
                replacement.PinSha256 == replacement.PreviousEnrollment.PinSha256 || replacement.PreviousEnrollment.StatusReceipt?.Stage == PairingStage.Redeemed ||
                state.Enrollment is null ||
                state.Enrollment != replacement.PreviousEnrollment && state.Enrollment.StatusReceipt?.Stage != PairingStage.Redeemed &&
                    (state.Enrollment.GrantId != replacement.GrantId || state.Enrollment.PinSha256 != replacement.PinSha256) ||
                replacement.PreviousEnrollment.ClaimReceipt is { } oldClaim && state.Enrollment.ClaimReceipt is { } newClaim && oldClaim.AgentId != newClaim.AgentId)
                throw new AgentControlException();
            Validate(state with { Enrollment = replacement.PreviousEnrollment, Replacement = null, Rotation = null, ConnectRequest = null, ConnectReceipt = null, PendingPoll = null, LatestPoll = null }, installation);
        }
        if (state.ConnectRequest is not null && state.Enrollment?.StatusReceipt?.Stage != PairingStage.Redeemed) throw new AgentControlException();
        if (state.ConnectReceipt is { } receipt && (state.ConnectRequest is not { } connect || receipt.BootId != connect.BootId ||
            connect.ExpectedFence == long.MaxValue || receipt.Fence != connect.ExpectedFence + 1 || receipt.AgentId != state.Enrollment?.ClaimReceipt?.AgentId)) throw new AgentControlException();
        if (state.PendingPoll is { } poll && (state.ConnectReceipt is not { } session || poll.SessionId != session.SessionId || poll.Fence != session.Fence)) throw new AgentControlException();
        if (state.LatestPoll is { } latest && (state.ConnectReceipt is not { } session2 || latest.SessionId != session2.SessionId || latest.Fence != session2.Fence)) throw new AgentControlException();
        if (state.Rotation is { } rotation)
        {
            if (state.Enrollment?.StatusReceipt?.Stage != PairingStage.Redeemed || rotation.ProofRequestId == Guid.Empty) throw new AgentControlException();
            _ = AgentProtocolCodec.Encode(rotation.Request);
            var pin = Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(rotation.Request.PublicCertificateDerBase64)));
            if (pin != rotation.PinSha256) throw new AgentControlException();
            if (rotation.ClaimReceipt is { } claim)
            {
                AgentReceiptChecks.Claim(claim, claim.GrantId, rotation.Request.ClaimId, pin);
                if (claim.AgentId != state.Enrollment.ClaimReceipt!.AgentId) throw new AgentControlException();
            }
            if (rotation.StatusReceipt is { } status)
            {
                if (rotation.ClaimReceipt is null) throw new AgentControlException();
                AgentReceiptChecks.Status(status, rotation.ClaimReceipt, null);
            }
        }
    }

    private static void RejectLinks(string path)
    {
        for (var current = new FileInfo(path) as FileSystemInfo; current is not null; current = Directory.GetParent(current.FullName))
            if (current.LinkTarget is not null || current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new AgentControlException();
    }
    public void Dispose() { disposed = true; ownership.Dispose(); }

    private sealed class ProtocolConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type type) => type.Namespace == typeof(AgentConnectRequest).Namespace;
        public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) => (JsonConverter)Activator.CreateInstance(typeof(ProtocolConverter<>).MakeGenericType(type))!;
    }
    private sealed class ProtocolConverter<T> : JsonConverter<T> where T : class
    {
        public override T? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return AgentProtocolCodec.Decode<T>(System.Text.Encoding.UTF8.GetBytes(document.RootElement.GetRawText()));
        }
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteRawValue(AgentProtocolCodec.Encode(value));
    }
}
