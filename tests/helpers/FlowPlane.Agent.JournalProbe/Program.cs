using FlowPlane.Agent.Protocol;
using FlowPlane.Agent.Runtime.Journal;

if (args.Length != 3 || !Guid.TryParseExact(args[1], "D", out var installation) || !int.TryParse(args[2], out var step) || step is < 0 or > 6)
    return 2;
// Synthetic local evidence only. This helper is never part of the Windows distribution.
using var journal = AgentOperationJournal.Open(args[0], installation, initialize: true);
var key = new AgentOperationKey(Guid.Parse("11111111-1111-4111-8111-111111111111"), Guid.Parse("22222222-2222-4222-8222-222222222222"), 1, 1, new string('a', 64));
if (step >= 1) await journal.AppendAcceptedAsync(key);
if (step >= 2) await journal.AppendAdmissionAsync(key);
if (step >= 3) await journal.MarkMayHaveInvokedAsync(key);
if (step >= 4) await journal.RecordResultAsync(key, new(Guid.Parse("33333333-3333-4333-8333-333333333333"), long.MaxValue, DateTimeOffset.Parse("2026-11-10T12:00:00Z")));
if (step >= 5) await journal.MarkCorePersistedAsync(key);
if (step == 6)
{
    journal.Dispose();
    using var interrupted = new FileStream(args[0], FileMode.Append, FileAccess.Write);
    interrupted.Write([182, 0, 0, 0, 1, 1]);
    interrupted.Flush(true);
}
Console.WriteLine("READY");
await Task.Delay(Timeout.InfiniteTimeSpan);
return 0;
