using System.Diagnostics;
using System.Runtime.Versioning;
using VIvian.Core;
using VIvian.Core.Execution;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Processes;
using VIvian.Infrastructure.Simulation;
using VIvian.Tests.Execution;
using Xunit;
using static VIvian.Tests.Processes.ProcessTestSupport;

namespace VIvian.Tests.Processes;

/// <summary>
/// The eleven failure scenarios of docs/M2_PLAN.md §4, run end to end with real processes:
/// QueueScheduler → FakeEngineAdapter → WindowsProcessSupervisor (job object) → FakeEngine.exe.
/// </summary>
[Collection(ProcessCollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class FakeEngineScenarioTests : IAsyncLifetime
{
    private sealed class LateLaunchAdapter(string engine, Task release) : IGameAdapter
    {
        public GameId Game => GameId.Genshin;
        public string RuleVersion => "late-launch-test";
        public bool IsSimulation => true;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? LaunchError;
        public Task<PreflightResult> PreflightAsync(RunContext context, CancellationToken ct) =>
            Task.FromResult(PreflightResult.Ok());
        public async Task<IEngineRun> StartAsync(RunContext context, IProcessSupervisor supervisor, CancellationToken ct)
        {
            Entered.TrySetResult();
            await release.ConfigureAwait(false);
            // Deliberately ignore the cancelled token: the scoped supervisor must still reject this.
            try
            {
                await supervisor.StartAsync(new ProcessLaunchSpec(engine,
                    ["--run-id", context.RunId.ToString("D"), "--scenario", "long-running"],
                    Path.GetDirectoryName(engine)!), context.RunId, CancellationToken.None);
            }
            catch (Exception ex) { LaunchError = ex; }
            throw new EngineStartException(ReasonCodes.StartFailed, nothingStarted: LaunchError is not null);
        }
    }

    [WindowsFact]
    public async Task TimedOutStartCannotCreateLateFakeEngineEvenWithUncancelledToken()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new LateLaunchAdapter(FakeEngine, release.Task);
        var scheduler = new QueueScheduler(new InMemoryJournal(), new WindowsProcessSupervisor(),
            Options with { StartTimeout = TimeSpan.FromMilliseconds(200) });
        try
        {
            var queue = scheduler.RunAsync([new QueueItem(GameId.Genshin, adapter)]);
            await adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var result = await queue.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(QueueStatus.Blocked, result.Status);
            release.TrySetResult();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (adapter.LaunchError is null && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert.IsType<EngineStartException>(adapter.LaunchError);
            Assert.Empty(FakeEngineAdapter.ForeignProcessIds(FakeEngine));
            Assert.Equal(CleanupState.Unconfirmed, result.Runs.Single().Cleanup);
        }
        finally { release.TrySetResult(); }
    }

    private static readonly SchedulerOptions Options = new(
        GracefulStopTimeout: TimeSpan.FromSeconds(3),
        EmergencyStopTimeout: TimeSpan.FromSeconds(10),
        ReconcileTimeout: TimeSpan.FromSeconds(15),
        DefaultLimits: new RunLimits(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30)));

    private readonly InMemoryJournal _journal = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vivian-scenarios", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return OperatingSystem.IsWindows() ? AssertNoFakeEngineRunningAsync("before scenario") : Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (OperatingSystem.IsWindows()) await AssertNoFakeEngineRunningAsync("after scenario");
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private QueueScheduler NewScheduler(IRunJournal? journal = null) => new(journal ?? _journal, new WindowsProcessSupervisor(), Options);

    private async Task<(RunUpdate Run, QueueResult Queue)> RunAsync(string scenario, RunLimits? limits = null)
    {
        var adapter = new FakeEngineAdapter(GameId.Genshin, FakeEngine, scenario);
        var queue = await NewScheduler().RunAsync([new QueueItem(GameId.Genshin, adapter, limits)]).WaitAsync(TimeSpan.FromSeconds(90));
        return (queue.Runs.Single(), queue);
    }

    private bool HasEvent(Func<RunEvent, bool> predicate) => _journal.EventsSnapshot().Any(predicate);

    // 1
    [WindowsFact]
    public async Task S01_SuccessWithCompletionEvidence()
    {
        var (run, queue) = await RunAsync("success");
        Assert.Equal(RunOutcome.Success, run.Outcome);
        Assert.Equal(CleanupState.Confirmed, run.Cleanup);
        Assert.True(run.IsSimulation);
        Assert.Equal(QueueStatus.Completed, queue.Status);
        Assert.True(HasEvent(e => e.Message == "step 3/3"));
        Assert.True(HasExitedWithin(EnginePid(_journal), TimeSpan.FromSeconds(5)));
    }

    // 2
    [WindowsFact]
    public async Task S02_ExitCodeZeroWithoutEvidenceIsIndeterminate()
    {
        var (run, _) = await RunAsync("no-evidence");
        Assert.Equal(RunOutcome.Indeterminate, run.Outcome);
        Assert.Equal(ReasonCodes.NoCompletionEvidence, run.ReasonCode);
        Assert.True(HasEvent(e => e.Message.Contains("退出码 0", StringComparison.Ordinal)));
    }

    // 3
    [WindowsFact]
    public async Task S03_ExplicitFailureIsFailed()
    {
        var (run, _) = await RunAsync("failure");
        Assert.Equal(RunOutcome.Failed, run.Outcome);
        Assert.Equal(ReasonCodes.ReportedFailure, run.ReasonCode);
        Assert.Equal(CleanupState.Confirmed, run.Cleanup);
    }

    // 4
    [WindowsFact]
    public async Task S04_ErrorAndSuccessWordsInLogsDoNotDecideTheOutcome()
    {
        var (run, _) = await RunAsync("noisy-success");
        Assert.Equal(RunOutcome.Success, run.Outcome);
        Assert.True(HasEvent(e => e.Message.StartsWith("ERROR:", StringComparison.Ordinal)));
    }

    // 5
    [WindowsFact]
    public async Task S05_EvidenceFromAnEarlierRunIsIgnored()
    {
        var (run, _) = await RunAsync("stale-evidence");
        Assert.Equal(RunOutcome.Indeterminate, run.Outcome);
        Assert.True(HasEvent(e => e.Kind == RunEventKind.Warning && e.Message.StartsWith("忽略不属于本次运行", StringComparison.Ordinal)));
    }

    // 6
    [WindowsFact]
    public async Task S06_ChildLeftRunningAfterParentExitIsCleanedUp()
    {
        var (run, _) = await RunAsync("orphan-child");
        Assert.Equal(RunOutcome.Success, run.Outcome);
        Assert.Equal(CleanupState.Confirmed, run.Cleanup);
        Assert.True(HasExitedWithin(ChildPid(_journal), TimeSpan.FromSeconds(5)), "orphaned child must be terminated");
    }

    // 7
    [WindowsFact]
    public async Task S07_SilentEngineIsStoppedAsStalled()
    {
        var (run, _) = await RunAsync("hang-silent", new RunLimits(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(3)));
        Assert.Equal(RunOutcome.Indeterminate, run.Outcome);
        Assert.Equal(ReasonCodes.Stalled, run.ReasonCode);
        Assert.Equal(CleanupState.Confirmed, run.Cleanup);
        Assert.True(_journal.AnyInPhase(RunPhase.Stopping));
    }

    // 8
    [WindowsFact]
    public async Task S08_IgnoredStopEscalatesToTerminatingTheJob()
    {
        var clock = Stopwatch.StartNew();
        var (run, _) = await RunAsync("ignore-stop", new RunLimits(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(60)));
        Assert.Equal(RunOutcome.Indeterminate, run.Outcome);
        Assert.Equal(ReasonCodes.TaskTimeout, run.ReasonCode);
        Assert.Equal(CleanupState.Confirmed, run.Cleanup);
        // Task limit (3 s) + ignored graceful window (3 s) before the job is terminated.
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(6), $"elapsed {clock.Elapsed}");
        Assert.True(HasExitedWithin(EnginePid(_journal), TimeSpan.FromSeconds(5)));
    }

    // 9
    [WindowsFact]
    public async Task S09_EmergencyStopIsImmediate()
    {
        var scheduler = NewScheduler();
        var adapter = new FakeEngineAdapter(GameId.StarRail, FakeEngine, "long-running");
        var second = new FakeEngineAdapter(GameId.ZenlessZoneZero, FakeEngine, "success");
        var queue = scheduler.RunAsync([new QueueItem(GameId.StarRail, adapter), new QueueItem(GameId.ZenlessZoneZero, second)]);
        var wait = Stopwatch.StartNew();
        while (!HasEvent(e => e.Message == "working") && wait.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(20);
        var enginePid = EnginePid(_journal);

        var clock = Stopwatch.StartNew();
        scheduler.EmergencyStop();
        Assert.True(HasExitedWithin(enginePid, TimeSpan.FromSeconds(5)), "engine must be gone within seconds");
        var stopLatency = clock.Elapsed;
        var result = await queue.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(stopLatency < TimeSpan.FromSeconds(3), $"stop took {stopLatency}");
        Assert.Equal(RunOutcome.Cancelled, result.Runs.Single().Outcome);
        Assert.Equal(CleanupState.Confirmed, result.Runs.Single().Cleanup);
        Assert.Equal(QueueStatus.Cancelled, result.Status);
        Assert.Equal(new[] { GameId.ZenlessZoneZero }, result.NotStarted);
    }

    // 10
    [WindowsFact]
    public async Task S10_VivianCrashKillsItsEnginesAndRestartReconcilesHonestly()
    {
        var database = Path.Combine(_root, "crash.db");
        using var host = Process.Start(new ProcessStartInfo(CrashHost)
        {
            ArgumentList = { database, FakeEngine }, RedirectStandardOutput = true, UseShellExecute = false,
        })!;
        int enginePid;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            string? line;
            do line = await host.StandardOutput.ReadLineAsync(cts.Token);
            while (line is not null && !line.StartsWith("ENGINE-PID ", StringComparison.Ordinal));
            Assert.NotNull(line);
            enginePid = int.Parse(line["ENGINE-PID ".Length..]);
            Assert.False(HasExitedWithin(enginePid, TimeSpan.FromMilliseconds(300)));
        }
        finally
        {
            // Simulate a crash: the host is killed without any chance to clean up.
            host.Kill();
            await host.WaitForExitAsync();
        }

        Assert.True(HasExitedWithin(enginePid, TimeSpan.FromSeconds(10)), "kill-on-close must end the engine");

        var restarted = new LocalDatabase(database);
        await restarted.InitializeAsync();
        var reconciled = Assert.Single(await restarted.ReconcileInterruptedRunsAsync(DateTimeOffset.UtcNow));
        Assert.Equal(RunPhase.Blocked, reconciled.Phase);
        Assert.Equal(RunOutcome.Indeterminate, reconciled.Outcome);
        Assert.Equal(CleanupState.Unconfirmed, reconciled.Cleanup);
        Assert.Equal(ReasonCodes.InterruptedByCrash, reconciled.ReasonCode);
        Assert.True(reconciled.IsSimulation);

        var success = new FakeEngineAdapter(GameId.Genshin, FakeEngine, "success");
        var refused = await NewScheduler(restarted).RunAsync([new QueueItem(GameId.Genshin, success)]);
        Assert.Equal(QueueStatus.Blocked, refused.Status);
        Assert.Equal(ReasonCodes.UnresolvedRuns, refused.BlockReason);

        await restarted.AcknowledgeAsync(reconciled.RunId, "测试：确认引擎已结束", DateTimeOffset.UtcNow);
        var allowed = await NewScheduler(restarted).RunAsync([new QueueItem(GameId.Genshin, success)]);
        Assert.Equal(QueueStatus.Completed, allowed.Status);
        Assert.Equal(RunOutcome.Indeterminate, (await restarted.ReadRecentRunsAsync(10)).Single(r => r.RunId == reconciled.RunId).Outcome);
    }

    // 11
    [WindowsFact]
    public async Task S11_ForeignEngineProcessBlocksTheQueueAndIsLeftAlone()
    {
        // Stands in for an engine the user started by hand; it was not started by VIvian.
        using var foreign = Process.Start(new ProcessStartInfo(FakeEngine)
        {
            ArgumentList = { "--scenario", "sleep-forever" }, UseShellExecute = false, CreateNoWindow = true,
        })!;
        try
        {
            var adapter = new FakeEngineAdapter(GameId.Genshin, FakeEngine, "success");
            var result = await NewScheduler().RunAsync([new QueueItem(GameId.Genshin, adapter)]);
            Assert.Equal(QueueStatus.Blocked, result.Status);
            var run = result.Runs.Single();
            Assert.Equal(RunPhase.Blocked, run.Phase);
            Assert.Equal(ReasonCodes.ForeignProcess, run.ReasonCode);
            Assert.Null(run.Outcome);
            Assert.False(foreign.HasExited, "VIvian must not touch a process it did not start");
        }
        finally
        {
            foreign.Kill();
            await foreign.WaitForExitAsync();
        }
    }
}
