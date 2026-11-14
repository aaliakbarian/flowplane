using FlowPlane.Agent.Protocol;

namespace FlowPlane.Agent.Tests;

public sealed class AgentRotationProtocolTests
{
    [Fact]
    public void Assignment_context_is_bounded_exact_and_lossless()
    {
        var receipt = new AgentAssignmentsReceipt("flowplane.agent/assignments.v1", Guid.NewGuid(), [new(Guid.NewGuid(), long.MaxValue)]);
        var bytes = AgentProtocolCodec.Encode(receipt);
        Assert.Contains("\"9223372036854775807\"", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.Equal(receipt.Providers[0], AgentProtocolCodec.Decode<AgentAssignmentsReceipt>(bytes).Providers[0]);
        Assert.Throws<AgentProtocolException>(() => AgentProtocolCodec.Encode(receipt with { Providers = [] }));
        Assert.Throws<AgentProtocolException>(() => AgentProtocolCodec.Encode(receipt with { Providers = [receipt.Providers[0], receipt.Providers[0]] }));
    }
    [Fact]
    public void Rotation_context_is_strict_versioned_and_lossless()
    {
        var request = new AgentRotationRequest("flowplane.agent/rotation.v1", Guid.NewGuid(), long.MaxValue,
            Guid.NewGuid(), Guid.NewGuid(), Convert.ToBase64String(new byte[] { 1, 2, 3 }));
        var bytes = AgentProtocolCodec.Encode(request);
        Assert.Contains("\"9223372036854775807\"", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.Equal(request, AgentProtocolCodec.Decode<AgentRotationRequest>(bytes));
        Assert.Throws<AgentProtocolException>(() => AgentProtocolCodec.Encode(request with { ProviderVersion = 0 }));
        Assert.Throws<AgentProtocolException>(() => AgentProtocolCodec.Encode(request with { Schema = "future" }));
    }
}
