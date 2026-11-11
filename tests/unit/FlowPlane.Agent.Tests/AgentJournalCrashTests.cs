using System.Diagnostics;

using FlowPlane.Agent.Runtime.Journal;

namespace FlowPlane.Agent.Tests;

public sealed class AgentJournalCrashTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task Abrupt_process_kill_preserves_flushed_markers_or_rejects_partial_tail(int step)
    {
        var directory = Path.Combine(Path.GetTempPath(), "flowplane-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "operation.journal");
        var installation = Guid.NewGuid();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "FlowPlane.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var helper = Path.Combine(root.FullName, "tests/helpers/FlowPlane.Agent.JournalProbe/bin", configuration, "net10.0/FlowPlane.Agent.JournalProbe.dll");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(helper); start.ArgumentList.Add(path); start.ArgumentList.Add(installation.ToString("D")); start.ArgumentList.Add(step.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var process = Process.Start(start)!;
        try
        {
            Assert.Equal("READY", await process.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.NotEqual(0, process.ExitCode);
            if (step == 6) Assert.Throws<AgentJournalException>(() => AgentOperationJournal.Open(path, installation));
            else
            {
                using var reopened = AgentOperationJournal.Open(path, installation);
                var summary = await reopened.ReadRecoverySummaryAsync(TestContext.Current.CancellationToken);
                Assert.Equal(step is 0 or 5 ? 0 : 1, summary.UnresolvedCount);
                Assert.Equal(step is >= 1 and < 5, summary.RequiresReconciliation);
            }
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(TestContext.Current.CancellationToken); }
            Directory.Delete(directory, true);
        }
    }
}
