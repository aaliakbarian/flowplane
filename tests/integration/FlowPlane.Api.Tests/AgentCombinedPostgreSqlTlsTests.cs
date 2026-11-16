using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using FlowPlane.Agent.Protocol;
using FlowPlane.Api.Agents;

using static FlowPlane.Api.Tests.AgentCombinedFixture;

namespace FlowPlane.Api.Tests;

public sealed class AgentCombinedPostgreSqlTlsTests
{
    [Fact]
    public async Task Enrollment_requires_real_operator_confirmation_and_private_key_possession_before_session_authority()
    {
        await using var fixture = await AgentCombinedFixture.CreateAsync();
        using var anonymous = fixture.Client(browser: true);
        using var unauthenticated = await anonymous.GetAsync("/agents/" + Guid.NewGuid(), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        using var nonOperator = fixture.Client(browser: true, subject: "non-operator");
        using var forbidden = await nonOperator.GetAsync("/agents/" + Guid.NewGuid(), Token);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        var human = fixture.Client(browser: true, subject: "operator");
        var issued = await fixture.IssueAsync(human);
        var cert = fixture.Certificate();
        var claim = new PairingClaimRequest("flowplane.agent/pairing-claim.v1", Guid.NewGuid(), issued.GetProperty("grantId").GetGuid(),
            issued.GetProperty("pairingCode").GetString()!, Convert.ToBase64String(cert.RawData));
        var receipt = await PostAsync<PairingClaimRequest, PairingClaimReceipt>(fixture.Client(), "/agent/v1/pairing-claims", claim);
        using var noKey = fixture.Client();
        using var missing = await SendAsync(noKey, $"/agent/v1/pairings/{claim.ClaimId}/proof",
            new PairingProofRequest("flowplane.agent/pairing-proof.v1", claim.ClaimId, Guid.NewGuid(), receipt.ServerNonce));
        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
        var client = fixture.Client(cert);
        using var pendingSession = await client.GetAsync("/agent/v1/session", Token);
        Assert.Equal(HttpStatusCode.Forbidden, pendingSession.StatusCode);
        var proof = new PairingProofRequest("flowplane.agent/pairing-proof.v1", claim.ClaimId, Guid.NewGuid(), receipt.ServerNonce);
        var proven = await PostAsync<PairingProofRequest, PairingStatusReceipt>(client, $"/agent/v1/pairings/{claim.ClaimId}/proof", proof);
        Assert.Equal(PairingStage.Proven, proven.Stage);
        var replay = await PostAsync<PairingProofRequest, PairingStatusReceipt>(client, $"/agent/v1/pairings/{claim.ClaimId}/proof", proof);
        Assert.Equal(proven, replay);
        using var stillPending = await client.GetAsync("/agent/v1/session", Token);
        Assert.Equal(HttpStatusCode.Forbidden, stillPending.StatusCode);
        var path = $"/agents/{receipt.AgentId}/pairings/{receipt.GrantId}";
        using var status = await human.GetAsync(path, Token);
        using var wrongActor = fixture.Client(browser: true, subject: "other-operator");
        using var other = await wrongActor.GetAsync(path, Token);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        using var confirm = new HttpRequestMessage(HttpMethod.Post, path + "/confirmation")
        { Content = JsonContent.Create(new { verificationPhrase = proven.VerificationPhrase }) };
        confirm.Headers.TryAddWithoutValidation("If-Match", status.Headers.ETag!.Tag);
        using var response = await human.SendAsync(confirm, Token);
        Assert.Equal("Redeemed", (await JsonAsync(response)).GetProperty("stage").GetString());
        Assert.Equal(receipt.AgentId, (await ConnectAsync(client)).AgentId);
    }

    [Fact]
    public async Task Real_database_health_NoWork_and_immutable_connect_poll_receipts_survive_lost_application_acknowledgements()
    {
        await using var fixture = await AgentCombinedFixture.CreateAsync();
        var agent = await fixture.EnrollAsync();
        using var initial = await agent.Client.GetAsync("/agent/v1/session", Token);
        var state = AgentProtocolCodec.Decode<AgentSessionStatusReceipt>(await initial.Content.ReadAsByteArrayAsync(Token));
        var request = new AgentConnectRequest("flowplane.agent/connect.v1", Guid.NewGuid(), Guid.NewGuid(), "0.1.0", 1, state.Fence, AgentPairingSecurity.RecoveryDigest(0, false));
        var first = await ConnectAsync(agent.Client, request);
        var replay = await ConnectAsync(agent.Client, request);
        Assert.Equal(first, replay);
        var poll = Poll(first, 1);
        using (var lost = await SendAsync(agent.Client, "/agent/v1/polls", poll))
            Assert.Equal(HttpStatusCode.OK, lost.StatusCode); // Discard the response without consuming its receipt.
        var recovered = await PostAsync<AgentPollRequest, AgentPollReceipt>(agent.Client, "/agent/v1/polls", poll);
        var repeated = await PostAsync<AgentPollRequest, AgentPollReceipt>(agent.Client, "/agent/v1/polls", poll);
        Assert.Equal(recovered, repeated);
        Assert.Equal(AgentWorkKind.None, recovered.Work);
        using var health = await agent.Human.GetAsync("/agents/" + agent.Id, Token);
        var data = await JsonAsync(health);
        Assert.True(data.GetProperty("authorityCurrent").GetBoolean());
        Assert.Equal("ReadyForControlPlane", data.GetProperty("readiness").GetString());
        Assert.Equal("Unknown", data.GetProperty("executionReadiness").GetString());
        Assert.Equal(recovered.LeaseExpiresAtUtc, data.GetProperty("leaseExpiresAtUtc").GetDateTimeOffset());
        Assert.Equal(recovered.AcknowledgedAtUtc, data.GetProperty("lastHeartbeatAtUtc").GetDateTimeOffset());
        using var changed = await SendAsync(agent.Client, "/agent/v1/polls", poll with { RequestId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        using var reordered = await SendAsync(agent.Client, "/agent/v1/polls", poll with { Sequence = 3, RequestId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.PreconditionFailed, reordered.StatusCode);
        var second = await PostAsync<AgentPollRequest, AgentPollReceipt>(agent.Client, "/agent/v1/polls", Poll(first, 2));
        Assert.Equal(2, second.Sequence);
    }

    [Fact]
    public async Task Rotation_proves_new_key_and_atomically_restricts_old_certificate_to_receipt_recovery()
    {
        await using var fixture = await AgentCombinedFixture.CreateAsync();
        var agent = await fixture.EnrollAsync();
        var next = fixture.Certificate();
        var request = new AgentRotationRequest("flowplane.agent/rotation.v1", fixture.ProviderId, 1, Guid.NewGuid(), Guid.NewGuid(), Convert.ToBase64String(next.RawData));
        var claim = await PostAsync<AgentRotationRequest, PairingClaimReceipt>(agent.Client, "/agent/v1/rotations", request);
        var pending = fixture.Client(next);
        using var denied = await pending.GetAsync("/agent/v1/session", Token);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var proof = new PairingProofRequest("flowplane.agent/pairing-proof.v1", claim.ClaimId, Guid.NewGuid(), claim.ServerNonce);
        var activated = await PostAsync<PairingProofRequest, PairingStatusReceipt>(pending, $"/agent/v1/rotations/{claim.GrantId}/proof", proof);
        Assert.Equal(PairingStage.Redeemed, activated.Stage);
        using var oldSession = await agent.Client.GetAsync("/agent/v1/session", Token);
        Assert.Equal(HttpStatusCode.Forbidden, oldSession.StatusCode);
        using var oldReceipt = await agent.Client.GetAsync($"/agent/v1/rotations/{claim.GrantId}", Token);
        Assert.Equal(HttpStatusCode.OK, oldReceipt.StatusCode);
        Assert.Equal(agent.Id, (await ConnectAsync(pending)).AgentId);
    }

    [Fact]
    public async Task Database_revocation_denies_the_next_protected_request_on_the_same_established_TLS_connection()
    {
        await using var fixture = await AgentCombinedFixture.CreateAsync();
        var agent = await fixture.EnrollAsync();
        await ConnectAsync(agent.Client);
        using var first = await agent.Client.GetAsync("/agent/v1/session", Token);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await first.Content.ReadAsByteArrayAsync(Token);
        var connection = first.Headers.GetValues("X-Test-Connection").Single();
        // Commit through the real store without refreshing the handshake snapshot: proves database-current RPC authorization.
        await fixture.RevokeWithoutPinRefreshAsync(agent.Id);
        using var revoked = await agent.Client.GetAsync("/agent/v1/session", Token);
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        Assert.Equal(connection, revoked.Headers.GetValues("X-Test-Connection").Single());
    }

    [Theory]
    [InlineData("configuration")]
    [InlineData("credential")]
    [InlineData("credential-only")]
    public async Task Provider_configuration_or_managed_credential_change_fences_an_already_connected_agent(string change)
    {
        await using var fixture = await AgentCombinedFixture.CreateAsync();
        var agent = await fixture.EnrollAsync();
        var session = await ConnectAsync(agent.Client);
        if (change == "credential-only") await fixture.ChangeOnlyCredentialVersionAsync();
        else await fixture.ChangeProviderAsync(change == "credential");
        using var denied = await SendAsync(agent.Client, "/agent/v1/polls", Poll(session, 1));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var health = await agent.Human.GetAsync("/agents/" + agent.Id, Token);
        Assert.False((await JsonAsync(health)).GetProperty("authorityCurrent").GetBoolean());
    }

    [Fact]
    public async Task A_different_active_agent_cannot_use_another_agents_session_or_provider_assignment()
    {
        await using var fixture = await AgentCombinedFixture.CreateAsync();
        var agent = await fixture.EnrollAsync();
        var session = await ConnectAsync(agent.Client);
        var otherProvider = await fixture.AddProviderAsync();
        var otherAgent = await fixture.EnrollAsync(otherProvider);
        await ConnectAsync(otherAgent.Client);
        using var forged = await SendAsync(otherAgent.Client, "/agent/v1/polls", Poll(session, 1));
        Assert.Equal(HttpStatusCode.PreconditionFailed, forged.StatusCode);
        var other = fixture.Certificate();
        var wrongProvider = new AgentRotationRequest("flowplane.agent/rotation.v1", otherProvider, 1, Guid.NewGuid(), Guid.NewGuid(), Convert.ToBase64String(other.RawData));
        using var denied = await SendAsync(agent.Client, "/agent/v1/rotations", wrongProvider);
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("same-key-different-DER")]
    public async Task Real_handshake_rejects_unknown_and_mismatched_certificate(string scenario)
    {
        await using var fixture = await AgentCombinedFixture.CreateAsync();
        var agent = await fixture.EnrollAsync();
        using var key = scenario == "unknown" ? RSA.Create(3072) : agent.Certificate.GetRSAPrivateKey()!;
        var request = new CertificateRequest("CN=isolated-certificate-canary", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, true));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var rejected = fixture.Client(cert);
        await Assert.ThrowsAsync<HttpRequestException>(() => rejected.GetAsync("/agent/v1/session", Token));
    }

    [Fact]
    public async Task Real_handshake_rejects_the_exact_previously_activated_certificate_after_its_bounded_expiry()
    {
        await using var fixture = await AgentCombinedFixture.CreateAsync();
        using var key = RSA.Create(3072);
        var request = new CertificateRequest("CN=short-lived-isolated-agent", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, true));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddSeconds(20));
        var agent = await fixture.EnrollAsync(certificate: cert);
        using var valid = await agent.Client.GetAsync("/agent/v1/session", Token);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode); // Exact DER is actually registered and authenticated before expiry.
        await valid.Content.ReadAsByteArrayAsync(Token);
        var remaining = new DateTimeOffset(cert.NotAfter.ToUniversalTime()) - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(200);
        Assert.InRange(remaining, TimeSpan.Zero, TimeSpan.FromSeconds(21));
        await Task.Delay(remaining, Token);
        using var fresh = fixture.Client(cert);
        await Assert.ThrowsAsync<HttpRequestException>(() => fresh.GetAsync("/agent/v1/session", Token));
        using var continuing = await agent.Client.GetAsync("/agent/v1/session", Token);
        Assert.Equal(HttpStatusCode.Forbidden, continuing.StatusCode);
    }

    [Fact]
    public async Task Abrupt_application_process_restart_recovers_durable_session_and_original_poll_receipt_without_reenrollment()
    {
        await using var fixture = await AgentCombinedFixture.CreateAsync();
        var agent = await fixture.EnrollAsync();
        var session = await ConnectAsync(agent.Client);
        var poll = Poll(session, 1);
        var accepted = await PostAsync<AgentPollRequest, AgentPollReceipt>(agent.Client, "/agent/v1/polls", poll);
        var previous = fixture.ProcessId;
        await fixture.RestartAsync();
        Assert.NotEqual(previous, fixture.ProcessId);
        var recoveredClient = fixture.Client(agent.Certificate);
        using var status = await recoveredClient.GetAsync("/agent/v1/session", Token);
        var current = AgentProtocolCodec.Decode<AgentSessionStatusReceipt>(await status.Content.ReadAsByteArrayAsync(Token));
        Assert.Equal(session.AgentId, current.AgentId);
        Assert.Equal(accepted.LeaseExpiresAtUtc, current.LeaseExpiresAtUtc);
        Assert.Equal(accepted, await PostAsync<AgentPollRequest, AgentPollReceipt>(recoveredClient, "/agent/v1/polls", poll));
        Assert.Equal(2, (await PostAsync<AgentPollRequest, AgentPollReceipt>(recoveredClient, "/agent/v1/polls", Poll(session, 2))).Sequence);
    }

    private static AgentPollRequest Poll(AgentSessionReceipt session, long sequence) => new("flowplane.agent/poll.v1",
        session.SessionId, session.Fence, sequence, Guid.NewGuid(), AgentAvailability.Available, AgentReadiness.ReadyForControlPlane, 0);

    [Fact]
    public async Task Expired_session_cannot_renew_and_reconnect_advances_fence_without_reviving_old_authority()
    {
        await using var fixture = await AgentCombinedFixture.CreateAsync();
        var agent = await fixture.EnrollAsync();
        var session = await ConnectAsync(agent.Client);
        // Age this owned fixture's session consistently, avoiding a minute-long wall-clock delay or a fake DB clock.
        await fixture.ExecuteAsync($"UPDATE \"Agents\" SET \"ConnectAcceptedAtUtc\"=\"ConnectAcceptedAtUtc\"-interval '2 minutes', \"ConnectLeaseExpiresAtUtc\"=\"ConnectLeaseExpiresAtUtc\"-interval '2 minutes', \"LeaseExpiresAtUtc\"=\"LeaseExpiresAtUtc\"-interval '2 minutes' WHERE \"Id\"='{agent.Id}'");
        using var expired = await SendAsync(agent.Client, "/agent/v1/polls", Poll(session, 1));
        Assert.Equal(HttpStatusCode.PreconditionFailed, expired.StatusCode);
        var recovered = await ConnectAsync(agent.Client);
        Assert.True(recovered.Fence > session.Fence);
        Assert.NotEqual(session.SessionId, recovered.SessionId);
        using var stale = await SendAsync(agent.Client, "/agent/v1/polls", Poll(session, 1));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(AgentWorkKind.None, (await PostAsync<AgentPollRequest, AgentPollReceipt>(agent.Client, "/agent/v1/polls", Poll(recovered, 1))).Work);
    }

    [Fact]
    public async Task Conflicting_live_incarnation_is_quarantined_and_cannot_gain_polling_authority()
    {
        await using var fixture = await AgentCombinedFixture.CreateAsync();
        var agent = await fixture.EnrollAsync();
        var session = await ConnectAsync(agent.Client);
        var other = new AgentConnectRequest("flowplane.agent/connect.v1", Guid.NewGuid(), Guid.NewGuid(), "0.1.0", 1, session.Fence, AgentPairingSecurity.RecoveryDigest(0, false));
        using var conflict = await SendAsync(agent.Client, "/agent/v1/sessions", other);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var stale = await SendAsync(agent.Client, "/agent/v1/polls", Poll(session, 1));
        Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);
        using var health = await agent.Human.GetAsync("/agents/" + agent.Id, Token);
        Assert.Equal("Quarantined", (await JsonAsync(health)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Operator_revocation_refreshes_real_database_pins_and_rejects_fresh_TLS_connections()
    {
        await using var fixture = await AgentCombinedFixture.CreateAsync();
        var agent = await fixture.EnrollAsync();
        using var health = await agent.Human.GetAsync("/agents/" + agent.Id, Token);
        using var revoke = new HttpRequestMessage(HttpMethod.Post, $"/agents/{agent.Id}/revocation");
        revoke.Headers.TryAddWithoutValidation("If-Match", health.Headers.ETag!.Tag);
        using var result = await agent.Human.SendAsync(revoke, Token);
        Assert.Equal(HttpStatusCode.NoContent, result.StatusCode);
        using var fresh = fixture.Client(agent.Certificate);
        await Assert.ThrowsAsync<HttpRequestException>(() => fresh.GetAsync("/agent/v1/session", Token));
    }

    [Fact]
    public async Task Concurrent_claims_bind_one_exact_certificate_and_loser_cannot_prove_possession_or_substitute_the_winner()
    {
        await using var fixture = await AgentCombinedFixture.CreateAsync();
        var human = fixture.Client(browser: true, subject: "operator");
        var issued = await fixture.IssueAsync(human);
        var certs = new[] { fixture.Certificate(), fixture.Certificate() };
        var claims = certs.Select(cert => new PairingClaimRequest("flowplane.agent/pairing-claim.v1", Guid.NewGuid(), issued.GetProperty("grantId").GetGuid(),
            issued.GetProperty("pairingCode").GetString()!, Convert.ToBase64String(cert.RawData))).ToArray();
        var responses = await Task.WhenAll(claims.Select(claim => SendAsync(fixture.Client(), "/agent/v1/pairing-claims", claim)));
        try
        {
            var winner = Array.FindIndex(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.True(winner >= 0);
            var loser = 1 - winner;
            Assert.Equal(HttpStatusCode.Conflict, responses[loser].StatusCode);
            var receipt = AgentProtocolCodec.Decode<PairingClaimReceipt>(await responses[winner].Content.ReadAsByteArrayAsync(Token));
            using var losingClient = fixture.Client(certs[loser]);
            await Assert.ThrowsAsync<HttpRequestException>(() => losingClient.GetAsync($"/agent/v1/pairings/{claims[winner].ClaimId}", Token));
            var proof = new PairingProofRequest("flowplane.agent/pairing-proof.v1", claims[winner].ClaimId, Guid.NewGuid(), receipt.ServerNonce);
            var proven = await PostAsync<PairingProofRequest, PairingStatusReceipt>(fixture.Client(certs[winner]), $"/agent/v1/pairings/{claims[winner].ClaimId}/proof", proof);
            Assert.Equal(PairingStage.Proven, proven.Stage);
            using var replay = await SendAsync(fixture.Client(), "/agent/v1/pairing-claims", claims[winner]);
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            var original = AgentProtocolCodec.Decode<PairingClaimReceipt>(await replay.Content.ReadAsByteArrayAsync(Token));
            Assert.True(receipt == original, "Claim replay must recover immutable original facts.");
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }
}
