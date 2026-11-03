namespace VIvian.Core.Execution;

/// <summary>Per-run time limits. The stall timeout restarts whenever the run reports an event.</summary>
public sealed record RunLimits(TimeSpan TaskTimeout, TimeSpan StallTimeout);

public sealed record SchedulerOptions(
    TimeSpan GracefulStopTimeout, TimeSpan EmergencyStopTimeout, TimeSpan ReconcileTimeout, RunLimits DefaultLimits)
{
    public static SchedulerOptions Default { get; } = new(
        TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30),
        new RunLimits(TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(10)));
}

/// <summary>
/// Runs queued games strictly one after another.
/// Safety rules (M2 plan §2.1): the next game starts only after the previous run's cleanup is confirmed;
/// an emergency stop is sent immediately with no countdown; uncertain runs are never retried;
/// success needs explicit completion evidence for the current RunId.
/// </summary>
public sealed class QueueScheduler(
    IRunJournal journal, IProcessSupervisor supervisor, SchedulerOptions? options = null, TimeProvider? time = null)
{
    private readonly SchedulerOptions _options = options ?? SchedulerOptions.Default;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private TaskCompletionSource _emergency = NewSignal();
    private int _running;

    /// <summary>Raised on a background thread whenever a run changes phase, outcome or cleanup state.</summary>
    public event Action<RunUpdate>? RunChanged;
    /// <summary>Raised on a background thread for each event that belongs to the current run.</summary>
    public event Action<RunEvent>? EventReceived;

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>Requests an immediate stop of the running queue. No countdown; safe to call from any thread.</summary>
    public void EmergencyStop() => Volatile.Read(ref _emergency).TrySetResult();

    public async Task<QueueResult> RunAsync(IReadOnlyList<QueueItem> items, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Select(i => i.Game).Distinct().Count() != items.Count)
            throw new ArgumentException("每款游戏在一个队列中只能出现一次。", nameof(items));
        if (Interlocked.Exchange(ref _running, 1) == 1)
            throw new InvalidOperationException("已有队列在运行。");
        var emergency = NewSignal();
        Volatile.Write(ref _emergency, emergency);
        // Cancelling the caller's token (e.g. application shutdown) is treated as an emergency stop.
        await using var registration = ct.Register(() => emergency.TrySetResult());
        using var stopToken = new CancellationTokenSource();
        _ = emergency.Task.ContinueWith(_ =>
        {
            try { stopToken.Cancel(); } catch (ObjectDisposedException) { /* queue already finished */ }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            var queueId = Guid.NewGuid();
            var runs = new List<RunUpdate>();
            var journalFaulted = false;
            var status = QueueStatus.Completed;
            var notStarted = new List<GameId>();
            for (var i = 0; i < items.Count; i++)
            {
                if (emergency.Task.IsCompleted)
                {
                    status = QueueStatus.Cancelled;
                    notStarted.AddRange(items.Skip(i).Select(x => x.Game));
                    break;
                }
                var execution = new RunExecution(this, queueId, items[i], emergency.Task, stopToken.Token);
                var (last, blocksQueue) = await execution.ExecuteAsync();
                journalFaulted |= execution.JournalFaulted;
                runs.Add(last);
                var stop = blocksQueue ? QueueStatus.Blocked
                    : last.Outcome == RunOutcome.Cancelled || emergency.Task.IsCompleted ? QueueStatus.Cancelled
                    : (QueueStatus?)null;
                if (stop is { } s)
                {
                    status = s;
                    notStarted.AddRange(items.Skip(i + 1).Select(x => x.Game));
                    break;
                }
            }
            return new QueueResult(queueId, status, runs, notStarted, journalFaulted);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>State of one run inside a queue.</summary>
    private sealed class RunExecution(QueueScheduler owner, Guid queueId, QueueItem item, Task emergency, CancellationToken stopToken)
    {
        private readonly RunStateMachine _sm = new();
        private readonly Guid _runId = Guid.NewGuid();
        private long _lastActivity;
        public bool JournalFaulted { get; private set; }

        private IGameAdapter Adapter => item.Adapter;
        private SchedulerOptions Options => owner._options;
        private TimeProvider Time => owner._time;

        private RunUpdate Snapshot() => new(_runId, queueId, item.Game, Adapter.RuleVersion, Adapter.IsSimulation,
            _sm.Phase, _sm.Outcome, _sm.Cleanup, _sm.ReasonCode, Time.GetUtcNow());

        public async Task<(RunUpdate Last, bool BlocksQueue)> ExecuteAsync()
        {
            var context = new RunContext(_runId, queueId, item.Game, Time.GetUtcNow());
            await PublishAsync();

            if (emergency.IsCompleted) return await CancelBeforeStartAsync();
            _sm.MoveTo(RunPhase.Preflight);
            await PublishAsync();

            PreflightResult preflight;
            try { preflight = await Adapter.PreflightAsync(context, stopToken); }
            catch (OperationCanceledException) when (emergency.IsCompleted) { return await CancelBeforeStartAsync(); }
            catch (Exception) { preflight = PreflightResult.Block(ReasonCodes.PreflightError, "预检出错。"); }
            if (!preflight.CanStart)
            {
                _sm.SetBlockReason(preflight.ReasonCode ?? ReasonCodes.PreflightError);
                _sm.MoveTo(RunPhase.Blocked);
                return (await PublishAsync(), preflight.BlocksQueue);
            }
            if (emergency.IsCompleted) return await CancelBeforeStartAsync();

            // The Starting record must be durable before any process exists, so a crash can be reconciled.
            try
            {
                await owner._journal.PublishAsync(
                    Snapshot() with { Phase = RunPhase.Starting, Cleanup = CleanupState.Pending }, CancellationToken.None);
            }
            catch (Exception)
            {
                JournalFaulted = true;
                _sm.SetBlockReason(ReasonCodes.JournalUnavailable);
                _sm.MoveTo(RunPhase.Blocked);
                return (await PublishAsync(), true);
            }
            _sm.MoveTo(RunPhase.Starting);
            owner.Raise(Snapshot());

            IEngineRun run;
            try
            {
                run = await Adapter.StartAsync(context, owner._supervisor, stopToken);
            }
            catch (EngineStartException ex)
            {
                var userStop = emergency.IsCompleted;
                return await FinishWithoutRunAsync(userStop ? RunOutcome.Cancelled : RunOutcome.Failed,
                    userStop ? ReasonCodes.UserStop : ex.ReasonCode,
                    ex.NothingStarted ? CleanupState.NotRequired : CleanupState.Unconfirmed);
            }
            catch (Exception)
            {
                var userStop = emergency.IsCompleted;
                return await FinishWithoutRunAsync(userStop ? RunOutcome.Cancelled : RunOutcome.Failed,
                    userStop ? ReasonCodes.UserStop : ReasonCodes.AdapterError, CleanupState.Unconfirmed);
            }

            await using (run)
            {
                return await SuperviseAsync(run);
            }
        }

        private async Task<(RunUpdate, bool)> SuperviseAsync(IEngineRun run)
        {
            _sm.MoveTo(RunPhase.Running);
            await PublishAsync();
            Interlocked.Exchange(ref _lastActivity, Time.GetTimestamp());
            var limits = item.Limits ?? Options.DefaultLimits;

            using var watchCts = new CancellationTokenSource();
            var pump = PumpEventsAsync(run, watchCts.Token);
            var verdictTask = WaitVerdictSafeAsync(run);
            var timeoutTask = Task.Delay(limits.TaskTimeout, Time, watchCts.Token);
            var stallTask = StallWatchAsync(limits.StallTimeout, watchCts.Token);

            var winner = await Task.WhenAny(verdictTask, timeoutTask, stallTask, emergency);
            RunOutcome outcome;
            string reason;
            if (winner == verdictTask)
            {
                (outcome, reason) = Judge(await verdictTask);
            }
            else
            {
                var stopReason = winner == emergency ? ReasonCodes.UserStop
                    : winner == timeoutTask ? ReasonCodes.TaskTimeout : ReasonCodes.Stalled;
                var mode = winner == emergency ? StopMode.Emergency : StopMode.Graceful;
                _sm.MoveTo(RunPhase.Stopping);
                // The stop request goes out before anything is written, so an emergency stop is never delayed by I/O.
                var firstRequest = RequestStopSafeAsync(run, mode);
                await PublishAsync();
                await firstRequest;
                var ended = await StopAsync(run, verdictTask, mode);
                if (emergency.IsCompleted) (outcome, reason) = (RunOutcome.Cancelled, ReasonCodes.UserStop);
                else if (ended && (await verdictTask).Outcome == RunOutcome.Failed)
                    (outcome, reason) = (RunOutcome.Failed, (await verdictTask).ReasonCode);
                else (outcome, reason) = (RunOutcome.Indeterminate, stopReason);
            }
            _sm.SetOutcome(outcome, reason);
            _sm.MoveTo(RunPhase.Reconciling);
            await PublishAsync();

            CleanupState cleanup;
            try
            {
                using var reconcileCts = new CancellationTokenSource(Options.ReconcileTimeout, Time);
                cleanup = await run.ReconcileAsync(reconcileCts.Token);
                if (cleanup is CleanupState.Pending or CleanupState.NotRequired) cleanup = CleanupState.Unconfirmed;
            }
            catch (Exception)
            {
                cleanup = CleanupState.Unconfirmed;
            }
            watchCts.Cancel();
            await Task.WhenAny(pump, Task.Delay(TimeSpan.FromSeconds(2), Time));
            return await FinishAsync(cleanup);
        }

        /// <summary>Applies the evidence rule: success only with explicit completion evidence for this run.</summary>
        private (RunOutcome, string) Judge(RunVerdict verdict)
        {
            if (verdict.Outcome == RunOutcome.Success
                && !verdict.Evidence.Any(e => e.RunId == _runId && e.Kind == EvidenceKind.CompletionReported))
                return (RunOutcome.Indeterminate, ReasonCodes.SuccessWithoutEvidence);
            return (verdict.Outcome, verdict.ReasonCode);
        }

        /// <summary>After the first stop request was sent, returns true when the engine has ended.
        /// Graceful escalates to emergency on timeout or when the user requests an emergency stop.</summary>
        private async Task<bool> StopAsync(IEngineRun run, Task<RunVerdict> verdictTask, StopMode mode)
        {
            if (mode == StopMode.Graceful)
            {
                var graceful = Task.Delay(Options.GracefulStopTimeout, Time);
                await Task.WhenAny(verdictTask, graceful, emergency);
                if (verdictTask.IsCompleted) return true;
                await RequestStopSafeAsync(run, StopMode.Emergency);
            }
            await Task.WhenAny(verdictTask, Task.Delay(Options.EmergencyStopTimeout, Time));
            return verdictTask.IsCompleted;
        }

        private async Task RequestStopSafeAsync(IEngineRun run, StopMode mode)
        {
            try
            {
                using var cts = new CancellationTokenSource(Options.EmergencyStopTimeout, Time);
                await run.RequestStopAsync(mode, cts.Token);
            }
            catch (Exception)
            {
                // Whether the engine really stopped is decided by the verdict and reconciliation, not by this call.
            }
        }

        private async Task<RunVerdict> WaitVerdictSafeAsync(IEngineRun run)
        {
            try { return await run.WaitForVerdictAsync(CancellationToken.None); }
            catch (Exception) { return new RunVerdict(RunOutcome.Indeterminate, ReasonCodes.AdapterError, []); }
        }

        private async Task PumpEventsAsync(IEngineRun run, CancellationToken ct)
        {
            try
            {
                await foreach (var runEvent in run.Events.WithCancellation(ct))
                {
                    // Events from other runs (old logs, other instances) are ignored entirely.
                    if (runEvent.RunId != _runId) continue;
                    Interlocked.Exchange(ref _lastActivity, Time.GetTimestamp());
                    try { await owner._journal.AppendEventAsync(runEvent, CancellationToken.None); }
                    catch (Exception) { JournalFaulted = true; }
                    try { owner.EventReceived?.Invoke(runEvent); } catch (Exception) { /* observers never break safety */ }
                }
            }
            catch (Exception)
            {
                // Event stream errors do not decide the outcome; the verdict and reconciliation do.
            }
        }

        private async Task StallWatchAsync(TimeSpan stall, CancellationToken ct)
        {
            while (true)
            {
                var idle = Time.GetElapsedTime(Interlocked.Read(ref _lastActivity));
                if (idle >= stall) return;
                await Task.Delay(stall - idle, Time, ct);
            }
        }

        private async Task<(RunUpdate, bool)> CancelBeforeStartAsync()
        {
            _sm.SetOutcome(RunOutcome.Cancelled, ReasonCodes.UserStop);
            _sm.MoveTo(RunPhase.Finished);
            return (await PublishAsync(), false);
        }

        private async Task<(RunUpdate, bool)> FinishWithoutRunAsync(RunOutcome outcome, string reason, CleanupState cleanup)
        {
            _sm.SetOutcome(outcome, reason);
            _sm.MoveTo(RunPhase.Reconciling);
            await PublishAsync();
            return await FinishAsync(cleanup);
        }

        private async Task<(RunUpdate, bool)> FinishAsync(CleanupState cleanup)
        {
            _sm.SetCleanup(cleanup);
            var confirmed = cleanup is CleanupState.Confirmed or CleanupState.NotRequired;
            _sm.MoveTo(confirmed ? RunPhase.Finished : RunPhase.Blocked);
            return (await PublishAsync(), !confirmed);
        }

        private async Task<RunUpdate> PublishAsync()
        {
            var update = Snapshot();
            try { await owner._journal.PublishAsync(update, CancellationToken.None); }
            catch (Exception) { JournalFaulted = true; }
            owner.Raise(update);
            return update;
        }
    }

    private readonly IRunJournal _journal = journal;
    private readonly IProcessSupervisor _supervisor = supervisor;

    private void Raise(RunUpdate update)
    {
        try { RunChanged?.Invoke(update); } catch (Exception) { /* observers never break safety */ }
    }
}
