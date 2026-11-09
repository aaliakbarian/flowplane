using System.Text;
using System.Text.Json;

using FlowPlane.Agent.Protocol;

namespace FlowPlane.Agent.Tests;

public sealed class AgentProtocolCodecTests
{
    private const string ClaimJson = """
        {"schema":"flowplane.agent/pairing-claim.v1","claimId":"11111111-1111-4111-8111-111111111111",
        "grantId":"22222222-2222-4222-8222-222222222222","pairingCode":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
        "publicCertificateDerBase64":"AQID"}
        """;
    private const string PollJson = """
        {"schema":"flowplane.agent/poll.v1","sessionId":"33333333-3333-4333-8333-333333333333",
        "fence":"9223372036854775806","sequence":"1","requestId":"44444444-4444-4444-8444-444444444444",
        "availability":"Available","readiness":"ReadyForControlPlane","unresolvedEvidenceCount":0}
        """;

    [Fact]
    public void Approved_claim_decodes_without_normalizing_its_identity()
    {
        var request = AgentProtocolCodec.DecodePairingClaim(Encoding.UTF8.GetBytes(ClaimJson));
        Assert.Equal(Guid.Parse("11111111-1111-4111-8111-111111111111"), request.ClaimId);
        Assert.Equal("AQID", request.PublicCertificateDerBase64);
    }

    [Theory]
    [InlineData("pairing-claim.v1", "pairing-claim.v2")]
    [InlineData("\"schema\":", "\"Schema\":")]
    [InlineData("\"claimId\":", "\"arbitraryCommand\":")]
    [InlineData("\"grantId\":", "\"grantId\":null,\"grantId\":")]
    [InlineData("22222222-2222-4222-8222-222222222222", "00000000-0000-0000-0000-000000000000")]
    [InlineData("11111111-1111-4111-8111-111111111111", "{11111111-1111-4111-8111-111111111111}")]
    [InlineData("AQID", "not base64")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "short")]
    public void Unknown_schema_member_duplicate_or_invalid_identity_is_rejected(string original, string replacement) =>
        Assert.Throws<AgentProtocolException>(() => AgentProtocolCodec.DecodePairingClaim(
            Encoding.UTF8.GetBytes(ClaimJson.Replace(original, replacement, StringComparison.Ordinal))));

    [Theory]
    [InlineData("22222222-2222-4222-8222-222222222222")]
    [InlineData("55555555-5555-4555-8555-555555555555")]
    public void Duplicate_top_level_properties_are_rejected_even_when_both_values_are_valid(string secondValue)
    {
        const string original = "\"grantId\":\"22222222-2222-4222-8222-222222222222\"";
        var replacement = $"\"grantId\":\"{secondValue}\"";
        Assert.Equal(Guid.Parse(secondValue), AgentProtocolCodec.DecodePairingClaim(
            Encoding.UTF8.GetBytes(ClaimJson.Replace(original, replacement, StringComparison.Ordinal))).GrantId);
        var duplicate = ClaimJson.Replace(original, $"{original},{replacement}", StringComparison.Ordinal);
        Assert.Throws<AgentProtocolException>(() => AgentProtocolCodec.DecodePairingClaim(Encoding.UTF8.GetBytes(duplicate)));
    }

    [Theory]
    [InlineData("fence", "1", "1")]
    [InlineData("fence", "1", "2")]
    [InlineData("payloadDigest", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("payloadDigest", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    public void Duplicate_nested_operation_properties_are_rejected_even_when_both_values_are_valid(
        string property, string firstValue, string secondValue)
    {
        var key = new AgentOperationKey(Guid.NewGuid(), Guid.NewGuid(), 1, 1, new string('a', 64));
        var receipt = new AgentOperationAcknowledgement("flowplane.agent/operation-acknowledgement.v1", key,
            AgentAcknowledgementPhase.AgentAccepted, Guid.NewGuid(), DateTimeOffset.Parse("2026-11-07T12:00:00Z"));
        var json = Encoding.UTF8.GetString(AgentProtocolCodec.Encode(receipt));
        var original = $"\"{property}\":\"{firstValue}\"";
        var replacement = $"\"{property}\":\"{secondValue}\"";
        Assert.Contains(original, json, StringComparison.Ordinal);
        var validReplacement = AgentProtocolCodec.Decode<AgentOperationAcknowledgement>(
            Encoding.UTF8.GetBytes(json.Replace(original, replacement, StringComparison.Ordinal)));
        if (property == "fence")
            Assert.Equal(long.Parse(secondValue, System.Globalization.CultureInfo.InvariantCulture), validReplacement.Operation.Fence);
        else
            Assert.Equal(secondValue, validReplacement.Operation.PayloadDigest);
        var duplicate = json.Replace(original, $"{original},{replacement}", StringComparison.Ordinal);
        Assert.Throws<AgentProtocolException>(() => AgentProtocolCodec.Decode<AgentOperationAcknowledgement>(Encoding.UTF8.GetBytes(duplicate)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{broken")]
    public void Missing_or_malformed_messages_fail_closed(string json) =>
        Assert.Throws<AgentProtocolException>(() => AgentProtocolCodec.DecodePairingClaim(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void Oversize_and_deep_messages_are_rejected()
    {
        Assert.Throws<AgentProtocolException>(() => AgentProtocolCodec.DecodePairingClaim(new byte[65_537]));
        var deep = ClaimJson.Replace("\"AQID\"", "[[[[[[[[[0]]]]]]]]]", StringComparison.Ordinal);
        Assert.Throws<AgentProtocolException>(() => AgentProtocolCodec.DecodePairingClaim(Encoding.UTF8.GetBytes(deep)));
        var oversizedDer = Convert.ToBase64String(new byte[16_385]);
        Assert.Throws<AgentProtocolException>(() => AgentProtocolCodec.DecodePairingClaim(
            Encoding.UTF8.GetBytes(ClaimJson.Replace("AQID", oversizedDer, StringComparison.Ordinal))));
        var maximumDer = Convert.ToBase64String(new byte[16_384]);
        Assert.Equal(maximumDer, AgentProtocolCodec.DecodePairingClaim(
            Encoding.UTF8.GetBytes(ClaimJson.Replace("AQID", maximumDer, StringComparison.Ordinal))).PublicCertificateDerBase64);
    }

    [Fact]
    public void Int64_fence_roundtrips_losslessly_as_a_decimal_string()
    {
        var poll = AgentProtocolCodec.DecodePoll(Encoding.UTF8.GetBytes(PollJson));
        Assert.Equal(9_223_372_036_854_775_806L, poll.Fence);
        using var json = JsonDocument.Parse(AgentProtocolCodec.Encode(poll));
        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("fence").ValueKind);
        Assert.Equal("9223372036854775806", json.RootElement.GetProperty("fence").GetString());
    }

    [Theory]
    [InlineData("9223372036854775808")]
    [InlineData("+1")]
    [InlineData("01")]
    [InlineData("1e3")]
    [InlineData(" 1")]
    [InlineData("-0")]
    [InlineData("0")]
    [InlineData("-1")]
    public void Noncanonical_or_invalid_fences_are_rejected(string value) =>
        Assert.Throws<AgentProtocolException>(() => AgentProtocolCodec.DecodePoll(Encoding.UTF8.GetBytes(
            PollJson.Replace("9223372036854775806", value, StringComparison.Ordinal))));

    [Theory]
    [InlineData("\"9223372036854775806\"", "9223372036854775806")]
    [InlineData("\"Available\"", "0")]
    [InlineData("\"Available\"", "\"UnknownFutureValue\"")]
    [InlineData("\"readiness\":\"ReadyForControlPlane\"", "\"readiness\":null")]
    [InlineData("\"unresolvedEvidenceCount\":0", "\"unresolvedEvidenceCount\":-1")]
    public void Poll_rejects_numeric_int64_unknown_enums_and_invalid_health(string original, string replacement) =>
        Assert.Throws<AgentProtocolException>(() => AgentProtocolCodec.DecodePoll(
            Encoding.UTF8.GetBytes(PollJson.Replace(original, replacement, StringComparison.Ordinal))));

    [Fact]
    public void Pairing_code_and_malicious_input_are_absent_from_diagnostics()
    {
        var claim = AgentProtocolCodec.DecodePairingClaim(Encoding.UTF8.GetBytes(ClaimJson));
        Assert.DoesNotContain(claim.PairingCode, claim.ToString(), StringComparison.Ordinal);
        const string canary = "synthetic-sensitive-canary-not-a-credential";
        var exception = Assert.Throws<AgentProtocolException>(() => AgentProtocolCodec.DecodePairingClaim(
            Encoding.UTF8.GetBytes(ClaimJson.Replace("AQID", canary, StringComparison.Ordinal))));
        Assert.DoesNotContain(canary, exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void Safe_pairing_receipt_has_no_code_or_arbitrary_payload()
    {
        var receipt = new PairingClaimReceipt("flowplane.agent/pairing-claim-receipt.v1", Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), new string('a', 64), new string('A', 43),
            DateTimeOffset.Parse("2026-11-07T12:00:00Z"), PairingStage.Claimed, 1);
        var bytes = AgentProtocolCodec.EncodePairingReceipt(receipt);
        Assert.Equal(receipt, AgentProtocolCodec.Decode<PairingClaimReceipt>(bytes));
        using var json = JsonDocument.Parse(bytes);
        Assert.False(json.RootElement.TryGetProperty("pairingCode", out _));
        Assert.Equal("1", json.RootElement.GetProperty("receiptVersion").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Operation_ordinals_outside_the_four_fixed_steps_are_rejected(int ordinal)
    {
        var key = new AgentOperationKey(Guid.NewGuid(), Guid.NewGuid(), ordinal, 1, new string('a', 64));
        var receipt = new AgentOperationAcknowledgement("flowplane.agent/operation-acknowledgement.v1", key,
            AgentAcknowledgementPhase.AgentAccepted, Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.Throws<AgentProtocolException>(() => AgentProtocolCodec.Encode(receipt));
    }

    [Fact]
    public void Typed_acknowledgement_roundtrips_and_fingerprint_binds_operation_context()
    {
        var key = new AgentOperationKey(Guid.NewGuid(), Guid.NewGuid(), 1, 9_223_372_036_854_775_806L, new string('a', 64));
        var receipt = new AgentOperationAcknowledgement("flowplane.agent/operation-acknowledgement.v1", key,
            AgentAcknowledgementPhase.MayHaveInvoked, Guid.NewGuid(), DateTimeOffset.UtcNow);
        var decoded = AgentProtocolCodec.Decode<AgentOperationAcknowledgement>(AgentProtocolCodec.Encode(receipt));
        Assert.Equal(receipt, decoded);
        Assert.Equal(AgentProtocolCodec.Fingerprint(key), AgentProtocolCodec.Fingerprint(decoded.Operation));
        Assert.NotEqual(AgentProtocolCodec.Fingerprint(key), AgentProtocolCodec.Fingerprint(key with { Fence = 2 }));
    }

    [Fact]
    public void Closed_messages_roundtrip_without_losing_context()
    {
        var time = DateTimeOffset.Parse("2026-11-07T12:00:00Z");
        Roundtrip(new PairingProofRequest("flowplane.agent/pairing-proof.v1", Guid.NewGuid(), Guid.NewGuid(), new string('A', 43)));
        Roundtrip(new PairingStatusReceipt("flowplane.agent/pairing-status.v1", Guid.NewGuid(), Guid.NewGuid(),
            PairingStage.Proven, time, "1234-5678-90ab-cdef", 1));
        Roundtrip(new AgentConnectRequest("flowplane.agent/connect.v1", Guid.NewGuid(), Guid.NewGuid(), "1.0.0", 1, 1, new string('a', 64)));
        Roundtrip(new AgentSessionStatusReceipt("flowplane.agent/session-status.v1", Guid.NewGuid(), 1, time, null, AgentRecoveryMode.None));
        Roundtrip(new AgentSessionReceipt("flowplane.agent/session-receipt.v1", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            1, 1, time, time.AddSeconds(60), AgentRecoveryMode.None));
        Roundtrip(new AgentPollReceipt("flowplane.agent/poll-receipt.v1", Guid.NewGuid(), 1, 1, time, time.AddSeconds(60), 5, AgentWorkKind.None));
    }

    [Fact]
    public void Invalid_outbound_models_cannot_bypass_wire_validation()
    {
        var time = DateTimeOffset.Parse("2026-11-07T12:00:00Z");
        Assert.Throws<AgentProtocolException>(() => AgentProtocolCodec.Encode(new AgentConnectRequest(
            "flowplane.agent/connect.v1", Guid.NewGuid(), Guid.NewGuid(), "arbitrary command", 1, 1, new string('a', 64))));
        Assert.Throws<AgentProtocolException>(() => AgentProtocolCodec.Encode(new AgentConnectRequest(
            "flowplane.agent/connect.v1", Guid.NewGuid(), Guid.NewGuid(), "1.0.0", 2, 1, new string('a', 64))));
        Assert.Throws<AgentProtocolException>(() => AgentProtocolCodec.Encode(new AgentPollReceipt(
            "flowplane.agent/poll-receipt.v1", Guid.NewGuid(), 1, 1, time, time, 5, AgentWorkKind.None)));
        Assert.Throws<AgentProtocolException>(() => AgentProtocolCodec.Encode(new AgentPollReceipt(
            "flowplane.agent/poll-receipt.v1", Guid.NewGuid(), 1, 1, time, time.AddSeconds(60), 5, (AgentWorkKind)999)));
        Assert.Throws<AgentProtocolException>(() => AgentProtocolCodec.Encode(new AgentPollReceipt(
            "flowplane.agent/poll-receipt.v1", Guid.NewGuid(), 1, 1, time, time.AddSeconds(60), 0, AgentWorkKind.None)));
        Assert.Throws<AgentProtocolException>(() => AgentProtocolCodec.Encode(new PairingStatusReceipt(
            "flowplane.agent/pairing-status.v1", Guid.NewGuid(), Guid.NewGuid(), PairingStage.Proven, time, "unbounded phrase", 1)));
        Assert.Throws<AgentProtocolException>(() => AgentProtocolCodec.Encode(new { Shell = "forbidden" }));
    }

    private static void Roundtrip<T>(T value) where T : class =>
        Assert.Equal(value, AgentProtocolCodec.Decode<T>(AgentProtocolCodec.Encode(value)));
}
