using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using VIvian.Infrastructure.Simulation;
using VIvian.Tests.Execution;
using Xunit;

namespace VIvian.Tests.Processes;

[SupportedOSPlatform("windows")]
internal static partial class ProcessTestSupport
{
    public static string FakeEngine => Path.Combine(AppContext.BaseDirectory, "VIvian.FakeEngine.exe");
    public static string CrashHost => Path.Combine(AppContext.BaseDirectory, "VIvian.CrashHost.exe");

    /// <summary>Fails fast if a previous test left a FakeEngine behind (it would look like a foreign process).</summary>
    public static async Task AssertNoFakeEngineRunningAsync(string when)
    {
        var deadline = Stopwatch.StartNew();
        IReadOnlyList<int> alive;
        do
        {
            alive = FakeEngineAdapter.ForeignProcessIds(FakeEngine);
            if (alive.Count == 0) return;
            await Task.Delay(100);
        } while (deadline.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Fail($"{when}: FakeEngine processes still running: {string.Join(",", alive)}");
    }

    public static bool HasExitedWithin(int pid, TimeSpan timeout)
    {
        Process process;
        try { process = Process.GetProcessById(pid); }
        catch (ArgumentException) { return true; } // not running
        using (process)
        {
            try { return process.WaitForExit(timeout); }
            catch (InvalidOperationException) { return true; }
        }
    }

    public static int EnginePid(InMemoryJournal journal) => FindNumber(journal, EnginePidPattern());
    public static int ChildPid(InMemoryJournal journal) => FindNumber(journal, ChildPidPattern());

    private static int FindNumber(InMemoryJournal journal, Regex pattern)
    {
        foreach (var e in journal.EventsSnapshot())
        {
            var match = pattern.Match(e.Message);
            if (match.Success) return int.Parse(match.Groups[1].Value);
        }
        throw new InvalidOperationException("PID not found in run events.");
    }

    [GeneratedRegex(@"模拟引擎已启动：PID (\d+)")]
    private static partial Regex EnginePidPattern();

    [GeneratedRegex(@"child-pid=(\d+)")]
    private static partial Regex ChildPidPattern();
}
