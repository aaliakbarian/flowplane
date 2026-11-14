namespace FlowPlane.Agent.Runtime.Security;

// Preserve F2's public entry point while sharing the exact profile with the API.
public static class AgentCertificateProfile
{
    public static bool IsValid(ReadOnlyMemory<byte> der, DateTimeOffset now) =>
        Protocol.AgentCertificateValidation.IsValid(der, now);
}
