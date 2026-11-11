using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;

using FlowPlane.Agent.Windows;
using FlowPlane.Agent.Windows.Security;

// No environment-variable/command-line configuration providers or console execution mode.
if (args.Length != 0 || !WindowsServiceHelpers.IsWindowsService()) return 2;
try
{
    new ServiceTokenVerifier().Verify();
    WindowsAgentSecurity.VerifyBinaryDirectory(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], DisableDefaults = true });
    builder.Logging.ClearProviders();
    builder.Services.AddWindowsService(options => options.ServiceName = WindowsAgentSecurity.ServiceName);
    builder.Services.AddHostedService<AgentWorker>();
    await builder.Build().RunAsync();
    return 0;
}
catch (Exception) { return 1; } // Do not emit raw Windows/CNG/IPC exception details.
