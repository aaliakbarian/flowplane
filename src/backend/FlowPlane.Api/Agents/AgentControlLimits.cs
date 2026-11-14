using System.Threading.RateLimiting;

namespace FlowPlane.Api.Agents;

internal sealed class AgentControlLimits : IDisposable
{
    private readonly TokenBucketRateLimiter enrollment = new(new()
    {
        TokenLimit = 10,
        TokensPerPeriod = 10,
        ReplenishmentPeriod = TimeSpan.FromMinutes(1),
        AutoReplenishment = true,
        QueueLimit = 0,
    });
    private readonly ConcurrencyLimiter requests = new(new() { PermitLimit = 64, QueueLimit = 0 });
    internal bool TryEnrollment()
    {
        using var lease = enrollment.AttemptAcquire();
        return lease.IsAcquired;
    }
    internal RateLimitLease AcquireRequest() => requests.AttemptAcquire();
    public void Dispose() { enrollment.Dispose(); requests.Dispose(); }
}
