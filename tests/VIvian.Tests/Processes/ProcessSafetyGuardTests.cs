using System.Text.RegularExpressions;
using Xunit;

namespace VIvian.Tests.Processes;

/// <summary>
/// Acceptance check from docs/M2_PLAN.md §5: product code must contain no path that stops processes by name.
/// Scans every .cs file under src/. Only the job-object supervisor may terminate, and only processes it owns.
/// </summary>
public sealed class ProcessSafetyGuardTests
{
    private static readonly string SourceRoot = FindSourceRoot();

    private static string FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VIvian.slnx"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("repository root not found"), "src");
    }

    private static IEnumerable<(string File, int Line, string Text)> Lines() =>
        Directory.EnumerateFiles(SourceRoot, "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadLines(f).Select((t, i) => (Path.GetRelativePath(SourceRoot, f).Replace('\\', '/'), i + 1, t)));

    [Theory]
    [InlineData(@"\.Kill\s*\(")]
    [InlineData(@"(?i)taskkill")]
    [InlineData(@"(?i)\bpkill\b|\bkillall\b")]
    [InlineData(@"CloseMainWindow\s*\(")]
    public void NoProcessIsStoppedOutsideTheSupervisor(string pattern)
    {
        var hits = Lines().Where(l => Regex.IsMatch(l.Text, pattern)).ToList();
        Assert.True(hits.Count == 0, "Forbidden process stop found:\n" + string.Join("\n", hits.Select(h => $"{h.File}:{h.Line}: {h.Text.Trim()}")));
    }

    [Fact]
    public void TerminationApisAppearOnlyInTheJobObjectSupervisor()
    {
        var allowed = new[] { "VIvian.Infrastructure/Processes/WindowsProcessSupervisor.cs", "VIvian.Infrastructure/Processes/NativeMethods.cs" };
        var hits = Lines().Where(l => Regex.IsMatch(l.Text, @"\bTerminate(Process|JobObject)\b") && !allowed.Contains(l.File)).ToList();
        Assert.True(hits.Count == 0, string.Join("\n", hits.Select(h => $"{h.File}:{h.Line}")));
    }

    /// <summary>
    /// Read-only discovery (e.g. the March7th inspector) must never be able to launch anything: every way to start a
    /// process is confined to the job-object supervisor, which places the child in VIvian's own job.
    /// </summary>
    [Theory]
    [InlineData(@"\bProcess\.Start\s*\(")]
    [InlineData(@"\bnew\s+(System\.Diagnostics\.)?ProcessStartInfo\b")]
    [InlineData(@"(?i)\bShellExecute")]
    [InlineData(@"\bCreateProcess\w*\s*\(")]
    [InlineData(@"\bWinExec\s*\(")]
    public void ProcessesAreStartedOnlyByTheJobObjectSupervisor(string pattern)
    {
        var allowed = new[] { "VIvian.Infrastructure/Processes/WindowsProcessSupervisor.cs", "VIvian.Infrastructure/Processes/NativeMethods.cs" };
        var hits = Lines().Where(l => Regex.IsMatch(l.Text, pattern) && !allowed.Contains(l.File)).ToList();
        Assert.True(hits.Count == 0, "Process start outside the supervisor:\n" + string.Join("\n", hits.Select(h => $"{h.File}:{h.Line}: {h.Text.Trim()}")));
    }

    [Fact]
    public void ProcessLookupByNameIsUsedOnlyForDetection()
    {
        // Discovery by name is read-only. Game window input separately requires a verified path, PID and start time.
        var hits = Lines().Where(l => l.Text.Contains("GetProcessesByName", StringComparison.Ordinal)).ToList();
        Assert.All(hits, h => Assert.Contains(h.File, new[]
        {
            "VIvian.Infrastructure/Simulation/FakeEngineAdapter.cs",
            "VIvian.Infrastructure/Accounts/StarRailWindow.cs",
            "VIvian.Infrastructure/Engines/BetterGi/GenshinWindowsLoginDesktop.cs",
        }));
    }

    [Fact]
    public void GuardActuallyScansTheProductCode() =>
        Assert.Contains(Lines(), l => l.File.EndsWith("QueueScheduler.cs", StringComparison.Ordinal));
}
