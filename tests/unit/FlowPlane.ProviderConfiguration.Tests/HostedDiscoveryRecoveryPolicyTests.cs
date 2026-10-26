using FlowPlane.Infrastructure.Persistence.Discovery;

namespace FlowPlane.ProviderConfiguration.Tests;

public sealed class HostedDiscoveryRecoveryPolicyTests
{
    [Theory]
    [InlineData("Staging", 60, "PreserveLive")]
    [InlineData("Preparing", 60, "PreserveLive")]
    [InlineData("Ready", 60, "PreserveLive")]
    [InlineData("Staging", -1, "AbandonExpired")]
    [InlineData("Failed", -1, "ReleaseTerminal")]
    [InlineData("Superseded", 60, "ReleaseTerminal")]
    [InlineData("Published", -1, "ReleaseTerminal")]
    public void Locked_current_row_not_an_earlier_expired_snapshot_controls_recovery(string status, int remaining, string expected)
    {
        var now = DateTimeOffset.UtcNow;
        var row = new DiscoveryGeneration
        {
            Status = Enum.Parse<DiscoveryGenerationStatus>(status),
            ExecutionSlot = 1,
            LeaseExpiresAtUtc = now.AddSeconds(remaining),
            ExpiresAtUtc = now.AddHours(1),
        };
        Assert.Equal(expected, HostedDiscoveryStore.RecoveryAction(row, now).ToString());
    }
}
