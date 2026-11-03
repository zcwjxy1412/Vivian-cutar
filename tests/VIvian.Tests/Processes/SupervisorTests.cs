using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Processes;
using Xunit;
using static VIvian.Tests.Processes.ProcessTestSupport;

namespace VIvian.Tests.Processes;

[Collection(ProcessCollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class SupervisorTests : IAsyncLifetime
{
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
