using System.Threading.Channels;

namespace VIvian.Core.Execution;

/// <summary>Per-run time limits. The stall timeout restarts whenever the run reports an event.</summary>
public sealed record RunLimits
{
    public static readonly TimeSpan Maximum = TimeSpan.FromHours(24);

    public RunLimits(TimeSpan TaskTimeout, TimeSpan StallTimeout)
    {
        this.TaskTimeout = Validate(TaskTimeout, nameof(TaskTimeout));
        this.StallTimeout = Validate(StallTimeout, nameof(StallTimeout));
    }

    public TimeSpan TaskTimeout { get; }
    public TimeSpan StallTimeout { get; }

    private static TimeSpan Validate(TimeSpan value, string name) =>
        value > TimeSpan.Zero && value <= Maximum ? value
            : throw new ArgumentOutOfRangeException(name, value, "时限必须大于 0 且不超过 24 小时。");
}

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
    private StopSignal _stop = new();
    private int _running;

    /// <summary>Raised on a background thread whenever a run changes phase, outcome or cleanup state.</summary>
    public event Action<RunUpdate>? RunChanged;
    /// <summary>Raised on a background thread for each event that belongs to the current run.</summary>
    public event Action<RunEvent>? EventReceived;

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>
    /// Requests an immediate stop of the queue that is currently running. No countdown; safe from any thread.
    /// A stop pressed just before <see cref="RunAsync"/> has begun may miss that queue; callers that start a queue
    /// asynchronously (e.g. from a UI) should stop it by cancelling the token passed to <see cref="RunAsync"/>,
    /// which also works when cancelled before the queue has started.
    /// </summary>
    public void EmergencyStop() => Volatile.Read(ref _stop).Trigger();

    /// <summary>
    /// One queue's stop request. <see cref="Trigger"/> cancels the adapters' token synchronously, so the stop is
    /// observable when it returns — including tokens linked to it (e.g. a call's own time-limit token). Registered
    /// callbacks therefore run on the caller's thread; a UI should call <see cref="EmergencyStop"/> or cancel the
    /// RunAsync token off its UI thread so a slow adapter callback cannot freeze it.
    /// </summary>
    private sealed class StopSignal : IDisposable
    {
        private readonly CancellationTokenSource _token = new();
        private readonly TaskCompletionSource _signal = NewSignal();
        private int _disposed;

        public Task Requested => _signal.Task;
        public CancellationToken Token => _token.Token;

        public void Trigger()
        {
            // The scheduler's own stop path (escalation, grace timers) is released first, so an adapter
            // cancellation callback that blocks below can never hide or delay the emergency stop.
            _signal.TrySetResult();
            if (Volatile.Read(ref _disposed) == 0)
            {
                // Not CancelAsync: that would also defer cancellation of linked tokens to the thread pool.
                try { _token.Cancel(); }
                catch (ObjectDisposedException) { /* the queue already finished */ }
                catch (AggregateException) { /* an adapter callback failed; the token is cancelled regardless */ }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _token.Dispose();
        }
    }

    public async Task<QueueResult> RunAsync(IReadOnlyList<QueueItem> items, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Select(i => i.Game).Distinct().Count() != items.Count)
            throw new ArgumentException("每款游戏在一个队列中只能出现一次。", nameof(items));
        if (Interlocked.CompareExchange(ref _running, 1, 0) == 1)
            throw new InvalidOperationException("已有队列在运行。");
        try
        {
            using var stopSignal = new StopSignal();
            Volatile.Write(ref _stop, stopSignal);
            var emergency = stopSignal.Requested;
            // Cancelling the caller's token (e.g. application shutdown) is an emergency stop; if it is already
            // cancelled, Register runs the trigger synchronously here, before anything starts.
            using var registration = ct.Register(stopSignal.Trigger);
            var queueId = Guid.NewGuid();
            // A previous run may still own processes (e.g. after a crash). Nothing starts until the user resolves it.
            int unresolved;
            try { unresolved = await _journal.CountUnresolvedAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { unresolved = -1; }
            if (unresolved != 0)
                return new QueueResult(queueId, QueueStatus.Blocked, [], items.Select(x => x.Game).ToList(),
                    unresolved < 0, unresolved < 0 ? ReasonCodes.JournalUnavailable : ReasonCodes.UnresolvedRuns);
            var runs = new List<RunUpdate>();
            var journalFaulted = false;
            var status = QueueStatus.Completed;
            var notStarted = new List<GameId>();
            for (var i = 0; i < items.Count; i++)
            {
                if (emergency.IsCompleted)
                {
                    status = QueueStatus.Cancelled;
                    notStarted.AddRange(items.Skip(i).Select(x => x.Game));
                    break;
                }
                var execution = new RunExecution(this, queueId, items[i], emergency, stopSignal.Token);
                RunUpdate last;
                bool blocksQueue;
                try
                {
                    (last, blocksQueue) = await execution.ExecuteAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // A bug must never leave a run open: close it as blocked with cleanup unconfirmed.
                    (last, blocksQueue) = (await execution.AbortAsync().ConfigureAwait(false), true);
                }
                journalFaulted |= execution.JournalFaulted;
                runs.Add(last);
                var stop = blocksQueue ? QueueStatus.Blocked
                    : last.Outcome == RunOutcome.Cancelled || emergency.IsCompleted ? QueueStatus.Cancelled
                    : (QueueStatus?)null;
                if (stop is { } s)
                {
                    status = s;
                    notStarted.AddRange(items.Skip(i + 1).Select(x => x.Game));
                    break;
                }
            }
            return new QueueResult(queueId, status, runs, notStarted, journalFaulted,
                status != QueueStatus.Blocked ? null
                : runs[^1].Cleanup == CleanupState.Unconfirmed ? ReasonCodes.CleanupUnconfirmed : runs[^1].ReasonCode);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>Events waiting to be stored per run; beyond this, output is dropped and the drop is recorded.</summary>
    public const int MaxBufferedEvents = 10_000;
    private const int MaxEventBatch = 500;
    private static readonly TimeSpan EventFlushTimeout = TimeSpan.FromSeconds(30);

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
            await PublishAsync().ConfigureAwait(false);

            if (emergency.IsCompleted) return await CancelBeforeStartAsync().ConfigureAwait(false);
            _sm.MoveTo(RunPhase.Preflight);
            await PublishAsync().ConfigureAwait(false);

            PreflightResult preflight;
            try { preflight = await Adapter.PreflightAsync(context, stopToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (emergency.IsCompleted) { return await CancelBeforeStartAsync().ConfigureAwait(false); }
            catch (Exception) { preflight = PreflightResult.Block(ReasonCodes.PreflightError, "预检出错。"); }
            if (!preflight.CanStart)
            {
                _sm.SetBlockReason(preflight.ReasonCode ?? ReasonCodes.PreflightError);
                _sm.MoveTo(RunPhase.Blocked);
                return (await PublishAsync().ConfigureAwait(false), preflight.BlocksQueue);
            }
            if (emergency.IsCompleted) return await CancelBeforeStartAsync().ConfigureAwait(false);

            // The Starting record must be durable before any process exists, so a crash can be reconciled.
            try
            {
                await owner._journal.PublishAsync(
                    Snapshot() with { Phase = RunPhase.Starting, Cleanup = CleanupState.Pending }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                JournalFaulted = true;
                _sm.SetBlockReason(ReasonCodes.JournalUnavailable);
                _sm.MoveTo(RunPhase.Blocked);
                return (await PublishAsync().ConfigureAwait(false), true);
            }
            _sm.MoveTo(RunPhase.Starting);
            owner.Raise(Snapshot());

            IEngineRun run;
            try
            {
                run = await Adapter.StartAsync(context, owner._supervisor, stopToken).ConfigureAwait(false);
            }
            catch (EngineStartException ex)
            {
                var userStop = emergency.IsCompleted;
                return await FinishWithoutRunAsync(userStop ? RunOutcome.Cancelled : RunOutcome.Failed,
                    userStop ? ReasonCodes.UserStop : ex.ReasonCode,
                    ex.NothingStarted ? CleanupState.NotRequired : CleanupState.Unconfirmed).ConfigureAwait(false);
            }
            catch (Exception)
            {
                var userStop = emergency.IsCompleted;
                return await FinishWithoutRunAsync(userStop ? RunOutcome.Cancelled : RunOutcome.Failed,
                    userStop ? ReasonCodes.UserStop : ReasonCodes.AdapterError, CleanupState.Unconfirmed).ConfigureAwait(false);
            }

            try
            {
                return await SuperviseAsync(run).ConfigureAwait(false);
            }
            finally
            {
                // Disposal releases handles; whether processes are gone was already decided by reconciliation.
                try { await run.DisposeAsync().ConfigureAwait(false); }
                catch (Exception) { /* never let disposal change the recorded result */ }
            }
        }

        /// <summary>Closes the run after an unexpected internal error, without depending on the clock or adapter.</summary>
        public async Task<RunUpdate> AbortAsync()
        {
            var started = _sm.Phase is not (RunPhase.Pending or RunPhase.Preflight);
            RunUpdate update;
            try
            {
                update = new RunUpdate(_runId, queueId, item.Game, Adapter.RuleVersion, Adapter.IsSimulation,
                    RunPhase.Blocked, _sm.Outcome ?? (started ? RunOutcome.Indeterminate : null),
                    started ? CleanupState.Unconfirmed : CleanupState.NotRequired, ReasonCodes.InternalError, DateTimeOffset.UtcNow);
            }
            catch (Exception)
            {
                update = new RunUpdate(_runId, queueId, item.Game, "unknown", true, RunPhase.Blocked,
                    started ? RunOutcome.Indeterminate : null, started ? CleanupState.Unconfirmed : CleanupState.NotRequired,
                    ReasonCodes.InternalError, DateTimeOffset.UtcNow);
            }
            try { await owner._journal.PublishAsync(update, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { JournalFaulted = true; }
            owner.Raise(update);
            return update;
        }

        private async Task<(RunUpdate, bool)> SuperviseAsync(IEngineRun run)
        {
            _sm.MoveTo(RunPhase.Running);
            await PublishAsync().ConfigureAwait(false);
            Interlocked.Exchange(ref _lastActivity, Time.GetTimestamp());
            var limits = item.Limits ?? Options.DefaultLimits;

            using var watchCts = new CancellationTokenSource();
            var pump = PumpEventsAsync(run, watchCts.Token);
            var verdictTask = WaitVerdictSafeAsync(run);
            var timeoutTask = Task.Delay(limits.TaskTimeout, Time, watchCts.Token);
            var stallTask = StallWatchAsync(limits.StallTimeout, watchCts.Token);

            var winner = await Task.WhenAny(verdictTask, timeoutTask, stallTask, emergency).ConfigureAwait(false);
            RunOutcome outcome;
            string reason;
            if (winner == verdictTask)
            {
                (outcome, reason) = Judge(await verdictTask.ConfigureAwait(false));
            }
            else
            {
                var stopReason = winner == emergency ? ReasonCodes.UserStop
                    : winner == timeoutTask ? ReasonCodes.TaskTimeout : ReasonCodes.Stalled;
                var mode = winner == emergency ? StopMode.Emergency : StopMode.Graceful;
                _sm.MoveTo(RunPhase.Stopping);
                // The stop request goes out before anything is written, so an emergency stop is never delayed by I/O.
                var firstRequest = RequestStopSafeAsync(run, mode);
                await PublishAsync().ConfigureAwait(false);
                await firstRequest.ConfigureAwait(false);
                var ended = await StopAsync(run, verdictTask, mode).ConfigureAwait(false);
                if (emergency.IsCompleted) (outcome, reason) = (RunOutcome.Cancelled, ReasonCodes.UserStop);
                else if (ended && (await verdictTask.ConfigureAwait(false)).Outcome == RunOutcome.Failed)
                    (outcome, reason) = (RunOutcome.Failed, (await verdictTask.ConfigureAwait(false)).ReasonCode);
                else (outcome, reason) = (RunOutcome.Indeterminate, stopReason);
            }
            _sm.SetOutcome(outcome, reason);
            _sm.MoveTo(RunPhase.Reconciling);
            await PublishAsync().ConfigureAwait(false);

            CleanupState cleanup;
            try
            {
                using var reconcileCts = new CancellationTokenSource(Options.ReconcileTimeout, Time);
                cleanup = await run.ReconcileAsync(reconcileCts.Token).ConfigureAwait(false);
                if (cleanup is CleanupState.Pending or CleanupState.NotRequired) cleanup = CleanupState.Unconfirmed;
            }
            catch (Exception)
            {
                cleanup = CleanupState.Unconfirmed;
            }
            // Stop reading engine output (prompt), then let already-buffered events reach storage so the record is
            // complete when the run is reported finished. Bounded, in case an adapter's event stream misbehaves.
            watchCts.Cancel();
            await Task.WhenAny(pump, Task.Delay(EventFlushTimeout, Time)).ConfigureAwait(false);
            return await FinishAsync(cleanup).ConfigureAwait(false);
        }

        /// <summary>Applies the evidence rule: success only with explicit completion evidence for this run.</summary>
        private (RunOutcome, string) Judge(RunVerdict verdict)
        {
            if (verdict.Outcome == RunOutcome.Success)
            {
                var ownKinds = verdict.Evidence.Where(e => e.RunId == _runId && e.Kind != EvidenceKind.Note)
                    .Select(e => e.Kind).Distinct().ToArray();
                if (ownKinds.Length > 1) return (RunOutcome.Indeterminate, ReasonCodes.ConflictingEvidence);
                if (!ownKinds.Contains(EvidenceKind.CompletionReported))
                    return (RunOutcome.Indeterminate, ReasonCodes.SuccessWithoutEvidence);
            }
            return (verdict.Outcome, verdict.ReasonCode);
        }

        /// <summary>After the first stop request was sent, returns true when the engine has ended.
        /// Graceful escalates to emergency on timeout or when the user requests an emergency stop.</summary>
        private async Task<bool> StopAsync(IEngineRun run, Task<RunVerdict> verdictTask, StopMode mode)
        {
            if (mode == StopMode.Graceful)
            {
                var graceful = Task.Delay(Options.GracefulStopTimeout, Time);
                await Task.WhenAny(verdictTask, graceful, emergency).ConfigureAwait(false);
                if (verdictTask.IsCompleted) return true;
                await RequestStopSafeAsync(run, StopMode.Emergency).ConfigureAwait(false);
            }
            await Task.WhenAny(verdictTask, Task.Delay(Options.EmergencyStopTimeout, Time)).ConfigureAwait(false);
            return verdictTask.IsCompleted;
        }

        private async Task RequestStopSafeAsync(IEngineRun run, StopMode mode)
        {
            try
            {
                using var cts = new CancellationTokenSource(Options.EmergencyStopTimeout, Time);
                await run.RequestStopAsync(mode, cts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Whether the engine really stopped is decided by the verdict and reconciliation, not by this call.
            }
        }

        private async Task<RunVerdict> WaitVerdictSafeAsync(IEngineRun run)
        {
            try { return await run.WaitForVerdictAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { return new RunVerdict(RunOutcome.Indeterminate, ReasonCodes.AdapterError, []); }
        }

        private async Task PumpEventsAsync(IEngineRun run, CancellationToken ct)
        {
            // Events are handed to a single batching writer: storage latency can never hold up the watchdogs or
            // an emergency stop, and a flood of output costs at most one blocked thread.
            var buffer = Channel.CreateBounded<RunEvent>(new BoundedChannelOptions(MaxBufferedEvents)
            { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
            var writer = WriteEventsAsync(buffer.Reader);
            var dropped = 0;
            try
            {
                await foreach (var runEvent in run.Events.WithCancellation(ct).ConfigureAwait(false))
                {
                    // Events from other runs (old logs, other instances) are ignored entirely.
                    if (runEvent.RunId != _runId) continue;
                    Interlocked.Exchange(ref _lastActivity, Time.GetTimestamp());
                    if (!buffer.Writer.TryWrite(runEvent)) dropped++;
                    try { owner.EventReceived?.Invoke(runEvent); } catch (Exception) { /* observers never break safety */ }
                }
            }
            catch (Exception)
            {
                // Event stream errors do not decide the outcome; the verdict and reconciliation do.
            }
            finally
            {
                if (dropped > 0)
                    buffer.Writer.TryWrite(new RunEvent(_runId, Time.GetUtcNow(), RunEventKind.Warning,
                        $"输出过快，已丢弃 {dropped} 条记录。"));
                buffer.Writer.TryComplete();
            }
            await writer.ConfigureAwait(false);
        }

        private async Task WriteEventsAsync(ChannelReader<RunEvent> reader)
        {
            var batch = new List<RunEvent>(MaxEventBatch);
            try
            {
                while (await reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    batch.Clear();
                    while (batch.Count < MaxEventBatch && reader.TryRead(out var item)) batch.Add(item);
                    try { await owner._journal.AppendEventsAsync(batch.ToArray(), CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception) { JournalFaulted = true; }
                }
            }
            catch (Exception)
            {
                JournalFaulted = true;
            }
        }

        private async Task StallWatchAsync(TimeSpan stall, CancellationToken ct)
        {
            while (true)
            {
                var idle = Time.GetElapsedTime(Interlocked.Read(ref _lastActivity));
                if (idle >= stall) return;
                await Task.Delay(stall - idle, Time, ct).ConfigureAwait(false);
            }
        }

        private async Task<(RunUpdate, bool)> CancelBeforeStartAsync()
        {
            _sm.SetOutcome(RunOutcome.Cancelled, ReasonCodes.UserStop);
            _sm.MoveTo(RunPhase.Finished);
            return (await PublishAsync().ConfigureAwait(false), false);
        }

        private async Task<(RunUpdate, bool)> FinishWithoutRunAsync(RunOutcome outcome, string reason, CleanupState cleanup)
        {
            _sm.SetOutcome(outcome, reason);
            _sm.MoveTo(RunPhase.Reconciling);
            await PublishAsync().ConfigureAwait(false);
            return await FinishAsync(cleanup).ConfigureAwait(false);
        }

        private async Task<(RunUpdate, bool)> FinishAsync(CleanupState cleanup)
        {
            _sm.SetCleanup(cleanup);
            var confirmed = cleanup is CleanupState.Confirmed or CleanupState.NotRequired;
            _sm.MoveTo(confirmed ? RunPhase.Finished : RunPhase.Blocked);
            return (await PublishAsync().ConfigureAwait(false), !confirmed);
        }

        private async Task<RunUpdate> PublishAsync()
        {
            var update = Snapshot();
            try { await owner._journal.PublishAsync(update, CancellationToken.None).ConfigureAwait(false); }
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
