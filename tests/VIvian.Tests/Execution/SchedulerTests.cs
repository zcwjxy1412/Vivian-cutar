using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using VIvian.Core;
using VIvian.Core.Execution;
using Xunit;

namespace VIvian.Tests.Execution;

/// <summary>Timing-sensitive tests run alone so disk-heavy tests cannot starve their timers.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingCollection
{
    public const string Name = "Timing-sensitive scheduler tests";
}

[Collection(TimingCollection.Name)]
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
    public async Task DisposedCallerTokenDoesNotLeaveSchedulerRunning()
    {
        var scheduler = NewScheduler();
        using var source = new CancellationTokenSource();
        var token = source.Token;
        source.Dispose();
        var first = Adapter(GameId.Genshin, EndsWith(EvidenceKind.CompletionReported));

        // .NET 10 permits registration on an already disposed token source; record that actual contract.
        var firstResult = await scheduler.RunAsync([Item(first)], token);
        Assert.Equal(QueueStatus.Completed, firstResult.Status);
        Assert.False(scheduler.IsRunning);
        Assert.Equal(1, first.StartCount);

        var next = Adapter(GameId.StarRail, EndsWith(EvidenceKind.CompletionReported));
        var result = await scheduler.RunAsync([Item(next)]);
        Assert.Equal(QueueStatus.Completed, result.Status);
        Assert.Equal(RunOutcome.Success, result.Runs.Single().Outcome);
    }

    [Fact]
    public async Task AdapterClaimingSuccessWithConflictingEvidenceIsIndeterminate()
    {
        var a = Adapter(GameId.Genshin, run =>
        {
            var evidence = new[] { EvidenceKind.CompletionReported, EvidenceKind.FailureReported }
                .Select(kind => new Evidence(run.RunId, kind, "test", DateTimeOffset.UtcNow)).ToArray();
            run.EndWith(new RunVerdict(RunOutcome.Success, ReasonCodes.Completed, evidence));
            return Task.CompletedTask;
        });
        var result = await NewScheduler().RunAsync([Item(a)]);
        Assert.Equal(RunOutcome.Indeterminate, result.Runs.Single().Outcome);
        Assert.Equal(ReasonCodes.ConflictingEvidence, result.Runs.Single().ReasonCode);
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
        var a = Adapter(GameId.Genshin);
        var limits = new RunLimits(TaskTimeout: TimeSpan.FromMinutes(45), StallTimeout: TimeSpan.FromHours(2));
        var (result, time) = await RunOnVirtualClockAsync([Item(a, limits)]);

        var run = result.Runs.Single();
        Assert.Equal(RunOutcome.Indeterminate, run.Outcome);
        Assert.Equal(ReasonCodes.TaskTimeout, run.ReasonCode);
        Assert.Equal(StopMode.Graceful, a.LastRun!.StopRequests.Single().Mode);
        Assert.InRange(time.GetUtcNow() - VirtualStart, TimeSpan.FromMinutes(45), TimeSpan.FromMinutes(50));
    }

    [Fact]
    public async Task SilentEngineIsStoppedAsStalled()
    {
        var a = Adapter(GameId.Genshin);
        var limits = new RunLimits(TaskTimeout: TimeSpan.FromHours(2), StallTimeout: TimeSpan.FromMinutes(8));
        var (result, time) = await RunOnVirtualClockAsync([Item(a, limits)]);

        var run = result.Runs.Single();
        Assert.Equal(ReasonCodes.Stalled, run.ReasonCode);
        Assert.Equal(RunOutcome.Indeterminate, run.Outcome);
        Assert.InRange(time.GetUtcNow() - VirtualStart, TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(13));
    }

    [Fact]
    public async Task OutputKeepsTheStallWatchdogFromFiring()
    {
        var time = new FakeTimeProvider(VirtualStart);
        var a = Adapter(GameId.Genshin);
        var limits = new RunLimits(TaskTimeout: TimeSpan.FromHours(2), StallTimeout: TimeSpan.FromMinutes(8));
        var queue = new QueueScheduler(_journal, new NoProcessSupervisor(), Fast, time).RunAsync([Item(a, limits)]);
        await WaitUntilAsync(() => a.LastRun is { } r && _journal.PhaseOf(r.RunId) == RunPhase.Running);

        // 30 virtual minutes in total — far beyond the 8-minute stall limit — but never 8 minutes of silence.
        for (var i = 0; i < 6; i++)
        {
            a.LastRun!.Emit("progress " + i);
            await WaitUntilAsync(() => _journal.EventCount == i + 1);
            time.Advance(TimeSpan.FromMinutes(5));
            await Task.Yield();
        }
        a.LastRun!.End(EvidenceKind.CompletionReported);

        var run = (await queue.WaitAsync(TimeSpan.FromSeconds(10))).Runs.Single();
        Assert.Equal(RunOutcome.Success, run.Outcome);
        Assert.Empty(a.LastRun.StopRequests);
    }

    [Fact]
    public async Task IgnoredGracefulStopEscalatesToEmergency()
    {
        var a = Adapter(GameId.Genshin);
        a.StopReaction = StopReaction.IgnoreGraceful;
        var limits = new RunLimits(TaskTimeout: TimeSpan.FromMinutes(45), StallTimeout: TimeSpan.FromHours(2));
        var (result, _) = await RunOnVirtualClockAsync([Item(a, limits)]);

        var run = result.Runs.Single();
        Assert.Equal(new[] { StopMode.Graceful, StopMode.Emergency }, a.LastRun!.StopRequests.Select(x => x.Mode));
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
        var limits = new RunLimits(TaskTimeout: TimeSpan.FromMinutes(45), StallTimeout: TimeSpan.FromHours(2));
        var (result, _) = await RunOnVirtualClockAsync([Item(first, limits), Item(second)]);

        var run = result.Runs.Single();
        Assert.Equal(new[] { StopMode.Graceful, StopMode.Emergency }, first.LastRun!.StopRequests.Select(x => x.Mode));
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
        _journal.SlowPhase = (RunPhase.Stopping, TimeSpan.FromSeconds(3));
        var started = new TaskCompletionSource();
        var a = Adapter(GameId.Genshin, run => { started.SetResult(); return Task.CompletedTask; });
        var scheduler = NewScheduler();
        var queue = scheduler.RunAsync([Item(a)]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var pressedAt = DateTimeOffset.UtcNow;
        scheduler.EmergencyStop();
        await queue.WaitAsync(TimeSpan.FromSeconds(10));
        var sentAt = a.LastRun!.StopRequests.First().At;
        Assert.True(sentAt - pressedAt < TimeSpan.FromMilliseconds(1500), $"stop sent after {sentAt - pressedAt}");
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
    public async Task EmergencyStopDuringStartDoesNotLoseRequestOrStartNextGame()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Adapter(GameId.Genshin);
        first.StartGate = gate;
        var second = Adapter(GameId.StarRail, EndsWith(EvidenceKind.CompletionReported));
        var scheduler = NewScheduler();
        var queue = scheduler.RunAsync([Item(first), Item(second)]);
        await WaitUntilAsync(() => first.StartCount == 1);

        scheduler.EmergencyStop();
        gate.SetResult();
        var result = await queue.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(QueueStatus.Cancelled, result.Status);
        Assert.Equal(RunOutcome.Cancelled, result.Runs.Single().Outcome);
        Assert.Equal(CleanupState.NotRequired, result.Runs.Single().Cleanup);
        Assert.Equal(0, second.StartCount);
    }

    /// <summary>
    /// The adapter's cancellation token must already be cancelled when EmergencyStop() returns — not some time later
    /// on the thread pool — so an adapter checking it can never start a process after the user pressed stop.
    /// Repeated to make a timing-dependent regression show up reliably.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopIsVisibleOnAdapterTokenWhenStopReturns(bool viaCallerToken)
    {
        for (var i = 0; i < 25; i++)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var adapter = Adapter(GameId.Genshin);
            adapter.StartGate = gate;
            var scheduler = NewScheduler();
            using var caller = new CancellationTokenSource();
            var queue = scheduler.RunAsync([Item(adapter)], caller.Token);
            await adapter.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            if (viaCallerToken) caller.Cancel(); else scheduler.EmergencyStop();
            var visible = adapter.StartToken.IsCancellationRequested; // read immediately, no waiting
            visible &= adapter.LinkedStartToken.IsCancellationRequested; // a token linked to it is cancelled too

            gate.SetResult();
            await queue.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(visible, $"iteration {i}: adapter token not yet cancelled when the stop call returned");
        }
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
    public async Task EventsAreFullyStoredBeforeTheRunIsReportedFinished()
    {
        _journal.EventDelay = TimeSpan.FromSeconds(3);
        var a = Adapter(GameId.Genshin, run =>
        {
            run.Emit("one"); run.Emit("two"); run.Emit("three");
            run.End(EvidenceKind.CompletionReported);
            return Task.CompletedTask;
        });
        await NewScheduler().RunAsync([Item(a)]);
        Assert.Equal(new[] { "one", "two", "three" }, _journal.EventsSnapshot().Select(e => e.Message));
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

    [Fact]
    public async Task UnresolvedRunsFromEarlierQueuesBlockNewQueue()
    {
        _journal.Unresolved = 1;
        var a = Adapter(GameId.Genshin, EndsWith(EvidenceKind.CompletionReported));
        var result = await NewScheduler().RunAsync([Item(a)]);
        Assert.Equal(QueueStatus.Blocked, result.Status);
        Assert.Equal(ReasonCodes.UnresolvedRuns, result.BlockReason);
        Assert.Empty(result.Runs);
        Assert.Equal(new[] { GameId.Genshin }, result.NotStarted);
        Assert.Equal(0, a.StartCount);
    }

    [Fact]
    public async Task UnreadableJournalBlocksNewQueue()
    {
        _journal.FailCount = true;
        var a = Adapter(GameId.Genshin, EndsWith(EvidenceKind.CompletionReported));
        var result = await NewScheduler().RunAsync([Item(a)]);
        Assert.Equal(QueueStatus.Blocked, result.Status);
        Assert.Equal(ReasonCodes.JournalUnavailable, result.BlockReason);
        Assert.True(result.JournalFaulted);
        Assert.Equal(0, a.StartCount);
    }

    [Fact]
    public async Task BlockedQueueReportsCleanupAsTheReason()
    {
        var a = Adapter(GameId.Genshin, EndsWith(EvidenceKind.CompletionReported));
        a.ReconcileResult = CleanupState.Unconfirmed;
        var result = await NewScheduler().RunAsync([Item(a)]);
        Assert.Equal(ReasonCodes.CleanupUnconfirmed, result.BlockReason);
    }

    [Fact]
    public async Task StopRequestedBeforeTheQueueBeginsStartsNothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // e.g. the user pressed stop the instant after pressing start
        var a = Adapter(GameId.Genshin, EndsWith(EvidenceKind.CompletionReported));
        var b = Adapter(GameId.StarRail, EndsWith(EvidenceKind.CompletionReported));
        var result = await NewScheduler().RunAsync([Item(a), Item(b)], cts.Token);
        Assert.Equal(QueueStatus.Cancelled, result.Status);
        Assert.Equal(new[] { GameId.Genshin, GameId.StarRail }, result.NotStarted);
        Assert.Equal(0, a.StartCount + b.StartCount);
    }

    [Fact]
    public async Task InternalErrorAfterStartClosesRunAsBlockedAndUnconfirmed()
    {
        // Fault injection: the clock fails once the engine is running, so the scheduler itself throws.
        var time = new FailingTimers(VirtualStart);
        var started = Adapter(GameId.Genshin, run => { run.Emit("x"); return Task.CompletedTask; });
        var next = Adapter(GameId.StarRail, EndsWith(EvidenceKind.CompletionReported));
        var result = await new QueueScheduler(_journal, new NoProcessSupervisor(), Fast, time)
            .RunAsync([Item(started), Item(next)]).WaitAsync(TimeSpan.FromSeconds(10));

        var run = result.Runs.Single();
        Assert.Equal(RunPhase.Blocked, run.Phase);
        Assert.Equal(RunOutcome.Indeterminate, run.Outcome);
        Assert.Equal(CleanupState.Unconfirmed, run.Cleanup);
        Assert.Equal(ReasonCodes.InternalError, run.ReasonCode);
        Assert.Equal(QueueStatus.Blocked, result.Status);
        Assert.Equal(0, next.StartCount);
        Assert.True(started.LastRun!.Disposed);
        Assert.Equal(RunPhase.Blocked, _journal.PhaseOf(run.RunId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(25 * 3600)]
    public void InvalidLimitsAreRejected(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunLimits(TimeSpan.FromSeconds(seconds), TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunLimits(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(seconds)));
    }

    /// <summary>A clock whose timers fail: the first timer the scheduler creates is the task timeout, right after Running.</summary>
    private sealed class FailingTimers(DateTimeOffset start) : FakeTimeProvider(start)
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            throw new InvalidOperationException("injected timer failure");
    }

    private static readonly DateTimeOffset VirtualStart = new(2026, 11, 3, 4, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Runs a queue on a virtual clock, advancing it in one-minute steps. Timeouts fire only when virtual time
    /// passes their limit, so the results do not depend on how fast or busy the test machine is.
    /// </summary>
    private async Task<(QueueResult Result, FakeTimeProvider Time)> RunOnVirtualClockAsync(IReadOnlyList<QueueItem> items)
    {
        var time = new FakeTimeProvider(VirtualStart);
        var queue = new QueueScheduler(_journal, new NoProcessSupervisor(), Fast, time).RunAsync(items);
        // Time stays frozen until the first engine is running, so its limits are measured from a known instant.
        await WaitUntilAsync(() => queue.IsCompleted || _journal.AnyInPhase(RunPhase.Running));
        for (var step = 0; step < 24 * 60 && !queue.IsCompleted; step++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(3); // let the scheduler react before time moves on
        }
        return (await queue.WaitAsync(TimeSpan.FromSeconds(10)), time);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? limit = null)
    {
        var clock = Stopwatch.StartNew();
        var max = limit ?? TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (clock.Elapsed > max)
            {
                if (limit is null) throw new TimeoutException("Condition was not reached.");
                return;
            }
            await Task.Delay(5);
        }
    }
}
