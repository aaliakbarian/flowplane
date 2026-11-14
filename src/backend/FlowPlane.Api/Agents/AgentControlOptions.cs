namespace FlowPlane.Api.Agents;

internal sealed class AgentControlOptions
{
    public const string SectionName = "FlowPlane:AgentControl";
    public bool Enabled { get; init; }
    public int BrowserPort { get; init; } = 8080;
    public int AgentPort { get; init; } = 8443;
    public bool BrowserHttps { get; init; }
    public string? ServerCertificatePath { get; init; }
    public void Validate()
    {
        if (!Enabled) return;
        if (BrowserPort is < 1 or > 65535 || AgentPort is < 1 or > 65535 || BrowserPort == AgentPort ||
            string.IsNullOrWhiteSpace(ServerCertificatePath) || !Path.IsPathFullyQualified(ServerCertificatePath))
            throw new InvalidOperationException("Agent control requires separate ports and an explicit server TLS certificate.");
    }
}
