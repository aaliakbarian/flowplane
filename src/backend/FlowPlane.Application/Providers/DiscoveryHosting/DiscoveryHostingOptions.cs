namespace FlowPlane.Application.Providers.DiscoveryHosting;

public sealed record DiscoveryHostingOptions
{
    public const string SectionName = "FlowPlane:DiscoveryHosting";
    public bool Enabled { get; init; }
    public int LeaseSeconds { get; init; } = 60;
    public int RenewalSeconds { get; init; } = 15;
    public int PollSeconds { get; init; } = 2;

    public void Validate()
    {
        if (LeaseSeconds is < 15 or > 300 || RenewalSeconds < 1 || RenewalSeconds > LeaseSeconds / 3 ||
            PollSeconds < 1 || PollSeconds > LeaseSeconds / 3)
            throw new InvalidOperationException("DiscoveryHosting requires bounded leases and renewal/poll intervals at most one third of the lease.");
    }
}
