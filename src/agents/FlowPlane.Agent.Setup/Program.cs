using System.Runtime.InteropServices;
using System.Security.Cryptography;

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
            PrintReceipt(status); break;
        case "pair":
            Console.Write("Backend HTTPS address: ");
            var address = Console.ReadLine();
            Console.Write("Pairing grant ID: ");
            if (!Guid.TryParseExact(Console.ReadLine(), "D", out var grantId) || grantId == Guid.Empty) throw new LocalSetupException();
            Console.Write("One-time pairing code (hidden): ");
            var code = new char[43];
            var length = 0;
            try
            {
                while (true)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.Enter) break;
                    if (key.Key == ConsoleKey.Backspace) { if (length != 0) code[--length] = '\0'; }
                    else if (length < code.Length && char.IsAscii(key.KeyChar)) code[length++] = key.KeyChar;
                    else throw new LocalSetupException();
                }
                var receipt = await new LocalSetupPipe().PairAsync(new(LocalSetupAction.Pair, address, new string(code, 0, length), grantId), CancellationToken.None);
                Console.WriteLine(); PrintReceipt(receipt);
            }
            finally { CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(code.AsSpan())); }
            break;
        default: return 2;
    }
    return 0;
}
catch (Exception) { Console.Error.WriteLine("Local agent setup requires review; protected evidence was retained."); return 1; }

static void PrintReceipt(LocalSetupReceipt receipt)
{
    Console.WriteLine(receipt.Status);
    if (receipt.VerificationPhrase is { } phrase) Console.WriteLine("Verification phrase: " + phrase);
    if (receipt.PairingStage is { } stage) Console.WriteLine("Pairing stage: " + stage);
}
