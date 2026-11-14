using FlowPlane.Agent.Runtime.Control;

namespace FlowPlane.Agent.Tests;

public sealed class AgentControlStateTests
{
    [Fact]
    public void Partial_replacement_and_corrupt_expected_marker_require_review_and_disposed_owner_cannot_write()
    {
        var directory = Directory.CreateTempSubdirectory("flowplane-control-");
        try
        {
            var path = Path.Combine(directory.FullName, "control.state");
            var installation = Guid.NewGuid();
            var store = AgentControlStore.Open(path, installation, true);
            var state = store.State;
            store.Dispose();
            Assert.Throws<AgentControlException>(() => store.Save(state));
            File.WriteAllBytes(path + ".next", [1]);
            Assert.Throws<AgentControlException>(() => AgentControlStore.Open(path, installation));
            File.Delete(path + ".next");
            File.WriteAllBytes(path + ".expected", []);
            Assert.Throws<AgentControlException>(() => AgentControlStore.Open(path, installation));
        }
        finally { directory.Delete(true); }
    }
    [Fact]
    public void Reopen_preserves_request_ids_and_rejects_a_different_installation()
    {
        var directory = Directory.CreateTempSubdirectory("flowplane-control-");
        try
        {
            var path = Path.Combine(directory.FullName, "control.state");
            var installation = Guid.NewGuid();
            var grant = Guid.NewGuid();
            var claim = Guid.NewGuid();
            using (var store = AgentControlStore.Open(path, installation, true))
            {
                store.Save(store.State with { Origin = "https://localhost:8443/", Enrollment = new(grant, claim, Guid.NewGuid(), new string('a', 64), null, null) });
                Assert.Throws<AgentControlException>(() => AgentControlStore.Open(path, installation));
            }
            using (var reopened = AgentControlStore.Open(path, installation))
            {
                Assert.Equal(claim, reopened.State.Enrollment!.ClaimId);
                Assert.Equal(grant, reopened.State.Enrollment.GrantId);
            }
            Assert.Throws<AgentControlException>(() => AgentControlStore.Open(path, Guid.NewGuid()));
            Assert.DoesNotContain("pairingCode", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public void Corruption_missing_expected_state_and_failed_replacement_never_reset_identity()
    {
        var directory = Directory.CreateTempSubdirectory("flowplane-control-");
        try
        {
            var path = Path.Combine(directory.FullName, "control.state");
            var installation = Guid.NewGuid();
            using (var store = AgentControlStore.Open(path, installation, true))
            {
                Directory.CreateDirectory(path + ".next");
                Assert.Throws<AgentControlException>(() => store.Save(store.State with { Origin = "https://localhost/" }));
                Assert.Null(store.State.Origin);
                Directory.Delete(path + ".next");
            }
            File.WriteAllBytes(path, [1, 2, 3]);
            Assert.Throws<AgentControlException>(() => AgentControlStore.Open(path, installation));
            Assert.Throws<AgentControlException>(() => AgentControlStore.Open(path, installation, true));
            File.Delete(path);
            Assert.Throws<AgentControlException>(() => AgentControlStore.Open(path, installation, true));
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData("missing-claim")]
    [InlineData("empty-rotation-proof")]
    public void Missing_cross_record_evidence_is_rejected_before_replacement(string defect)
    {
        using var fixture = new AgentControlFixture();
        AgentPollingTests.PreparePaired(fixture);
        var state = fixture.Store.State;
        var invalid = defect == "missing-claim"
            ? state with { Enrollment = state.Enrollment! with { ClaimReceipt = null } }
            : state with { Rotation = new(new("flowplane.agent/rotation.v1", Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), Convert.ToBase64String(fixture.Certificate.RawData)), Guid.Empty, state.Enrollment!.PinSha256, null, null) };
        Assert.Throws<AgentControlException>(() => fixture.Store.Save(invalid));
        Assert.Equal(state, fixture.Store.State);
    }
}
