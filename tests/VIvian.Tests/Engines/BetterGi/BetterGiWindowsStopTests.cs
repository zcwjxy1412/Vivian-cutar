using System.Diagnostics;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Engines.BetterGi;
using VIvian.Infrastructure.Processes;
using VIvian.Tests.Execution;
using VIvian.Tests.Processes;
using Xunit;

namespace VIvian.Tests.Engines.BetterGi;

[Collection(ProcessCollection.Name)]
public sealed class BetterGiWindowsStopTests
{
    [WindowsFact]
    public async Task EmergencyStopClearsTheRealWindowsJobIncludingItsChild()
    {
        await using var fixture = new BetterGiFixture();
        var installRoot = fixture.Root;
        var fake = ProcessTestSupport.FakeEngine;
        var command = new ProcessLaunchSpec(fake, ["--scenario", "orphan-child"], Path.GetDirectoryName(fake)!);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var group = await new WindowsProcessSupervisor().StartAsync(command, fixture.Context.RunId, timeout.Token);
        var log = fixture.Logs.Open(installRoot);
        fixture.Logs.Write(BgiLog.Record(group.RootProcessId, group.RootStartTime, "替身已归属"));
        await using var run = new BetterGiRun(fixture.Context.RunId, group, log,
            BetterGiArguments.OneDragon("默认配置").Request!, new BetterGiRunOptions(), TimeProvider.System, _ => { });

        await using var events = run.Events.GetAsyncEnumerator(timeout.Token);
        int? childPid = null;
        while (await events.MoveNextAsync())
        {
            const string child = "|child-pid=";
            var message = events.Current.Message;
            var start = message.IndexOf(child, StringComparison.Ordinal);
            if (start < 0) continue;
            childPid = int.Parse(message[(start + child.Length)..]);
            break;
        }
        Assert.NotNull(childPid);
        using var childProcess = Process.GetProcessById(childPid.Value);
        Assert.False(childProcess.HasExited);
        Assert.True(group.ActiveProcessCount > 0);

        var result = await run.RequestStopAsync(StopMode.Emergency, timeout.Token);

        Assert.Equal(CleanupState.Confirmed, result.Cleanup);
        await childProcess.WaitForExitAsync(timeout.Token);
        Assert.Equal(0, group.ActiveProcessCount);
        Assert.Equal(RunOutcome.Indeterminate, (await run.WaitForVerdictAsync(timeout.Token)).Outcome);
        Assert.Equal(CleanupState.Confirmed, await run.ReconcileAsync(timeout.Token));
    }
}
