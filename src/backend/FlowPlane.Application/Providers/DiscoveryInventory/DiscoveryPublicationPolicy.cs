namespace FlowPlane.Application.Providers.DiscoveryInventory;

public static class DiscoveryPublicationPolicy
{
    public static bool CanPublish(ProviderDiscoveryScanResult summary, long durableObservations, long durableCandidates) =>
        summary.ScanCompleted && summary.SourceCompatibility == ProviderDiscoverySourceCompatibility.Compatible
        && summary.ObservationsDelivered >= 0 && summary.ExecutableCandidates >= 0
        && summary.ExecutableCandidates <= summary.ObservationsDelivered
        && summary.IncompatibleDefinitions >= 0 && summary.IncompatibleDefinitions <= summary.ObservationsDelivered
        && summary.ObservationsDelivered == durableObservations && summary.ExecutableCandidates == durableCandidates
        && summary.StartedAtUtc != default && summary.FinishedAtUtc >= summary.StartedAtUtc
        && ((summary.Outcome == ProviderDiscoveryOutcome.Succeeded && summary.IncompatibleDefinitions == 0)
            || (summary.Outcome == ProviderDiscoveryOutcome.Partial && summary.IncompatibleDefinitions > 0));
}
