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
    /// <summary>Scheduler-owned limit for <see cref="IGameAdapter.PreflightAsync"/> (#28).</summary>
    public TimeSpan PreflightTimeout { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>Scheduler-owned limit for <see cref="IGameAdapter.StartAsync"/> (#28).</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(120);
    /// <summary>Scheduler-owned limit for disposing a run (#28).</summary>
    public TimeSpan DisposeTimeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Scheduler-owned limit for journal gate queries and abandoned-call audit writes.</summary>
    public TimeSpan JournalTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public static SchedulerOptions Default { get; } = new(
        TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30),
        new RunLimits(TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(10)));
}

/// <summary>
/// Runs queued games strictly one after another.
/// Safety rules (M2 plan §2.1): the next game starts only after the previous run's cleanup is confirmed;
/// an emergency stop is sent immediately with no countdown; uncertain runs are never retried;
/// success needs explicit completion evidence for the current RunId.
/// Every adapter call is bounded by a scheduler-owned limit (#28): an adapter that ignores cancellation or never
/// returns cannot keep the queue running. An abandoned call is not assumed to have ended; it holds the
/// <see cref="SessionHazards"/> barrier until it actually returns, and its run ends with cleanup Unconfirmed.
/// </summary>
public sealed class QueueScheduler(
    IRunJournal journal, IProcessSupervisor supervisor, SchedulerOptions? options = null, TimeProvider? time = null,
    Func<IGameAdapter, bool>? admitReal = null)
{
    private readonly SchedulerOptions _options = options ?? SchedulerOptions.Default;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private StopSignal _stop = new();
    private int _running;
    private readonly SafetyBarrier _barrier = new();
    private readonly JournalWriter _writer = new(journal);
    // Default is closed. The application may explicitly admit one reviewed adapter in a future build.
    private readonly Func<IGameAdapter, bool> _admitReal = admitReal ?? (_ => false);

    /// <summary>State writes queued or running; for deterministic tests that drive a virtual clock.</summary>
    internal int StateWritesInFlight => _writer.InFlight;

    /// <summary>A state write's bounded wait has given up and the write has not completed.</summary>
    internal bool JournalStalled => _writer.IsStalled;

    /// <summary>Raised on a background thread whenever a run changes phase, outcome or cleanup state.</summary>
    public event Action<RunUpdate>? RunChanged;
    /// <summary>Raised on a background thread for each event that belongs to the current run.</summary>
    public event Action<RunEvent>? EventReceived;

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>
    /// Runs that, in this session, may still occupy the machine. While this is not empty, no queue starts — even if
    /// the database could not record the problem (#29) or an abandoned adapter call has not returned yet (#28).
    /// </summary>
    public IReadOnlyList<SessionHazard> SessionHazards => _barrier.Snapshot();

    /// <summary>Raised on a background thread when <see cref="SessionHazards"/> changes.</summary>
    public event Action? HazardsChanged
    {
        add => _barrier.Changed += value;
        remove => _barrier.Changed -= value;
    }

    /// <summary>
    /// The user confirms that a run whose cleanup was unconfirmed no longer occupies the machine. Refused while an
    /// abandoned adapter call of that run is still running. If the run's final state never reached the journal it is
    /// written first; the acknowledgement is stored by <paramref name="history"/> and never rewrites the verdict.
    /// The in-session barrier is released only after the acknowledgement was stored.
    /// </summary>
    public async Task AcknowledgeAsync(Guid runId, string note, IRunHistory history, DateTimeOffset at, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (_barrier.Find(runId) is { } hazard)
        {
            if (hazard.PendingOperations > 0 || hazard.PersistencePending > 0 || hazard.RequiresVerifiedRecovery)
                throw new RunStillActiveException();
            var persisted = hazard.TerminalPersisted;
            if (!persisted && hazard.LastKnown is { } last)
            {
                // Through the serialized writer, bounded: it can never overtake or be overtaken by another state write.
                var write = _writer.Enqueue(last);
                try
                {
                    await write.WaitAsync(_options.JournalTimeout, _time, ct).ConfigureAwait(false);
                    _barrier.MarkPersisted(runId);
                    persisted = true;
                }
                catch (Exception)
                {
                    write.ObserveLater();
                    // The acknowledgement below fails as well if the record is still missing; the barrier then stays.
                }
            }
            // Cleanup was confirmed by the run itself and its final state is stored: only the in-session gate (set
            // because persistence was uncertain) remains, and there is no stored cleanup to acknowledge.
            if (persisted && hazard.LastKnown is { Cleanup: not CleanupState.Unconfirmed, Phase: RunPhase.Finished or RunPhase.Blocked })
            {
                if (!_barrier.TryResolve(runId)) throw new RunStillActiveException();
                return;
            }
        }
        try
        {
            await history.AcknowledgeAsync(runId, note, at, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException) when (_barrier.Find(runId) is null)
        {
            // M-30-1: a row left open by an earlier session counts as unresolved (it blocks queues) but cannot be
            // acknowledged while it is open. Close interrupted rows (never as success: started runs become Blocked
            // with cleanup Unconfirmed) while no queue can start, then try once more.
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) throw new RunStillActiveException();
            try
            {
                await history.ReconcileInterruptedRunsAsync(at, ct).ConfigureAwait(false);
                await history.AcknowledgeAsync(runId, note, at, ct).ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _running, 0);
            }
        }
        if (!_barrier.TryResolve(runId)) throw new RunStillActiveException();
    }

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
        private int _requested;

        public Task Requested => _signal.Task;
        public CancellationToken Token => _token.Token;

        /// <summary>
        /// Set before anything else observes the stop (signal, token, callbacks). Safety gates — such as a run's
        /// launch permission — read this flag directly, so no adapter cancellation callback can delay them.
        /// </summary>
        public bool IsRequested => Volatile.Read(ref _requested) == 1;

        public void Trigger()
        {
            // 1. Safety gates first: from here on no gate that reads IsRequested admits new work.
            Interlocked.Exchange(ref _requested, 1);
            // 2. The scheduler's own stop path (escalation, grace timers) is released next, so an adapter
            //    cancellation callback that blocks below can never hide or delay the emergency stop.
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
        var reservedIds = items.Where(i => i.RunId.HasValue).Select(i => i.RunId!.Value).ToArray();
        if (reservedIds.Distinct().Count() != reservedIds.Length)
            throw new ArgumentException("运行编号不能重复。", nameof(items));
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
            // In-session barrier first: it does not depend on the database being writable.
            if (!_barrier.IsClear)
                return new QueueResult(queueId, QueueStatus.Blocked, [], items.Select(x => x.Game).ToList(), false,
                    _barrier.HasPendingPersistence ? ReasonCodes.JournalUnavailable
                    : _barrier.HasPendingOperations ? ReasonCodes.AdapterUnresponsive : ReasonCodes.UnresolvedRuns);
            // A previous run may still own processes (e.g. after a crash). Nothing starts until the user resolves it.
            int unresolved;
            try
            {
                using var queryCts = new CancellationTokenSource(_options.JournalTimeout, _time);
                var query = _journal.CountUnresolvedAsync(queryCts.Token);
                unresolved = await query.WaitAsync(_options.JournalTimeout, _time, CancellationToken.None).ConfigureAwait(false);
            }
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
                var execution = new RunExecution(this, queueId, items[i], emergency, stopSignal.Token, () => stopSignal.IsRequested);
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
    private static readonly TimeSpan EventDrainTimeout = TimeSpan.FromSeconds(2);

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>State of one run inside a queue.</summary>
    private sealed class RunExecution(QueueScheduler owner, Guid queueId, QueueItem item, Task emergency, CancellationToken stopToken,
        Func<bool> stopRequested)
    {
        private readonly RunStateMachine _sm = new();
        private readonly Guid _runId = item.RunId ?? Guid.NewGuid();
        private RunProcessScope? _processScope;
        private long _lastActivity;
        private bool _lastPublishSucceeded;
        private bool _abandoned;      // an adapter call of this run outlived its limit (#28)
        private bool _runDisposed;
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

            // No real adapter has passed process-ownership admission yet. A third-party adapter can
            // ignore our supervisor and launch on its own; the scoped gate cannot contain such code.
            if (!Adapter.IsSimulation && !owner._admitReal(Adapter))
            {
                _sm.SetBlockReason(ReasonCodes.RealAdapterUnverified);
                _sm.MoveTo(RunPhase.Blocked);
                return (await PublishAsync().ConfigureAwait(false), true);
            }

            var pre = await BoundAsync("PreflightAsync", t => Adapter.PreflightAsync(context, t),
                Options.PreflightTimeout, emergencyShortens: true).ConfigureAwait(false);
            if (pre.Status == BoundStatus.Abandoned) return await AbandonBeforeStartAsync().ConfigureAwait(false);
            if (pre.Status == BoundStatus.Faulted && pre.Error is OperationCanceledException && emergency.IsCompleted)
                return await CancelBeforeStartAsync().ConfigureAwait(false);
            var preflight = pre.Status == BoundStatus.Completed && pre.Value is { } value
                ? value : PreflightResult.Block(ReasonCodes.PreflightError, "预检出错。");
            if (!preflight.CanStart)
            {
                _sm.SetBlockReason(preflight.ReasonCode ?? ReasonCodes.PreflightError);
                _sm.MoveTo(RunPhase.Blocked);
                return (await PublishAsync().ConfigureAwait(false), preflight.BlocksQueue);
            }
            if (emergency.IsCompleted) return await CancelBeforeStartAsync().ConfigureAwait(false);

            // The Starting record must be durable before any process exists, so a crash can be reconciled.
            if (!await PersistAsync(Snapshot() with { Phase = RunPhase.Starting, Cleanup = CleanupState.Pending }).ConfigureAwait(false))
            {
                JournalFaulted = true;
                _sm.SetBlockReason(ReasonCodes.JournalUnavailable);
                _sm.MoveTo(RunPhase.Blocked);
                return (await PublishAsync().ConfigureAwait(false), true);
            }
            _sm.MoveTo(RunPhase.Starting);
            owner.Raise(Snapshot());

            // A run that arrives after the limit is still released (e.g. its job handle closed), but that is never
            // taken as proof that its processes have ended.
            // The scope reads the queue's stop flag itself: revocation never depends on a cancellation callback running.
            _processScope = new RunProcessScope(owner._supervisor, _runId, Time, stopRequested);
            using var stopRegistration = stopToken.Register(() => _processScope.RevokeLaunches());
            var started = await BoundAsync("StartAsync", t => Adapter.StartAsync(context, _processScope, t),
                Options.StartTimeout, emergencyShortens: true,
                onLateResult: async late => { if (late is not null) await late.DisposeAsync().ConfigureAwait(false); })
                .ConfigureAwait(false);
            var userStop = emergency.IsCompleted;
            if (started.Status == BoundStatus.Abandoned)
            {
                _processScope.RevokeLaunches();
                if (!await _processScope.RevokeAsync(Options.DisposeTimeout).ConfigureAwait(false))
                    owner._barrier.RequireVerifiedRecovery(_runId, item.Game);
                return await FinishWithoutRunAsync(userStop ? RunOutcome.Cancelled : RunOutcome.Indeterminate,
                    ReasonCodes.AdapterUnresponsive, CleanupState.Unconfirmed).ConfigureAwait(false);
            }
            if (started.Status == BoundStatus.Faulted)
            {
                _processScope.RevokeLaunches();
                if (!await _processScope.RevokeAsync(Options.DisposeTimeout).ConfigureAwait(false))
                    owner._barrier.RequireVerifiedRecovery(_runId, item.Game);
                return started.Error is EngineStartException ex
                    ? await FinishWithoutRunAsync(userStop ? RunOutcome.Cancelled : RunOutcome.Failed,
                        userStop ? ReasonCodes.UserStop : ex.ReasonCode,
                        ex.NothingStarted && !(_processScope.HasUnsafeLaunch) ? CleanupState.NotRequired : CleanupState.Unconfirmed).ConfigureAwait(false)
                    : await FinishWithoutRunAsync(userStop ? RunOutcome.Cancelled : RunOutcome.Failed,
                        userStop ? ReasonCodes.UserStop : ReasonCodes.AdapterError, CleanupState.Unconfirmed).ConfigureAwait(false);
            }
            if (started.Value is not { } run)
            {
                _processScope.RevokeLaunches();
                if (!await _processScope.RevokeAsync(Options.DisposeTimeout).ConfigureAwait(false))
                    owner._barrier.RequireVerifiedRecovery(_runId, item.Game);
                return await FinishWithoutRunAsync(RunOutcome.Failed, ReasonCodes.AdapterError, CleanupState.Unconfirmed)
                    .ConfigureAwait(false);
            }

            try
            {
                return await SuperviseAsync(run).ConfigureAwait(false);
            }
            finally
            {
                // Only reached without disposal on an unexpected internal error; AbortAsync then records Unconfirmed.
                if (!_runDisposed) await DisposeRunAsync(run).ConfigureAwait(false);
            }
        }

        /// <summary>Preflight did not return in time: the adapter may still be doing something, so nothing is assumed.</summary>
        private async Task<(RunUpdate, bool)> AbandonBeforeStartAsync()
        {
            if (emergency.IsCompleted) _sm.SetOutcome(RunOutcome.Cancelled, ReasonCodes.AdapterUnresponsive);
            else _sm.SetBlockReason(ReasonCodes.AdapterUnresponsive);
            _sm.SetCleanup(CleanupState.Unconfirmed);
            _sm.MoveTo(RunPhase.Blocked);
            // L-30-1: the barrier closes before the state becomes visible anywhere, so a user acknowledging the
            // published row can never race ahead of it.
            owner._barrier.MarkUnconfirmed(Snapshot(), persisted: false);
            var update = await PublishAsync().ConfigureAwait(false);
            owner._barrier.MarkUnconfirmed(update, _lastPublishSucceeded);
            return (update, true);
        }

        private async Task DisposeRunAsync(IEngineRun run)
        {
            _runDisposed = true;
            // Disposal releases handles; whether processes are gone is decided by reconciliation, not by this call.
            try
            {
                var disposed = await BoundAsync("DisposeAsync", async _ => { await run.DisposeAsync().ConfigureAwait(false); return true; },
                    Options.DisposeTimeout, emergencyShortens: false).ConfigureAwait(false);
                if (disposed.Status != BoundStatus.Completed)
                    owner._barrier.RequireVerifiedRecovery(_runId, item.Game);
            }
            catch (Exception)
            {
                // Bound setup failed before it launched the call. Try disposal once, track it,
                // and never interpret a late or hung release as confirmed cleanup.
                _abandoned = true;
                owner._barrier.RequireVerifiedRecovery(_runId, item.Game);
                owner._barrier.BeginOperation(_runId, item.Game);
                var release = Task.Run(async () => { await run.DisposeAsync().ConfigureAwait(false); return true; });
                _ = ObserveAbandonedAsync("DisposeAsync", release, null);
            }
        }

        private enum BoundStatus { Completed, Faulted, Abandoned }

        private readonly record struct Bounded<T>(BoundStatus Status, T? Value, Exception? Error);

        /// <summary>
        /// Runs one adapter call under a scheduler-owned limit (#28). The call runs on the thread pool, so an adapter
        /// that blocks synchronously is bounded too, and receives a token cancelled by an emergency stop or the limit.
        /// If it has not returned by the limit (or <see cref="SchedulerOptions.EmergencyStopTimeout"/> after an
        /// emergency stop, when <paramref name="emergencyShortens"/>), the scheduler stops waiting. That does not end
        /// the call: it is tracked on the safety barrier until it returns, its result or exception is observed, and
        /// the run's cleanup can no longer be reported as confirmed.
        /// </summary>
        private async Task<Bounded<T>> BoundAsync<T>(string operation, Func<CancellationToken, Task<T>> call, TimeSpan limit,
            bool emergencyShortens, Func<T, Task>? onLateResult = null)
        {
            CancellationTokenSource? limitCts = null;
            CancellationTokenSource? callCts = null;
            CancellationTokenRegistration launchRegistration = default;
            Task<T> task;
            try
            {
                limitCts = new CancellationTokenSource(limit, Time);
                callCts = CancellationTokenSource.CreateLinkedTokenSource(stopToken, limitCts.Token);
                if (operation == "StartAsync")
                    launchRegistration = callCts.Token.Register(() => _processScope?.RevokeLaunches());
                task = Task.Run(() => call(callCts.Token), CancellationToken.None);
            }
            catch
            {
                launchRegistration.Dispose();
                callCts?.Dispose();
                limitCts?.Dispose();
                throw;
            }
            Task? winner;
            try
            {
                using var waitCts = new CancellationTokenSource();
                var deadline = Task.Delay(limit, Time, waitCts.Token);
                var grace = emergencyShortens
                    ? GraceAfterEmergencyAsync(waitCts.Token)
                    : Task.Delay(Timeout.InfiniteTimeSpan, waitCts.Token);
                winner = await Task.WhenAny(task, deadline, grace).ConfigureAwait(false);
                waitCts.Cancel();
            }
            catch (Exception)
            {
                // The call is already running: if waiting for it fails, it must still never go untracked.
                winner = null;
            }
            if (winner == task)
            {
                launchRegistration.Dispose();
                callCts.Dispose();
                limitCts.Dispose();
                try { return new(BoundStatus.Completed, await task.ConfigureAwait(false), null); }
                catch (Exception ex) { return new(BoundStatus.Faulted, default, ex); }
            }

            _abandoned = true;
            if (operation == "StartAsync") _processScope?.RevokeLaunches();
            owner._barrier.BeginOperation(_runId, item.Game);
            _ = ObserveAbandonedAsync(operation, task, onLateResult, launchRegistration, callCts, limitCts);
            return new(BoundStatus.Abandoned, default, null);
        }

        private async Task GraceAfterEmergencyAsync(CancellationToken ct)
        {
            await emergency.WaitAsync(ct).ConfigureAwait(false);
            await Task.Delay(Options.EmergencyStopTimeout, Time, ct).ConfigureAwait(false);
        }

        /// <summary>Waits for an abandoned call to really return, then releases its hold on the barrier.</summary>
        private async Task ObserveAbandonedAsync<T>(string operation, Task<T> task, Func<T, Task>? onLateResult,
            params IDisposable[] disposables)
        {
            try
            {
                string note;
                try
                {
                    var value = await task.ConfigureAwait(false);
                    note = $"适配器操作 {operation} 在时限之后才返回。";
                    if (onLateResult is not null)
                    {
                        Task? release = null;
                        try
                        {
                            release = onLateResult(value);
                            await release.WaitAsync(Options.DisposeTimeout, Time, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (TimeoutException)
                        {
                            release?.ObserveLater();
                            owner._barrier.RequireVerifiedRecovery(_runId, item.Game);
                            note += " 迟到结果释放超时；需核实资源归属。";
                        }
                        catch (Exception ex)
                        {
                            owner._barrier.RequireVerifiedRecovery(_runId, item.Game);
                            note += $" 释放迟到的结果时出错（{ex.GetType().Name}）。";
                        }
                    }
                }
                catch (Exception ex)
                {
                    note = $"适配器操作 {operation} 在时限之后以异常结束（{ex.GetType().Name}）。";
                }
                foreach (var d in disposables) d.Dispose();
                // A journal that never returns must not keep PendingOperations permanently high.
                Task? write = null;
                try
                {
                    using var auditCts = new CancellationTokenSource(Options.JournalTimeout, Time);
                    write = owner._journal.AppendEventsAsync(
                        [new RunEvent(_runId, Time.GetUtcNow(), RunEventKind.Warning, note)], auditCts.Token);
                    await write.WaitAsync(Options.JournalTimeout, Time, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    write?.ObserveLater();
                    owner._barrier.RequireVerifiedRecovery(_runId, item.Game);
                }
            }
            finally
            {
                owner._barrier.EndOperation(_runId);
            }
        }

        /// <summary>Closes the run after an unexpected internal error, without depending on the clock or adapter.</summary>
        public async Task<RunUpdate> AbortAsync()
        {
            _processScope?.RevokeLaunches();
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
            if (update.Cleanup == CleanupState.Unconfirmed) owner._barrier.MarkUnconfirmed(update, persisted: false);
            var persisted = await PersistAsync(update).ConfigureAwait(false);
            if (!persisted) JournalFaulted = true;
            if (update.Cleanup == CleanupState.Unconfirmed) owner._barrier.MarkUnconfirmed(update, persisted);
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
            // Off the scheduler's path: an adapter whose enumerator or verdict blocks synchronously cannot stall it.
            var pump = Task.Run(() => PumpEventsAsync(run, watchCts.Token), CancellationToken.None);
            var verdictTask = Task.Run(() => WaitVerdictSafeAsync(run), CancellationToken.None);
            var timeoutTask = run is IPausableEngineRun pausable
                ? ActiveTimeoutWatchAsync(limits.TaskTimeout, pausable, watchCts.Token)
                : Task.Delay(limits.TaskTimeout, Time, watchCts.Token);
            var stallTask = StallWatchAsync(limits.StallTimeout, run as IPausableEngineRun, watchCts.Token);

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

            var reconciled = await BoundAsync("ReconcileAsync", t => run.ReconcileAsync(t),
                Options.ReconcileTimeout, emergencyShortens: false).ConfigureAwait(false);
            var cleanup = reconciled is { Status: BoundStatus.Completed, Value: CleanupState.Confirmed }
                ? CleanupState.Confirmed : CleanupState.Unconfirmed;
            // The engine has ended (or been stopped): give its event stream a short, bounded chance to complete on its
            // own, so events emitted before the end are not lost when the pump started late. Then stop reading and let
            // already-buffered events reach storage. Both waits are bounded in case an adapter's stream misbehaves.
            await Task.WhenAny(pump, Task.Delay(EventDrainTimeout, Time)).ConfigureAwait(false);
            watchCts.Cancel();
            await Task.WhenAny(pump, Task.Delay(EventFlushTimeout, Time)).ConfigureAwait(false);
            await DisposeRunAsync(run).ConfigureAwait(false);
            if (_processScope is { } scope)
            {
                scope.RevokeLaunches();
                if (!await scope.RevokeAsync(Options.DisposeTimeout).ConfigureAwait(false))
                {
                    owner._barrier.RequireVerifiedRecovery(_runId, item.Game);
                    cleanup = CleanupState.Unconfirmed;
                }
            }
            // An abandoned call may still be acting on the machine: never report this run as cleaned up.
            if (_abandoned) cleanup = CleanupState.Unconfirmed;
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
            // Whether the engine really stopped is decided by the verdict and reconciliation, not by this call.
            await BoundAsync("RequestStopAsync", t => run.RequestStopAsync(mode, t),
                Options.EmergencyStopTimeout, emergencyShortens: false).ConfigureAwait(false);
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

        private async Task StallWatchAsync(TimeSpan stall, IPausableEngineRun? pausable, CancellationToken ct)
        {
            while (true)
            {
                if (pausable?.IsPaused == true)
                {
                    Interlocked.Exchange(ref _lastActivity, Time.GetTimestamp());
                    await Task.Delay(TimeSpan.FromSeconds(1), Time, ct).ConfigureAwait(false);
                    continue;
                }
                var idle = Time.GetElapsedTime(Interlocked.Read(ref _lastActivity));
                if (idle >= stall) return;
                await Task.Delay(stall - idle, Time, ct).ConfigureAwait(false);
            }
        }

        private async Task ActiveTimeoutWatchAsync(TimeSpan limit, IPausableEngineRun run, CancellationToken ct)
        {
            var active = TimeSpan.Zero;
            var previous = Time.GetTimestamp();
            while (active < limit)
            {
                var interval = TimeSpan.FromSeconds(1);
                await Task.Delay(interval, Time, ct).ConfigureAwait(false);
                var now = Time.GetTimestamp();
                if (!run.IsPaused) active += Time.GetElapsedTime(previous, now);
                previous = now;
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
            _processScope?.RevokeLaunches();
            if (owner._barrier.Find(_runId)?.RequiresVerifiedRecovery == true)
                cleanup = CleanupState.Unconfirmed;
            _sm.SetCleanup(cleanup);
            var confirmed = cleanup is CleanupState.Confirmed or CleanupState.NotRequired;
            _sm.MoveTo(confirmed ? RunPhase.Finished : RunPhase.Blocked);
            // L-30-1: close the barrier before the terminal state is published or shown.
            if (!confirmed) owner._barrier.MarkUnconfirmed(Snapshot(), persisted: false);
            var update = await PublishAsync().ConfigureAwait(false);
            // Fail closed: the barrier holds whether or not the database accepted the final state.
            if (!confirmed) owner._barrier.MarkUnconfirmed(update, _lastPublishSucceeded);
            return (update, !confirmed);
        }

        private async Task<RunUpdate> PublishAsync()
        {
            var update = Snapshot();
            _lastPublishSucceeded = await PersistAsync(update).ConfigureAwait(false);
            if (!_lastPublishSucceeded) JournalFaulted = true;
            owner._barrier.Observe(update, _lastPublishSucceeded);
            owner.Raise(update);
            return update;
        }

        /// <summary>
        /// Writes one state through the scheduler's serialized writer and waits a bounded time
        /// (<see cref="SchedulerOptions.JournalTimeout"/>; not at all while the journal is already stalled, and no
        /// longer once an emergency stop arrives). Returns whether the write is known to have succeeded. A write that
        /// is still running is never abandoned silently: it stays queued (so it cannot land after a newer state), its
        /// completion or failure is observed, and until then the run is a persistence hazard that blocks new queues.
        /// </summary>
        private async Task<bool> PersistAsync(RunUpdate update)
        {
            // Whether a process of this run may exist at the time of this write (its launch capability was issued).
            var processMayExist = _processScope is not null;
            Task write;
            try { write = owner._writer.Enqueue(update); }
            catch (Exception) { return Failed(update, processMayExist); }
            if (!write.IsCompleted && !owner._writer.IsStalled)
            {
                using var waitCts = new CancellationTokenSource();
                Task deadline;
                try { deadline = Task.Delay(Options.JournalTimeout, Time, waitCts.Token); }
                catch (Exception) { deadline = Task.Delay(Options.JournalTimeout, waitCts.Token); } // clock failed (abort path)
                // An emergency stop that arrives during the wait ends it; one that is already in effect does not
                // (the write is then usually instantaneous, and shortening it would only manufacture hazards).
                await (emergency.IsCompleted ? Task.WhenAny(write, deadline) : Task.WhenAny(write, deadline, emergency))
                    .ConfigureAwait(false);
                waitCts.Cancel();
            }
            if (write.IsCompleted)
                return write.IsCompletedSuccessfully || Failed(update, processMayExist, write);

            owner._writer.BeginOverdue();
            owner._barrier.BeginPersistence(_runId, item.Game);
            _ = ObserveOverdueWriteAsync(write, update, processMayExist);
            return false;
        }

        private bool Failed(RunUpdate update, bool processMayExist, Task? write = null)
        {
            _ = write?.Exception; // observed
            if (processMayExist) owner._barrier.MarkPersistenceFailed(Snapshot() with { At = update.At });
            return false;
        }

        private async Task ObserveOverdueWriteAsync(Task write, RunUpdate update, bool processMayExist)
        {
            try
            {
                var succeeded = true;
                try { await write.ConfigureAwait(false); }
                catch (Exception) { succeeded = false; }
                if (succeeded && update.Phase is RunPhase.Finished or RunPhase.Blocked) owner._barrier.MarkPersisted(_runId);
                if (!succeeded && processMayExist) owner._barrier.MarkPersistenceFailed(Snapshot());
            }
            finally
            {
                owner._writer.EndOverdue();
                owner._barrier.EndPersistence(_runId);
            }
        }
    }

    private readonly IRunJournal _journal = journal;
    private readonly IProcessSupervisor _supervisor = supervisor;

    private void Raise(RunUpdate update)
    {
        try { RunChanged?.Invoke(update); } catch (Exception) { /* observers never break safety */ }
    }
}
