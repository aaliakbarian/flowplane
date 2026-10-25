using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace FlowPlane.Providers.Ssis.Configuration;

public sealed record SsisAllowedEndpoint(string Host, int Port);

/// <summary>Exact operator-owned destinations, not a DNS-rebinding-resistant network firewall.</summary>
public sealed partial class SsisEndpointPolicy
{
    private readonly SsisAllowedEndpoint[] endpoints;

    public SsisEndpointPolicy(IEnumerable<SsisAllowedEndpoint> endpoints)
    {
        this.endpoints = endpoints.ToArray();
        if (this.endpoints.Length > 100 || this.endpoints.Any(endpoint => endpoint is null ||
                !ValidHost(endpoint.Host) || endpoint.Port is < 1 or > 65535))
            throw new InvalidOperationException("FlowPlane:Ssis:AllowedEndpoints contains an invalid host/port pair.");
    }

    public bool Allows(SsisProviderConfiguration configuration) => ValidHost(configuration.Host) &&
        endpoints.Any(endpoint => endpoint.Port == configuration.Port &&
            string.Equals(endpoint.Host, configuration.Host, StringComparison.Ordinal));

    private static bool ValidHost(string? host)
    {
        if (host is null || host.Length is < 1 or > 253 || !DnsName().IsMatch(host)) return false;
        if (IPAddress.TryParse(host, out var address))
            return address.AddressFamily == AddressFamily.InterNetwork && address.ToString() == host;
        return Uri.CheckHostName(host) == UriHostNameType.Dns;
    }

    [GeneratedRegex(@"\A[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex DnsName();
}
