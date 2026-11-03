using System.Diagnostics;
using VIvian.Core;
using VIvian.Core.Execution;
using Xunit;

namespace VIvian.Tests.Execution;

public sealed class SchedulerTests
{
    private static readonly SchedulerOptions Fast = new(
        GracefulStopTimeout: TimeSpan.FromMilliseconds(300),
        EmergencyStopTimeout: TimeSpan.FromMilliseconds(300),
        ReconcileTimeout: TimeSpan.FromSeconds(1),
        DefaultLimits: new RunLimits(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)));

    private readonly InMemoryJournal _journal = new();

    private QueueScheduler NewScheduler() => new(_journal, new NoProcessSupervisor(), Fast);

    private ScriptedAdapter Adapter(GameId game, Func<ScriptedRun, Task>? script = null)
        => new(game) { Script = script, Journal = _journal };

    private static Func<ScriptedRun, Task> EndsWith(params EvidenceKind[] evidence) => run =>
    {
        run.Emit("working");
        run.End(evidence);
        return Task.CompletedTask;
    };

    private static QueueItem Item(ScriptedAdapter a, RunLimits? limits = null) => new(a.Game, a, limits);

    [Fact]
    public async Task AllGamesWithCompletionEvidenceSucceedInOrder()
    {
        var adapters = new[] { GameId.Genshin, GameId.StarRail, GameId.ZenlessZoneZero }
            .Select(g => Adapter(g, EndsWith(EvidenceKind.CompletionReported))).ToArray();
        var result = await NewScheduler().RunAsync(adapters.Select(a => Item(a)).ToList());

        Assert.Equal(QueueStatus.Completed, result.Status);
        Assert.Equal(new[] { GameId.Genshin, GameId.StarRail, GameId.ZenlessZoneZero }, result.Runs.Select(r => r.Game));
        Assert.All(result.Runs, r =>
        {
            Assert.Equal(RunPhase.Finished, r.Phase);
            Assert.Equal(RunOutcome.Success, r.Outcome);
            Assert.Equal(CleanupState.Confirmed, r.Cleanup);
            Assert.True(r.IsSimulation);
        });
        Assert.All(adapters, a => Assert.True(a.LastRun!.Disposed));
    }

    [Fact]
    public async Task EndingWithoutEvidenceIsIndeterminateNotSuccess()
    {
        var a = Adapter(GameId.Genshin, EndsWith());
        var run = (await NewScheduler().RunAsync([Item(a)])).Runs.Single();
        Assert.Equal(RunOutcome.Indeterminate, run.Outcome);
        Assert.Equal(ReasonCodes.NoCompletionEvidence, run.ReasonCode);
    }

    [Fact]
    public async Task AdapterClaimingSuccessWithoutEvidenceIsDowngraded()
    {
        var a = Adapter(GameId.Genshin, run =>
        {
            run.EndWith(new RunVerdict(RunOutcome.Success, ReasonCodes.Completed, []));
            return Task.CompletedTask;
        });
        var run = (await NewScheduler().RunAsync([Item(a)])).Runs.Single();
        Assert.Equal(RunOutcome.Indeterminate, run.Outcome);
        Assert.Equal(ReasonCodes.SuccessWithoutEvidence, run.ReasonCode);
    }

    [Fact]
    public async Task EvidenceForAnotherRunDoesNotCountAsSuccess()
    {
        var a = Adapter(GameId.Genshin, run =>
        {
            var stale = new Evidence(Guid.NewGuid(), EvidenceKind.CompletionReported, "old log", DateTimeOffset.UtcNow);
            run.EndWith(new RunVerdict(RunOutcome.Success, ReasonCodes.Completed, [stale]));
            return Task.CompletedTask;
        });
        var run = (await NewScheduler().RunAsync([Item(a)])).Runs.Single();
        Assert.Equal(RunOutcome.Indeterminate, run.Outcome);
    }

    [Fact]
    public async Task FailureWithConfirmedCleanupContinuesToNextGame()
    {
        var first = Adapter(GameId.Genshin, EndsWith(EvidenceKind.FailureReported));
        var second = Adapter(GameId.StarRail, EndsWith(EvidenceKind.CompletionReported));
        var result = await NewScheduler().RunAsync([Item(first), Item(second)]);

        Assert.Equal(QueueStatus.Completed, result.Status);
        Assert.Equal(RunOutcome.Failed, result.Runs[0].Outcome);
        Assert.Equal(RunOutcome.Success, result.Runs[1].Outcome);
    }

    [Fact]
    public async Task UnconfirmedCleanupBlocksTheRestOfTheQueue()
    {
        var first = Adapter(GameId.Genshin, EndsWith(EvidenceKind.CompletionReported));
        first.ReconcileResult = CleanupState.Unconfirmed;
        var second = Adapter(GameId.StarRail, EndsWith(EvidenceKind.CompletionReported));
        var result = await NewScheduler().RunAsync([Item(first), Item(second)]);

        Assert.Equal(QueueStatus.Blocked, result.Status);
        Assert.Equal(RunPhase.Blocked, result.Runs.Single().Phase);
        Assert.Equal(CleanupState.Unconfirmed, result.Runs.Single().Cleanup);
        Assert.Equal(new[] { GameId.StarRail }, result.NotStarted);
        Assert.Equal(0, second.StartCount);
    }

    [Fact]
    public async Task ReconcileReportingPendingIsTreatedAsUnconfirmed()
    {
        var a = Adapter(GameId.Genshin, EndsWith(EvidenceKind.CompletionReported));
        a.ReconcileResult = CleanupState.Pending;
        var result = await NewScheduler().RunAsync([Item(a)]);
        Assert.Equal(QueueStatus.Blocked, result.Status);
        Assert.Equal(CleanupState.Unconfirmed, result.Runs.Single().Cleanup);
    }

    [Fact]
    public async Task TaskTimeoutStopsGracefullyAndIsIndeterminate()
    {
        var a = Adapter(GameId.Genshin, run => { run.Emit("started"); return Task.CompletedTask; });
        var limits = new RunLimits(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(10));
        var run = (await NewScheduler().RunAsync([Item(a, limits)])).Runs.Single();

        Assert.Equal(RunOutcome.Indeterminate, run.Outcome);
        Assert.Equal(ReasonCodes.TaskTimeout, run.ReasonCode);
        Assert.Equal(StopMode.Graceful, a.LastRun!.StopRequests.First().Mode);
    }

    [Fact]
    public async Task SilentEngineIsStoppedAsStalled()
    {
        var a = Adapter(GameId.Genshin);
        var limits = new RunLimits(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(200));
        var run = (await NewScheduler().RunAsync([Item(a, limits)])).Runs.Single();
        Assert.Equal(ReasonCodes.Stalled, run.ReasonCode);
        Assert.Equal(RunOutcome.Indeterminate, run.Outcome);
    }

    [Fact]
    public async Task OutputKeepsTheStallWatchdogFromFiring()
    {
        var a = Adapter(GameId.Genshin, async run =>
        {
            for (var i = 0; i < 6; i++) { run.Emit("tick " + i); await Task.Delay(100); }
            run.End(EvidenceKind.CompletionReported);
        });
        var limits = new RunLimits(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(350));
        var run = (await NewScheduler().RunAsync([Item(a, limits)])).Runs.Single();
        Assert.Equal(RunOutcome.Success, run.Outcome);
    }

    [Fact]
    public async Task IgnoredGracefulStopEscalatesToEmergency()
    {
        var a = Adapter(GameId.Genshin);
        a.StopReaction = StopReaction.IgnoreGraceful;
        var limits = new RunLimits(TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(10));
        var run = (await NewScheduler().RunAsync([Item(a, limits)])).Runs.Single();

        Assert.Equal(new[] { StopMode.Graceful, StopMode.Emergency }, a.LastRun!.StopRequests.Select(s => s.Mode));
        Assert.Equal(RunOutcome.Indeterminate, run.Outcome);
        Assert.Equal(CleanupState.Confirmed, run.Cleanup);
    }

    [Fact]
    public async Task EngineThatNeverStopsBlocksQueueWhenCleanupCannotBeConfirmed()
    {
        var first = Adapter(GameId.Genshin);
        first.StopReaction = StopReaction.IgnoreAll;
        first.ReconcileResult = CleanupState.Unconfirmed;
        var second = Adapter(GameId.StarRail, EndsWith(EvidenceKind.CompletionReported));
        var limits = new RunLimits(TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(10));
        var result = await NewScheduler().RunAsync([Item(first, limits), Item(second)]);

        var run = result.Runs.Single();
        Assert.Equal(RunOutcome.Indeterminate, run.Outcome);
        Assert.Equal(ReasonCodes.TaskTimeout, run.ReasonCode);
        Assert.Equal(RunPhase.Blocked, run.Phase);
        Assert.Equal(QueueStatus.Blocked, result.Status);
        Assert.Equal(0, second.StartCount);
    }

    [Fact]
    public async Task EmergencyStopIsImmediateWithoutGracefulPhase()
    {
        var started = new TaskCompletionSource();
        var first = Adapter(GameId.Genshin, run => { run.Emit("running"); started.SetResult(); return Task.CompletedTask; });
        var second = Adapter(GameId.StarRail, EndsWith(EvidenceKind.CompletionReported));
        var scheduler = NewScheduler();
        var queue = scheduler.RunAsync([Item(first), Item(second)]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var clock = Stopwatch.StartNew();
        scheduler.EmergencyStop();
        var result = await queue.WaitAsync(TimeSpan.FromSeconds(5));

        var stop = first.LastRun!.StopRequests.First();
        Assert.Equal(StopMode.Emergency, stop.Mode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"stop took {clock.Elapsed}");
        Assert.Equal(RunOutcome.Cancelled, result.Runs.Single().Outcome);
        Assert.Equal(ReasonCodes.UserStop, result.Runs.Single().ReasonCode);
        Assert.Equal(QueueStatus.Cancelled, result.Status);
        Assert.Equal(new[] { GameId.StarRail }, result.NotStarted);
        Assert.Equal(0, second.StartCount);
    }

    [Fact]
    public async Task EmergencyStopIsSentBeforeSlowJournalWrites()
    {
        _journal.SlowPhase = (RunPhase.Stopping, TimeSpan.FromMilliseconds(800));
        var started = new TaskCompletionSource();
        var a = Adapter(GameId.Genshin, run => { started.SetResult(); return Task.CompletedTask; });
        var scheduler = NewScheduler();
        var queue = scheduler.RunAsync([Item(a)]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var pressedAt = DateTimeOffset.UtcNow;
        scheduler.EmergencyStop();
        await queue.WaitAsync(TimeSpan.FromSeconds(5));
        var sentAt = a.LastRun!.StopRequests.First().At;
        Assert.True(sentAt - pressedAt < TimeSpan.FromMilliseconds(400), $"stop sent after {sentAt - pressedAt}");
    }

    [Fact]
    public async Task EmergencyStopWithUnconfirmedCleanupReportsBlockedNotCancelled()
    {
        var started = new TaskCompletionSource();
        var a = Adapter(GameId.Genshin, run => { started.SetResult(); return Task.CompletedTask; });
        a.ReconcileResult = CleanupState.Unconfirmed;
        var scheduler = NewScheduler();
        var queue = scheduler.RunAsync([Item(a)]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scheduler.EmergencyStop();
        var result = await queue.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(QueueStatus.Blocked, result.Status);
        Assert.Equal(RunOutcome.Cancelled, result.Runs.Single().Outcome);
    }

    [Fact]
    public async Task CallerCancellationActsAsEmergencyStop()
    {
        var started = new TaskCompletionSource();
        var a = Adapter(GameId.Genshin, run => { started.SetResult(); return Task.CompletedTask; });
        using var cts = new CancellationTokenSource();
        var queue = NewScheduler().RunAsync([Item(a)], cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        var result = await queue.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StopMode.Emergency, a.LastRun!.StopRequests.First().Mode);
        Assert.Equal(QueueStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task BlockingPreflightStopsQueueWithoutStarting()
    {
        var first = Adapter(GameId.Genshin);
        first.Preflight = PreflightResult.Block(ReasonCodes.ForeignProcess, "已有不属于本次运行的同名进程");
        var second = Adapter(GameId.StarRail, EndsWith(EvidenceKind.CompletionReported));
        var result = await NewScheduler().RunAsync([Item(first), Item(second)]);

        Assert.Equal(QueueStatus.Blocked, result.Status);
        var run = result.Runs.Single();
        Assert.Equal(RunPhase.Blocked, run.Phase);
        Assert.Null(run.Outcome);
        Assert.Equal(CleanupState.NotRequired, run.Cleanup);
        Assert.Equal(ReasonCodes.ForeignProcess, run.ReasonCode);
        Assert.Equal(0, first.StartCount);
        Assert.Equal(0, second.StartCount);
    }

    [Fact]
    public async Task NonBlockingPreflightSkipsOnlyThatGame()
    {
        var first = Adapter(GameId.Genshin);
        first.Preflight = PreflightResult.Block(ReasonCodes.NotConfigured, "未设置路径", blocksQueue: false);
        var second = Adapter(GameId.StarRail, EndsWith(EvidenceKind.CompletionReported));
        var result = await NewScheduler().RunAsync([Item(first), Item(second)]);

        Assert.Equal(QueueStatus.Completed, result.Status);
        Assert.Equal(RunPhase.Blocked, result.Runs[0].Phase);
        Assert.Equal(RunOutcome.Success, result.Runs[1].Outcome);
    }

    [Fact]
    public async Task StartFailureWithNothingStartedIsFailedAndContinues()
    {
        var first = Adapter(GameId.Genshin);
        first.StartException = new EngineStartException(ReasonCodes.StartFailed, nothingStarted: true, "exe missing");
        var second = Adapter(GameId.StarRail, EndsWith(EvidenceKind.CompletionReported));
        var result = await NewScheduler().RunAsync([Item(first), Item(second)]);

        Assert.Equal(RunOutcome.Failed, result.Runs[0].Outcome);
        Assert.Equal(CleanupState.NotRequired, result.Runs[0].Cleanup);
        Assert.Equal(QueueStatus.Completed, result.Status);
    }

    [Fact]
    public async Task UnexpectedStartErrorBlocksQueueBecauseProcessesMayExist()
    {
        var first = Adapter(GameId.Genshin);
        first.StartException = new InvalidOperationException("boom");
        var second = Adapter(GameId.StarRail, EndsWith(EvidenceKind.CompletionReported));
        var result = await NewScheduler().RunAsync([Item(first), Item(second)]);

        Assert.Equal(RunOutcome.Failed, result.Runs[0].Outcome);
        Assert.Equal(ReasonCodes.AdapterError, result.Runs[0].ReasonCode);
        Assert.Equal(CleanupState.Unconfirmed, result.Runs[0].Cleanup);
        Assert.Equal(QueueStatus.Blocked, result.Status);
    }

    [Fact]
    public async Task StartingIsJournaledBeforeTheEngineStarts()
    {
        var a = Adapter(GameId.Genshin, EndsWith(EvidenceKind.CompletionReported));
        await NewScheduler().RunAsync([Item(a)]);
        Assert.Equal(RunPhase.Starting, a.JournalPhaseAtStart);
    }

    [Fact]
    public async Task EngineIsNotStartedWhenStartingCannotBeJournaled()
    {
        _journal.FailOnPhase = RunPhase.Starting;
        var a = Adapter(GameId.Genshin, EndsWith(EvidenceKind.CompletionReported));
        var result = await NewScheduler().RunAsync([Item(a)]);
        Assert.Equal(0, a.StartCount);
        Assert.Equal(QueueStatus.Blocked, result.Status);
        Assert.Equal(ReasonCodes.JournalUnavailable, result.Runs.Single().ReasonCode);
        Assert.True(result.JournalFaulted);
    }

    [Fact]
    public async Task EventsFromOtherRunsAreIgnored()
    {
        var a = Adapter(GameId.Genshin, run =>
        {
            run.Emit("mine");
            run.Emit("from an old log", Guid.NewGuid());
            run.End(EvidenceKind.CompletionReported);
            return Task.CompletedTask;
        });
        var result = await NewScheduler().RunAsync([Item(a)]);
        var runId = result.Runs.Single().RunId;
        Assert.Equal(new[] { "mine" }, _journal.Events.Select(e => e.Message));
        Assert.All(_journal.Events, e => Assert.Equal(runId, e.RunId));
    }

    [Fact]
    public async Task JournalRecordsEveryPhaseInLegalOrder()
    {
        var a = Adapter(GameId.Genshin, EndsWith(EvidenceKind.CompletionReported));
        await NewScheduler().RunAsync([Item(a)]);
        var phases = _journal.Updates.Select(u => u.Phase).ToList();
        Assert.Equal(new[] { RunPhase.Pending, RunPhase.Preflight, RunPhase.Starting, RunPhase.Running,
            RunPhase.Reconciling, RunPhase.Finished }, phases);
        for (var i = 1; i < phases.Count; i++) Assert.True(RunStateMachine.CanTransition(phases[i - 1], phases[i]));
    }

    [Fact]
    public async Task SecondQueueCannotRunConcurrently()
    {
        var gate = new TaskCompletionSource();
        var a = Adapter(GameId.Genshin, async run => { await gate.Task; run.End(EvidenceKind.CompletionReported); });
        var scheduler = NewScheduler();
        var first = scheduler.RunAsync([Item(a)]);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => scheduler.RunAsync([Item(Adapter(GameId.StarRail))]));
        gate.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(scheduler.IsRunning);
    }

    [Fact]
    public async Task DuplicateGamesInOneQueueAreRejected()
    {
        var a = Adapter(GameId.Genshin);
        await Assert.ThrowsAsync<ArgumentException>(() => NewScheduler().RunAsync([Item(a), Item(a)]));
    }

    [Fact]
    public void QueueItemRejectsMismatchedAdapter() =>
        Assert.Throws<ArgumentException>(() => new QueueItem(GameId.StarRail, new ScriptedAdapter(GameId.Genshin)));

    [Fact]
    public async Task ObserverExceptionsDoNotBreakTheQueue()
    {
        var scheduler = NewScheduler();
        scheduler.RunChanged += _ => throw new InvalidOperationException("ui bug");
        scheduler.EventReceived += _ => throw new InvalidOperationException("ui bug");
        var a = Adapter(GameId.Genshin, EndsWith(EvidenceKind.CompletionReported));
        var result = await scheduler.RunAsync([Item(a)]);
        Assert.Equal(RunOutcome.Success, result.Runs.Single().Outcome);
    }
}
