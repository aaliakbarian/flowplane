using System.Text;

using FlowPlane.Agent.Runtime.Setup;
using FlowPlane.Agent.Setup;
using FlowPlane.Agent.Windows.Security;
using FlowPlane.Agent.Windows.Setup;

// The single argument selects a fixed local verb. Enrollment secrets are never command-line inputs.
if (args.Length != 1) return 2;
try
{
    WindowsAgentSecurity.RequireAdministrator();
    var binary = Path.Combine(AppContext.BaseDirectory, "FlowPlane.Agent.Windows.exe");
    switch (args[0])
    {
        case "install": new WindowsServiceInstaller().Install(binary); break;
        case "uninstall": new WindowsServiceInstaller().Uninstall(binary); break;
        case "initialize":
        case "status":
            var status = await new LocalSetupPipe().SendAsync(new(args[0] == "initialize" ? LocalSetupAction.Initialize : LocalSetupAction.Status), CancellationToken.None);
            Console.WriteLine(status.Status); break;
        case "pair":
            Console.Write("Backend HTTPS address: ");
            var address = Console.ReadLine();
            Console.Write("One-time pairing code (hidden): ");
            var code = new StringBuilder(43);
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Backspace) { if (code.Length != 0) code.Length--; }
                else if (code.Length < 43 && char.IsAscii(key.KeyChar)) code.Append(key.KeyChar);
                else throw new LocalSetupException();
            }
            try
            {
                var receipt = await new LocalSetupPipe().PairAsync(new(LocalSetupAction.Pair, address, code.ToString()), CancellationToken.None);
                Console.WriteLine(); Console.WriteLine(receipt.Status);
            }
            finally { code.Clear(); }
            break;
        default: return 2;
    }
    return 0;
}
catch (Exception) { Console.Error.WriteLine("Local agent setup requires review; protected evidence was retained."); return 1; }
