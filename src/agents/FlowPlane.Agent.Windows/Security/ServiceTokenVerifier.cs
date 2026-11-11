using System.Security.Principal;

namespace FlowPlane.Agent.Windows.Security;

public interface IServiceTokenVerifier { void Verify(); }

public sealed class ServiceTokenVerifier : IServiceTokenVerifier
{
    public void Verify()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsAuthenticated || identity.IsSystem || identity.User != WindowsAgentSecurity.ServiceSid ||
            new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new AgentWindowsException();
    }
}
