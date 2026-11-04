using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Processes;
using Xunit;
using Xunit.Abstractions;
using static VIvian.Tests.Processes.ProcessTestSupport;

namespace VIvian.Tests.Processes;

[Collection(ProcessCollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class SupervisorTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly ITestOutputHelper _output = output;
    private readonly WindowsProcessSupervisor _supervisor = new();

    public Task InitializeAsync() => OperatingSystem.IsWindows() ? AssertNoFakeEngineRunningAsync("before test") : Task.CompletedTask;
    public Task DisposeAsync() => OperatingSystem.IsWindows() ? AssertNoFakeEngineRunningAsync("after test") : Task.CompletedTask;

    private Task<ISupervisedProcessGroup> StartAsync(string scenario, params string[] extra) =>
        _supervisor.StartAsync(new ProcessLaunchSpec(FakeEngine,
            ["--run-id", Guid.NewGuid().ToString("D"), "--scenario", scenario, .. extra],
            Path.GetDirectoryName(FakeEngine)!), Guid.NewGuid(), default);

    private static async Task<List<string>> ReadAllAsync(ISupervisedProcessGroup group)
    {
        var lines = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await foreach (var line in group.OutputLines.WithCancellation(cts.Token)) lines.Add(line);
        return lines;
    }

    [WindowsFact]
    public async Task UnrelatedInheritableHandleIsNotVisibleInChild()
    {
        using var unrelated = new EventWaitHandle(false, EventResetMode.ManualReset);
        Assert.True(SetHandleInformation(unrelated.SafeWaitHandle, 1, 1));
        await using var group = await StartAsync("probe-handle", "--probe-handle",
            unrelated.SafeWaitHandle.DangerousGetHandle().ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture));
        var lines = await ReadAllAsync(group);
        Assert.Contains(lines, line => line.EndsWith("|info|unrelated-handle-inherited=False", StringComparison.Ordinal));
        Assert.Equal(0, await group.WaitForRootExitAsync(default));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeWaitHandle handle, uint mask, uint flags);

    [WindowsFact]
    public async Task ArgumentsReachTheEngineExactly()
    {
        string[] tricky = ["E:\\游戏 目录\\原神", "quote\"inside", "trailing\\", "& | > <", ""];
        await using var group = await StartAsync("echo-args", [.. tricky.SelectMany(t => new[] { "--t", t })]);
        var lines = await ReadAllAsync(group);
        var echoed = lines.Where(l => l.Contains("|info|arg=")).Select(l => l[(l.IndexOf("|info|arg=", StringComparison.Ordinal) + 10)..]).ToList();
        foreach (var t in tricky) Assert.Contains(t, echoed);
        Assert.Equal(0, await group.WaitForRootExitAsync(default));
    }

    [WindowsFact]
    public async Task RootIdentityIsRecordedAndOutputIsUtf8()
    {
        await using var group = await StartAsync("noisy-success");
        var lines = await ReadAllAsync(group);
        Assert.Contains(lines, l => l.EndsWith($"|info|pid={group.RootProcessId}", StringComparison.Ordinal));
        Assert.Contains("任务失败？不，已恢复。成功！", lines);
        Assert.InRange(DateTimeOffset.Now - group.RootStartTime, TimeSpan.Zero, TimeSpan.FromMinutes(2));
    }

    [WindowsFact]
    public async Task HarmlessChildKeepsCallerIdentityAndPrivilegeLevel()
    {
        using var caller = WindowsIdentity.GetCurrent();
        var callerElevated = new WindowsPrincipal(caller).IsInRole(WindowsBuiltInRole.Administrator);
        await using var group = await StartAsync("probe-identity");
        var lines = await ReadAllAsync(group);
        Assert.Contains(lines, l => l.EndsWith("|info|user-sid=" + caller.User!.Value, StringComparison.Ordinal));
        Assert.Contains(lines, l => l.EndsWith("|info|elevated=" + callerElevated, StringComparison.Ordinal));
        Assert.Contains(lines, l => l.EndsWith("|info|api-ok=True", StringComparison.Ordinal));
        Assert.Equal(0, await group.WaitForRootExitAsync(default));
    }

    /// <summary>
    /// Membership in VIvian's OWN Job, checked from the parent side with IsProcessInJob(process, VIvian's job handle).
    /// A probe started outside the supervisor is the control group: it must not be in VIvian's job. Whether the test host itself runs inside some job is recorded,
    /// because that is exactly what makes an "in any job" check non-discriminating.
    /// </summary>
    [WindowsFact]
    public async Task ChildIsCountedInViviansOwnJobAndControlProcessIsNot()
    {
        using var control = Process.Start(new ProcessStartInfo(FakeEngine)
        {
            ArgumentList = { "--scenario", "probe-identity-wait", "--run-id", Guid.NewGuid().ToString("D") },
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardInput = true,
        })!;
        try
        {
            var controlLines = new List<string>();
            while (await control.StandardOutput.ReadLineAsync() is { } line)
            {
                controlLines.Add(line);
                if (line.EndsWith("|info|waiting", StringComparison.Ordinal)) break;
            }
            var hostInAnyJob = controlLines.FirstOrDefault(l => l.Contains("|info|any-job-member=", StringComparison.Ordinal));
            _output.WriteLine($"control group (started outside VIvian): {hostInAnyJob ?? "no output"}");

            await using var group = await StartAsync("probe-identity-wait");
            await foreach (var line in group.OutputLines)
                if (line.EndsWith("|info|waiting", StringComparison.Ordinal)) break;

            // Identity, not counts: Windows may add helper processes (e.g. a console host) to the job.
            using var child = Process.GetProcessById(group.RootProcessId);
            Assert.True(group.OwnsProcess(child.SafeHandle), "VIvian's child must be in VIvian's own job");
            Assert.False(group.OwnsProcess(control.SafeHandle), "the control process must not be in VIvian's job");
            using (var self = Process.GetCurrentProcess())
                Assert.False(group.OwnsProcess(self.SafeHandle), "the test host must not be in VIvian's job");
            Assert.True(group.ActiveProcessCount >= 1);
            Assert.False(control.HasExited);

            Assert.True(await group.SendInputLineAsync("stop", default));
            await group.WaitForRootExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
            await WaitForAsync(() => group.ActiveProcessCount == 0);
            Assert.False(control.HasExited, "the control process must be unaffected");
        }
        finally
        {
            if (!control.HasExited) control.Kill(); // exact test-owned process object, never lookup by name
            await control.WaitForExitAsync();
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
        Assert.True(condition());
    }

    [WindowsFact]
    public async Task SameNamedExternalProcessSurvivesJobTermination()
    {
        using var external = Process.Start(new ProcessStartInfo(FakeEngine)
        {
            ArgumentList = { "--scenario", "sleep-forever" },
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        try
        {
            await using var group = await StartAsync("sleep-forever");
            Assert.Equal(CleanupState.Confirmed, await group.TerminateAllAsync(TimeSpan.FromSeconds(10), default));
            Assert.False(HasExitedWithin(external.Id, TimeSpan.FromMilliseconds(300)),
                "terminating our Job must not touch an independently launched process with the same name");
        }
        finally
        {
            if (!external.HasExited) external.Kill(); // exact test-owned process object, never lookup by name
            await external.WaitForExitAsync();
        }
    }

    [WindowsFact]
    public async Task OrphanedChildStaysInsideTheJobAndIsTerminated()
    {
        await using var group = await StartAsync("orphan-child");
        await group.WaitForRootExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        Assert.True(group.ActiveProcessCount >= 1, "the child should still be running inside the job");

        Assert.Equal(CleanupState.Confirmed, await group.TerminateAllAsync(TimeSpan.FromSeconds(10), default));
        Assert.Equal(0, group.ActiveProcessCount);
    }

    [WindowsFact]
    public async Task ClosingTheJobKillsEverythingInIt()
    {
        var group = await StartAsync("sleep-forever");
        var pid = group.RootProcessId;
        Assert.False(HasExitedWithin(pid, TimeSpan.FromMilliseconds(300)));
        await group.DisposeAsync(); // kill-on-close
        Assert.True(HasExitedWithin(pid, TimeSpan.FromSeconds(10)));
    }

    [WindowsFact]
    public async Task TerminateWhenNothingIsRunningIsConfirmedImmediately()
    {
        await using var group = await StartAsync("success");
        await group.WaitForRootExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        await ReadAllAsync(group);
        Assert.Equal(CleanupState.Confirmed, await group.TerminateAllAsync(TimeSpan.FromSeconds(1), default));
    }

    [WindowsFact]
    public async Task GracefulStopIsDeliveredOnStandardInput()
    {
        await using var group = await StartAsync("long-running");
        Assert.True(await group.SendInputLineAsync("stop", default));
        Assert.Equal(3, await group.WaitForRootExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token));
    }

    [WindowsFact]
    public async Task MissingProgramIsReportedAsNothingStarted()
    {
        var spec = new ProcessLaunchSpec(Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid() + ".exe"), [], Path.GetTempPath());
        var ex = await Assert.ThrowsAsync<EngineStartException>(() => _supervisor.StartAsync(spec, Guid.NewGuid(), default));
        Assert.True(ex.NothingStarted);
    }
}
