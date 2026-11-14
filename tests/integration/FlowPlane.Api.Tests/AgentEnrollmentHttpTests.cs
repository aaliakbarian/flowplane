using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

using FlowPlane.Agent.Protocol;
using FlowPlane.Agent.Runtime.Control;
using FlowPlane.Agent.Runtime.Journal;
using FlowPlane.Agent.Runtime.Setup;
using FlowPlane.Agent.Runtime.Transport;
using FlowPlane.Api.Agents;
using FlowPlane.Application.Agents;
using FlowPlane.Application.Providers;
using FlowPlane.Domain.Providers;
using FlowPlane.Providers.Ssis.Configuration;

using Microsoft.Extensions.DependencyInjection;

namespace FlowPlane.Api.Tests;

// Real human HTTP/auth/serialization boundary; only durable storage is doubled.
// HTTPS TestServer requests are not evidence of a real TLS handshake.
public sealed class AgentEnrollmentHttpTests
{
    private const long ReceiptVersion = 9007199254740993;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Claimed_or_proven_explicit_HTTP_reissue_is_consumable_with_fresh_helper_key_and_operator_confirmation(bool proven)
    {
        var directory = Directory.CreateTempSubdirectory("flowplane-reissue-seam-");
        using var oldKey = RSA.Create(3072);
        using var freshKey = RSA.Create(3072);
        using var original = AgentBoundaryTests.Certificate(oldKey, "previous-enrollment");
        using var fresh = AgentBoundaryTests.Certificate(freshKey, "replacement-enrollment");
        try
        {
            var boundary = new Boundary { Certificate = original.RawData };
            boundary.Prove(proven);
            var oldReceipt = boundary.Receipt;
            var oldClaim = AgentControlService.ClaimReceipt(oldReceipt with { State = AgentGrantState.Claimed }, original.RawData);
            var installation = Guid.NewGuid();
            var path = Path.Combine(directory.FullName, "control.state");
            using (var state = AgentControlStore.Open(path, installation, true))
                state.Save(state.State with
                {
                    Origin = "https://localhost/",
                    Enrollment = new(oldClaim.GrantId, oldClaim.ClaimId, Guid.NewGuid(), oldClaim.PinSha256, oldClaim,
                    proven ? AgentControlService.StatusReceipt(oldReceipt, original.RawData) : null)
                });
            using var journal = AgentOperationJournal.Open(Path.Combine(directory.FullName, "operations.journal"), installation, true);
            using var api = Factory(boundary);
            using var client = Client(api);
            using var issue = await client.PostAsJsonAsync("/agents/pairing-grants", new
            {
                providerInstanceId = boundary.Instance.Id,
                providerVersion = "1",
                clientRequestId = Guid.NewGuid(),
                displayName = "Replacement",
                existingAgentId = oldReceipt.AgentId,
                expectedAgentVersion = ReceiptVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, issue.StatusCode);
            using var issued = JsonDocument.Parse(await issue.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var grant = issued.RootElement.GetProperty("grantId").GetGuid();
            Assert.NotEqual(oldClaim.GrantId, grant);
            Assert.Equal(oldReceipt.AgentId, issued.RootElement.GetProperty("agentId").GetGuid());
            var stages = 0;
            using var scope = api.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<AgentControlService>();
            using var host = new AgentControlHost(path, installation, original, journal, (origin, certificate) =>
                new AgentHttpsTransport(origin, new WireHandler(async (request, token) => Wire(await service.ClaimAsync(AgentProtocolCodec.Decode<PairingClaimRequest>(await request.Content!.ReadAsByteArrayAsync(token)), token))),
                    new WireHandler(async (request, token) =>
                    {
                        Assert.Equal(fresh.RawData, certificate.RawData);
                        var authority = new AgentCredentialAuthority(new(boundary.Receipt.AgentId, boundary.Receipt.CredentialId!.Value, certificate.RawData),
                            AgentLifecycleState.Pending, AgentCredentialState.PendingProof, 1, 1, 0, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), null, true, false, DateTimeOffset.UtcNow);
                        if (request.Method == HttpMethod.Post)
                        {
                            var input = AgentProtocolCodec.Decode<PairingProofRequest>(await request.Content!.ReadAsByteArrayAsync(token));
                            return Wire(await service.ProveAsync(input.ClaimId, input, authority, token));
                        }
                        return Wire(AgentControlService.StatusReceipt((await service.PairingAsync(boundary.Receipt.ClaimId!.Value, authority, token)).Receipt, certificate.RawData));
                    })), _ =>
                    {
                        stages++;
                        using var publicCertificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(fresh.RawData);
                        return publicCertificate.CopyWithPrivateKey(freshKey);
                    });
            var pending = await host.PairAsync(new(LocalSetupAction.Pair, "https://localhost/", issued.RootElement.GetProperty("pairingCode").GetString(), grant), TestContext.Current.CancellationToken);
            Assert.Equal(LocalSetupStatus.PairingPending, pending.Status);
            Assert.Equal(PairingStage.Proven, pending.PairingStage);
            Assert.Equal(1, stages);
            Assert.Equal(0, boundary.Confirmations);
            Assert.Equal(oldReceipt.AgentId, host.State!.Enrollment!.ClaimReceipt!.AgentId);
            Assert.Equal(oldClaim, host.State.Replacement!.PreviousEnrollment.ClaimReceipt);
            using var status = await client.GetAsync(boundary.StatusPath, TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(await status.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(pending.VerificationPhrase, document.RootElement.GetProperty("verificationPhrase").GetString());
            using var confirmation = Confirmation(boundary, pending.VerificationPhrase!, status.Headers.ETag!.Tag);
            using var confirmed = await client.SendAsync(confirmation, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
            await host.StepAsync(TestContext.Current.CancellationToken);
            Assert.Equal(LocalSetupStatus.Paired, (await host.StatusAsync(TestContext.Current.CancellationToken)).Status);
            Assert.Equal(1, boundary.Confirmations);
            Assert.Equal(0, (await journal.ReadRecoverySummaryAsync(TestContext.Current.CancellationToken)).UnresolvedCount);
        }
        finally { directory.Delete(true); }
    }

    private static HttpResponseMessage Wire<T>(T receipt) where T : class
    { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(AgentProtocolCodec.Encode(receipt)) }; response.Content.Headers.ContentType = new("application/json"); return response; }
    private sealed class WireHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }

    [Fact]
    public async Task Rotation_replay_crosses_actual_BeginRotation_committed_Claim_not_committed_service_seam()
    {
        var boundary = new Boundary { FailFirstRotationClaim = true };
        using var api = Factory(boundary);
        using var scope = api.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<AgentControlService>();
        using var key = RSA.Create(3072);
        using var pending = AgentBoundaryTests.Certificate(key, "pending-rotation");
        var authority = new AgentCredentialAuthority(new(boundary.Receipt.AgentId, Guid.NewGuid(), new byte[] { 1 }), AgentLifecycleState.Active,
            AgentCredentialState.Active, 1, 1, 0, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), null, true, false, DateTimeOffset.UtcNow);
        var request = new AgentRotationRequest("flowplane.agent/rotation.v1", boundary.Instance.Id, 1, Guid.NewGuid(), Guid.NewGuid(), Convert.ToBase64String(pending.RawData));
        await Assert.ThrowsAsync<IOException>(() => service.RotateAsync(request, authority, TestContext.Current.CancellationToken));
        Assert.Equal(AgentGrantState.Issued, boundary.Receipt.State);
        Assert.Null(boundary.Receipt.CredentialId);
        var committedGrant = boundary.Receipt.GrantId;
        var claim = await service.RotateAsync(request, authority, TestContext.Current.CancellationToken);
        Assert.Equal(committedGrant, claim.GrantId);
        Assert.Equal(request.ClaimId, claim.ClaimId);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(pending.RawData)), claim.PinSha256);
        Assert.Equal(2, boundary.RotationRequests.Count);
        Assert.Equal(boundary.RotationRequests[0], boundary.RotationRequests[1]);
        Assert.Equal(request.RequestId, boundary.RotationRequests[0].ClientRequestId);
        Assert.True(scope.ServiceProvider.GetRequiredService<AgentPinRegistry>().Accepts(pending, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Explicit_issue_returns_code_once_and_sends_only_its_hash_to_storage()
    {
        var boundary = new Boundary();
        using var api = Factory(boundary);
        using var client = Client(api);
        using var first = await Issue(client, boundary);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal('"' + ReceiptVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) + '"', first.Headers.ETag?.Tag);
        Assert.True(first.Headers.CacheControl?.NoStore);
        var text = await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var firstJson = JsonDocument.Parse(text);
        var code = firstJson.RootElement.GetProperty("pairingCode").GetString()!;
        Assert.Equal(32, AgentPairingSecurity.DecodeToken(code).Length);
        Assert.Equal(SHA256.HashData(AgentPairingSecurity.DecodeToken(code)), boundary.PersistedHash);
        Assert.Equal(new AgentActor(ProviderApiFactory.Issuer, "operator"), boundary.Issues[0].Actor);
        Assert.Equal(boundary.RequestId, boundary.Issues[0].ClientRequestId);
        Assert.Equal(1, boundary.Issues[0].Provider.ProviderVersion);
        Assert.Equal(JsonValueKind.String, firstJson.RootElement.GetProperty("version").ValueKind);
        Assert.Equal("9007199254740993", firstJson.RootElement.GetProperty("version").GetString());
        Assert.Equal(new[] { "agentId", "expiresAtUtc", "grantId", "pairingCode", "providerInstanceId", "stage", "version" },
            firstJson.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(Boundary.SecretLocator, text, StringComparison.Ordinal);

        using var replay = await Issue(client, boundary);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var replayJson = JsonDocument.Parse(await replay.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(JsonValueKind.Null, replayJson.RootElement.GetProperty("pairingCode").ValueKind);
        Assert.Equal(firstJson.RootElement.GetProperty("grantId").GetGuid(), replayJson.RootElement.GetProperty("grantId").GetGuid());
        Assert.Equal(SHA256.HashData(AgentPairingSecurity.DecodeToken(code)), boundary.PersistedHash);

        using var status = await client.GetAsync(boundary.StatusPath, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var statusText = await status.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var statusJson = JsonDocument.Parse(statusText);
        Assert.False(statusJson.RootElement.TryGetProperty("pairingCode", out _));
        Assert.Equal(JsonValueKind.Null, statusJson.RootElement.GetProperty("verificationPhrase").ValueKind);
        Assert.DoesNotContain(code, statusText, StringComparison.Ordinal);
        Assert.DoesNotContain(code, api.Logs.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(boundary.PersistedHash!), statusText, StringComparison.Ordinal);
        Assert.DoesNotContain(Boundary.SecretLocator, api.Logs.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Proven_phrase_requires_explicit_current_ETag_confirmation_and_returns_only_safe_status()
    {
        var boundary = new Boundary();
        boundary.Prove();
        using var api = Factory(boundary);
        using var client = Client(api);
        using var status = await client.GetAsync(boundary.StatusPath, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        using var document = JsonDocument.Parse(await status.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var phrase = document.RootElement.GetProperty("verificationPhrase").GetString();
        Assert.False(string.IsNullOrEmpty(phrase));
        Assert.Equal(0, boundary.Confirmations);
        using var confirmation = Confirmation(boundary, phrase!, status.Headers.ETag!.Tag);
        using var response = await client.SendAsync(confirmation, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, boundary.Confirmations);
        Assert.Equal(new AgentActor(ProviderApiFactory.Issuer, "operator"), boundary.Confirmation?.Actor);
        Assert.Equal(ReceiptVersion, boundary.Confirmation?.ExpectedGrantVersion);
        Assert.Equal(boundary.Receipt.Nonce, boundary.Confirmation?.Nonce.ToArray());
        Assert.Equal("\"9007199254740994\"", response.Headers.ETag?.Tag);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var safe = JsonDocument.Parse(body);
        Assert.Equal("Redeemed", safe.RootElement.GetProperty("stage").GetString());
        Assert.Equal(new[] { "agentId", "expiresAtUtc", "grantId", "providerInstanceId", "stage", "verificationPhrase", "version" },
            safe.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(Convert.ToBase64String(boundary.Certificate), body, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(boundary.Receipt.Nonce!), body, StringComparison.Ordinal);
        Assert.DoesNotContain(Boundary.SecretLocator, body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, 428)]
    [InlineData("W/\"9007199254740993\"", 400)]
    [InlineData("\"09007199254740993\"", 400)]
    [InlineData("\"9007199254740992\"", 412)]
    public async Task Confirmation_requires_a_strong_canonical_current_ETag(string? etag, int expected)
    {
        var boundary = new Boundary();
        boundary.Prove();
        using var api = Factory(boundary);
        using var client = Client(api);
        using var request = Confirmation(boundary, boundary.Phrase, etag);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal((HttpStatusCode)expected, response.StatusCode);
        Assert.Equal(0, boundary.Confirmations);
        Assert.Equal(AgentGrantState.Proven, boundary.Receipt.State);
    }

    [Fact]
    public async Task Mismatched_phrase_cannot_activate_the_agent()
    {
        var boundary = new Boundary();
        boundary.Prove();
        using var api = Factory(boundary);
        using var client = Client(api);
        using var request = Confirmation(boundary, "synthetic-wrong-phrase", "\"9007199254740993\"");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, boundary.Confirmations);
        Assert.Equal(AgentGrantState.Proven, boundary.Receipt.State);
    }

    [Theory]
    [InlineData("other-operator", false)]
    [InlineData("operator", true)]
    public async Task Grant_status_and_confirmation_are_bound_to_original_actor_and_agent(string subject, bool otherAgent)
    {
        var boundary = new Boundary();
        boundary.Prove();
        using var api = Factory(boundary);
        using var client = Client(api, subject);
        var path = otherAgent ? $"/agents/{Guid.NewGuid():D}/pairings/{boundary.Receipt.GrantId:D}" : boundary.StatusPath;
        using var read = await client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, path + "/confirmation")
        { Content = JsonContent.Create(new { verificationPhrase = boundary.Phrase }) };
        request.Headers.TryAddWithoutValidation("If-Match", "\"9007199254740993\"");
        using var confirm = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, confirm.StatusCode);
        Assert.Equal(0, boundary.Confirmations);
        Assert.DoesNotContain(boundary.Phrase, await read.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, 401)]
    [InlineData("non-operator", 403)]
    public async Task JWT_operator_policy_precedes_enabled_pairing_storage(string? subject, int status)
    {
        var boundary = new Boundary();
        using var api = Factory(boundary);
        using var client = Client(api, subject);
        using var response = await Issue(client, boundary);
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Empty(boundary.Issues);
    }

    [Fact]
    public async Task HTTP_cannot_issue_a_code_even_with_forged_forwarded_TLS_headers()
    {
        var boundary = new Boundary();
        using var api = Factory(boundary);
        using var client = Client(api);
        client.BaseAddress = new("http://localhost");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-Port", "8443");
        using var response = await Issue(client, boundary);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("AgentHttpsRequired", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Empty(boundary.Issues);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("wrong-case")]
    [InlineData("private-key")]
    [InlineData("too-large")]
    public async Task Strict_issue_JSON_rejects_extra_duplicate_misnamed_private_key_and_oversized_input(string scenario)
    {
        var boundary = new Boundary();
        using var api = Factory(boundary);
        using var client = Client(api);
        var body = JsonSerializer.Serialize(IssueBody(boundary));
        body = scenario switch
        {
            "unknown" => body[..^1] + ",\"actor\":{\"issuer\":\"forged\",\"subject\":\"operator\"}}",
            "duplicate" => body[..^1] + ",\"providerVersion\":\"1\"}",
            "wrong-case" => body.Replace("providerInstanceId", "ProviderInstanceId", StringComparison.Ordinal),
            "private-key" => body[..^1] + ",\"privateKey\":\"SYNTHETIC_PRIVATE_KEY_NEVER_ACCEPTED\"}",
            "too-large" => body[..^1] + ",\"padding\":\"" + new string('x', AgentProtocol.MaxPayloadBytes) + "\"}",
            _ => throw new InvalidOperationException(),
        };
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/agents/pairing-grants", content, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(boundary.Issues);
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_KEY_NEVER_ACCEPTED", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_KEY_NEVER_ACCEPTED", api.Logs.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("disabled", 404)]
    [InlineData("blocked", 403)]
    [InlineData("changed", 412)]
    public async Task Current_provider_policy_fences_issue_and_confirmation_before_storage(string scenario, int expected)
    {
        var boundary = new Boundary(host: scenario == "blocked" ? "blocked.example.test" : "sql.example.test", enabled: scenario != "disabled");
        boundary.Prove();
        if (scenario == "changed") boundary.Instance.MarkCredentialChanged(1, DateTimeOffset.UtcNow);
        using var api = Factory(boundary);
        using var client = Client(api);
        using var issue = await Issue(client, boundary);
        Assert.Equal((HttpStatusCode)expected, issue.StatusCode);
        using var confirm = Confirmation(boundary, boundary.Phrase, "\"9007199254740993\"");
        using var response = await client.SendAsync(confirm, TestContext.Current.CancellationToken);
        Assert.Equal((HttpStatusCode)expected, response.StatusCode);
        Assert.Empty(boundary.Issues);
        Assert.Equal(0, boundary.Confirmations);
    }

    [Theory]
    [InlineData(false, "sql.example.test")]
    [InlineData(true, "blocked.example.test")]
    public async Task Revocation_remains_available_after_provider_disable_or_endpoint_policy_denial(bool enabled, string host)
    {
        var boundary = new Boundary(host, enabled);
        using var api = Factory(boundary);
        using var client = Client(api);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/agents/{boundary.Receipt.AgentId:D}/revocation");
        request.Headers.TryAddWithoutValidation("If-Match", "\"9007199254740993\"");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(boundary.Receipt.AgentId, boundary.Revocation?.AgentId);
        Assert.Equal(new AgentActor(ProviderApiFactory.Issuer, "operator"), boundary.Revocation?.Actor);
    }

    private static ProviderApiFactory Factory(Boundary boundary) => new(settings: new Dictionary<string, string?>
    {
        ["FlowPlane:Operators:Allowed:1:Issuer"] = ProviderApiFactory.Issuer,
        ["FlowPlane:Operators:Allowed:1:Subject"] = "other-operator",
    }, configureServices: services =>
    {
        // AddAgentControl has already registered disabled options; override only runtime gate.
        // No TLS file loading, listener setup or hosted pin loader is enabled by this test.
        services.AddSingleton(new AgentControlOptions { Enabled = true });
        services.AddSingleton<IAgentControlStore>(boundary);
        services.AddSingleton<IAgentControlReader>(boundary);
        services.AddSingleton<IProviderInstanceStore>(boundary);
    });
    private static HttpClient Client(ProviderApiFactory api, string? subject = "operator")
    { var client = api.Client(subject); client.BaseAddress = new("https://localhost"); return client; }
    private static object IssueBody(Boundary boundary) => new { providerInstanceId = boundary.Instance.Id, providerVersion = "1", clientRequestId = boundary.RequestId, displayName = "Synthetic agent" };
    private static Task<HttpResponseMessage> Issue(HttpClient client, Boundary boundary) => client.PostAsJsonAsync("/agents/pairing-grants", IssueBody(boundary), TestContext.Current.CancellationToken);
    private static HttpRequestMessage Confirmation(Boundary boundary, string phrase, string? etag)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, boundary.StatusPath + "/confirmation") { Content = JsonContent.Create(new { verificationPhrase = phrase }) };
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        return request;
    }

    private sealed class Boundary(string host = "sql.example.test", bool enabled = true) : IAgentControlStore, IAgentControlReader, IProviderInstanceStore
    {
        internal const string SecretLocator = "SYNTHETIC_AGENT_SQL_LOCATOR_NEVER_RETURNED";
        internal Guid RequestId { get; } = Guid.NewGuid();
        internal ProviderInstance Instance { get; } = ProviderInstance.Create(Guid.NewGuid(), SsisProviderConfigurationCodec.SsisProviderType,
            "Synthetic provider", enabled, new SsisProviderConfigurationCodec().Encode(new(host, 1433, "synthetic", false)), SecretReference.Environment(SecretLocator), DateTimeOffset.UtcNow);
        internal List<IssueAgentGrant> Issues { get; } = [];
        internal byte[]? PersistedHash { get; private set; }
        internal byte[] Certificate { get; set; } = Encoding.UTF8.GetBytes("SYNTHETIC_PUBLIC_CERTIFICATE_BYTES");
        private byte[]? tombstonedDer;
        internal AgentGrantReceipt Receipt { get; private set; } = new(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, AgentGrantState.Issued,
            ReceiptVersion, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(10), null, null, null, null);
        internal string StatusPath => $"/agents/{Receipt.AgentId:D}/pairings/{Receipt.GrantId:D}";
        internal string Phrase => AgentPairingSecurity.VerificationPhrase(Receipt.GrantId, Receipt.ClaimId!.Value,
            Convert.ToHexStringLower(SHA256.HashData(Certificate)), AgentPairingSecurity.EncodeToken(Receipt.Nonce!));
        internal int Confirmations { get; private set; }
        internal ConfirmAgentGrant? Confirmation { get; private set; }
        internal RevokeAgent? Revocation { get; private set; }
        internal bool FailFirstRotationClaim { get; init; }
        internal List<RotateAgentCredential> RotationRequests { get; } = [];
        private byte[]? rotationDer;
        internal void Prove(bool proven = true) => Receipt = Receipt with { ProviderInstanceId = Instance.Id, State = proven ? AgentGrantState.Proven : AgentGrantState.Claimed, ClaimId = Guid.NewGuid(), CredentialId = Guid.NewGuid(), Nonce = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray() };
        public Task<AgentGrantReceipt> IssueGrantAsync(IssueAgentGrant request, CancellationToken token = default)
        {
            Issues.Add(request);
            if (request.ExistingAgentId is { } agent)
            {
                Assert.Equal(Receipt.AgentId, agent);
                Assert.Equal(ReceiptVersion, request.ExpectedAgentVersion);
                tombstonedDer = Certificate;
                Receipt = Receipt with { GrantId = Guid.NewGuid(), State = AgentGrantState.Issued, ClaimId = null, CredentialId = null, Nonce = null };
            }
            PersistedHash ??= request.TokenHash.ToArray();
            Receipt = Receipt with { ProviderInstanceId = Instance.Id };
            return Task.FromResult(Receipt);
        }
        public Task<bool> MatchesIssuedCodeAsync(Guid grantId, ReadOnlyMemory<byte> tokenHash, CancellationToken token = default) => Task.FromResult(PersistedHash is not null && tokenHash.Span.SequenceEqual(PersistedHash));
        public Task<AgentGrantView?> ReadGrantAsync(Guid grantId, CancellationToken token = default) => Task.FromResult(grantId == Receipt.GrantId ? new AgentGrantView(Receipt, new(ProviderApiFactory.Issuer, "operator"), null, Receipt.ClaimId is null ? null : Certificate, Instance.Id, 1) : null);
        public Task<AgentGrantReceipt> ConfirmAsync(ConfirmAgentGrant request, CancellationToken token = default)
        {
            if (request.ExpectedGrantVersion != Receipt.Version) throw new AgentStoreException(AgentStoreError.ContextChanged);
            Confirmation = request; Confirmations++;
            Receipt = Receipt with { State = AgentGrantState.Redeemed, Version = Receipt.Version + 1 };
            return Task.FromResult(Receipt);
        }
        public Task RevokeAsync(RevokeAgent request, CancellationToken token = default) { Revocation = request; return Task.CompletedTask; }
        public Task<IReadOnlyList<AgentPublicPin>> ReadPinsAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<AgentPublicPin>>(rotationDer is null ? [] : [new(Receipt.AgentId, Receipt.CredentialId!.Value, rotationDer)]);
        public Task<AgentHealthView?> ReadHealthAsync(Guid agentId, CancellationToken token = default) => Task.FromResult<AgentHealthView?>(agentId == Receipt.AgentId ? new(agentId, "Synthetic agent", AgentLifecycleState.Active,
            ReceiptVersion, 1, 1, null, null, FlowPlane.Application.Agents.AgentReadiness.Unknown, null, null, null, DateTimeOffset.UtcNow, false, false, [new(Instance.Id, 1)]) : null);
        public Task<ProviderInstance?> FindAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(id == Instance.Id ? Instance : null);
        public Task<AgentCredentialAuthority?> ReadCredentialAsync(ReadOnlyMemory<byte> exactCertificateDer, CancellationToken token = default) => throw new NotSupportedException();
        public Task<AgentGrantView?> FindClaimAsync(Guid claimId, Guid credentialId, CancellationToken token = default)
            => Task.FromResult<AgentGrantView?>(Receipt.ClaimId == claimId && Receipt.CredentialId == credentialId ? new(Receipt, new(ProviderApiFactory.Issuer, "operator"), null, Certificate, Instance.Id, 1) : null);
        public Task<AgentGrantReceipt> ClaimAsync(ClaimAgentGrant request, CancellationToken token = default)
        {
            if (FailFirstRotationClaim && RotationRequests.Count == 1) throw new IOException("synthetic-failure-after-BeginRotation-commit");
            if (tombstonedDer is not null && request.CertificateDer.Span.SequenceEqual(tombstonedDer)) throw new AgentStoreException(AgentStoreError.PinCollision);
            if (tombstonedDer is not null) Assert.Equal(PersistedHash, request.TokenHash.ToArray());
            rotationDer = request.CertificateDer.ToArray();
            Certificate = rotationDer;
            Receipt = Receipt with { State = AgentGrantState.Claimed, ClaimId = request.ClaimId, CredentialId = Guid.NewGuid(), Nonce = new byte[32] };
            return Task.FromResult(Receipt);
        }
        public Task<AgentGrantReceipt> RecordProofAsync(ProveAgentGrant request, CancellationToken token = default)
        { Assert.Equal(Receipt.Nonce, request.Nonce.ToArray()); Receipt = Receipt with { State = AgentGrantState.Proven }; return Task.FromResult(Receipt); }
        public Task<AgentConnectOutcome> ConnectAsync(ConnectAgent request, CancellationToken token = default) => throw new NotSupportedException();
        public Task<AgentPollOutcome> PollAsync(PollAgent request, CancellationToken token = default) => throw new NotSupportedException();
        public Task<AgentGrantReceipt> BeginRotationAsync(RotateAgentCredential request, CancellationToken token = default)
        {
            RotationRequests.Add(request);
            Receipt = Receipt with { ProviderInstanceId = Instance.Id };
            return Task.FromResult(Receipt);
        }
        public Task<AgentGrantReceipt> ActivateRotationAsync(ProveAgentGrant request, CancellationToken token = default) => throw new NotSupportedException();
        public Task<AgentSessionStatus?> ReadSessionAsync(AgentCertificateContext certificate, CancellationToken token = default) => throw new NotSupportedException();
        public Task<AgentGrantReceipt> ReadGrantAsync(Guid grantId, AgentCertificateContext certificate, CancellationToken token = default) => throw new NotSupportedException();
        public Task<int> CleanupAsync(Guid agentId, CancellationToken token = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProviderInstance>> ListAsync(int offset, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProviderInstance> UpdateAsync(Guid id, long expectedVersion, string displayName, bool isEnabled, ProviderConfigurationDocument configuration, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProviderInstance> CreateWithManagedSecretAsync(Guid id, ProviderType providerType, string displayName, bool isEnabled, ProviderConfigurationDocument configuration, ReadOnlyMemory<char> secret, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReplaceManagedSecretAsync(Guid providerInstanceId, long expectedVersion, ReadOnlyMemory<char> secret, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid providerInstanceId, long expectedVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
