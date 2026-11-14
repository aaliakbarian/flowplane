using System.Net;
using FlowPlane.Agent.Protocol;
using FlowPlane.Agent.Runtime.Control;
using FlowPlane.Agent.Runtime.Journal;
using FlowPlane.Agent.Runtime.Setup;

namespace FlowPlane.Agent.Tests;

public sealed class AgentControlHostTests
{
    [Fact]
    public async Task Unpaired_host_stays_local_and_legacy_pair_is_unavailable()
    {
        using var fixture = new AgentControlFixture();
        using var journal = AgentOperationJournal.Open(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(fixture.Path)!, "operations.journal"), fixture.Installation, true);
        fixture.Store.Dispose();
        using var host = new AgentControlHost(fixture.Path, fixture.Installation, fixture.Certificate, journal,
            (_, _) => throw new InvalidOperationException("unpaired-must-not-use-network"));
        Assert.Equal(LocalSetupStatus.ReadyForLocalSetup, (await host.StatusAsync(TestContext.Current.CancellationToken)).Status);
        await host.StepAsync(TestContext.Current.CancellationToken);
        Assert.Equal(LocalSetupStatus.PairingUnavailable, (await host.PairAsync(new(LocalSetupAction.Pair, "https://localhost/", new string('A', 43)), TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Explicit_v2_pair_displays_safe_phrase_and_waits_for_operator_before_polling()
    {
        using var fixture = new AgentControlFixture();
        using var journal = AgentOperationJournal.Open(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(fixture.Path)!, "operations.journal"), fixture.Installation, true);
        fixture.Store.Dispose();
        PairingClaimReceipt? claim = null;
        using var host = new AgentControlHost(fixture.Path, fixture.Installation, fixture.Certificate, journal,
            (_, _) => fixture.Transport(async (request, token) =>
            {
                var input = AgentProtocolCodec.Decode<PairingClaimRequest>(await request.Content!.ReadAsByteArrayAsync(token));
                claim = fixture.Claim(input.GrantId, input.ClaimId);
                return AgentControlFixture.Reply(claim);
            }, (request, _) =>
            {
                Assert.Contains("pairings", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
                return Task.FromResult(AgentControlFixture.Reply(fixture.Status(claim!, PairingStage.Proven)));
            }));
        var receipt = await host.PairAsync(new(LocalSetupAction.Pair, "https://localhost/", new string('A', 43), Guid.NewGuid()), TestContext.Current.CancellationToken);
        Assert.Equal("flowplane.agent/local-setup-receipt.v2", receipt.Schema);
        Assert.Equal(LocalSetupStatus.PairingPending, receipt.Status);
        Assert.Equal(PairingStage.Proven, receipt.PairingStage);
        Assert.NotNull(receipt.VerificationPhrase);
        await host.StepAsync(TestContext.Current.CancellationToken);
        Assert.Equal(LocalSetupStatus.PairingPending, (await host.StatusAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Equal(0, (await journal.ReadRecoverySummaryAsync(TestContext.Current.CancellationToken)).UnresolvedCount);
    }

    [Fact]
    public async Task Rotation_uses_authenticated_current_binding_and_new_key_proof()
    {
        using var fixture = new AgentControlFixture();
        AgentPollingTests.PreparePaired(fixture);
        using var pending = AgentControlFixture.CreateCertificate();
        using var journal = AgentOperationJournal.Open(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(fixture.Path)!, "operations.journal"), fixture.Installation, true);
        fixture.Store.Dispose();
        var provider = Guid.NewGuid();
        PairingClaimReceipt? claim = null;
        using var host = new AgentControlHost(fixture.Path, fixture.Installation, fixture.Certificate, journal, (_, certificate) =>
            fixture.Transport((_, _) => throw new InvalidOperationException(), async (request, token) =>
            {
                if (request.Method == HttpMethod.Get) return AgentControlFixture.Reply(new AgentAssignmentsReceipt("flowplane.agent/assignments.v1", fixture.Agent, [new(provider, 7)]));
                if (certificate == fixture.Certificate)
                {
                    var rotation = AgentProtocolCodec.Decode<AgentRotationRequest>(await request.Content!.ReadAsByteArrayAsync(token));
                    Assert.Equal(provider, rotation.ProviderInstanceId); Assert.Equal(7, rotation.ProviderVersion);
                    claim = fixture.Claim(Guid.NewGuid(), rotation.ClaimId) with { PinSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(pending.RawData)) };
                    return AgentControlFixture.Reply(claim);
                }
                Assert.Equal(pending, certificate);
                return AgentControlFixture.Reply(fixture.Status(claim!, PairingStage.Redeemed));
            }));
        Assert.Equal(PairingStage.Redeemed, (await host.RotateAsync(pending, TestContext.Current.CancellationToken)).Stage);
    }
}
