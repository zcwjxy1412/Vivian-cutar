using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using VIvian.Core;
using VIvian.Core.Execution;
using VIvian.Infrastructure;
using Xunit;

namespace VIvian.Tests.Execution;

/// <summary>
/// Regression tests for #28: adapter calls that ignore cancellation and never return. Driven on a virtual clock,
/// so results do not depend on machine speed.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class AdapterHangTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vivian-hang", Guid.NewGuid().ToString("N"));
    public AdapterHangTests() => Directory.CreateDirectory(_root);
    public void Dispose() => TestDirectories.DeleteWithRetry(_root);

    private static readonly DateTimeOffset Start = new(2026, 11, 4, 4, 0, 0, TimeSpan.Zero);

    private static SchedulerOptions Options => new(
        GracefulStopTimeout: TimeSpan.FromSeconds(30), EmergencyStopTimeout: TimeSpan.FromSeconds(15),
        ReconcileTimeout: TimeSpan.FromSeconds(30), DefaultLimits: new RunLimits(TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(10)));

    private static ScriptedAdapter Completes(GameId game) =>
        new(game) { Script = r => { r.End(EvidenceKind.CompletionReported); return Task.CompletedTask; } };

    // ---------------------------------------------------------------- #28

    public enum HangPoint { Preflight, Start, Stop, Reconcile }

    /// <summary>
    /// Adapter whose chosen call ignores cancellation and does not return until the test releases it.
    /// Before release the call is "hung"; release lets the late result arrive afterwards.
    /// </summary>
    public sealed class HangingAdapter(GameId game, HangPoint hang) : IGameAdapter
    {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HangingRun? Run;
        public int StartCount;
        public GameId Game => game;
        public string RuleVersion => "hang-1";
        public bool IsSimulation => true;

        public async Task<PreflightResult> PreflightAsync(RunContext c, CancellationToken ct)
        {
            if (hang == HangPoint.Preflight) await Release.Task.ConfigureAwait(false); // ignores ct on purpose
            return PreflightResult.Ok();
        }

        public async Task<IEngineRun> StartAsync(RunContext c, IProcessSupervisor s, CancellationToken ct)
        {
            Interlocked.Increment(ref StartCount);
            if (hang == HangPoint.Start) await Release.Task.ConfigureAwait(false);
            Run = new HangingRun(c.RunId, hang, Release.Task);
            return Run;
        }
    }

    public sealed class HangingRun : IEngineRun
    {
        private readonly TaskCompletionSource<RunVerdict> _verdict = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly HangPoint _hang;
        private readonly Task _release;
        public volatile bool Disposed;
        public int StopRequests;

        public HangingRun(Guid id, HangPoint hang, Task release)
        {
            RunId = id;
            _hang = hang;
            _release = release;
            // Reconcile scenario: the engine finishes normally, only reconciliation hangs.
            if (hang is HangPoint.Reconcile or HangPoint.Preflight or HangPoint.Start) End(EvidenceKind.CompletionReported);
        }

        public Guid RunId { get; }
        public IAsyncEnumerable<RunEvent> Events => Empty();
        private static async IAsyncEnumerable<RunEvent> Empty() { await Task.CompletedTask; yield break; }

        private void End(EvidenceKind kind) => _verdict.TrySetResult(
            VerdictPolicy.Decide(RunId, [new Evidence(RunId, kind, "hang-test", DateTimeOffset.UtcNow)]));

        public Task<RunVerdict> WaitForVerdictAsync(CancellationToken ct) => _verdict.Task.WaitAsync(ct);

        public async Task<StopResult> RequestStopAsync(StopMode mode, CancellationToken ct)
        {
            Interlocked.Increment(ref StopRequests);
            if (_hang == HangPoint.Stop) await _release.ConfigureAwait(false); // ignores ct
            End(EvidenceKind.CancelReported);
            return new StopResult(CleanupState.Pending, "stopped");
        }

        public async Task<CleanupState> ReconcileAsync(CancellationToken ct)
        {
            if (_hang == HangPoint.Reconcile) await _release.ConfigureAwait(false); // ignores ct
            return CleanupState.Confirmed;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Advances virtual time in one-second steps until the queue finishes or the budget is spent.</summary>
    private static async Task<QueueResult?> DriveAsync(Task<QueueResult> queue, FakeTimeProvider time, TimeSpan budget)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < budget && !queue.IsCompleted; elapsed += TimeSpan.FromSeconds(1))
        {
            await Task.Delay(2);
            time.Advance(TimeSpan.FromSeconds(1));
        }
        for (var i = 0; i < 200 && !queue.IsCompleted; i++) await Task.Delay(5);
        return queue.IsCompleted ? await queue : null;
    }

    private static RunLimits HangLimits => new(TimeSpan.FromMinutes(5), TimeSpan.FromHours(2));

    [Theory]
    [InlineData(HangPoint.Preflight)]
    [InlineData(HangPoint.Start)]
    [InlineData(HangPoint.Stop)]
    [InlineData(HangPoint.Reconcile)]
    public async Task I28_HungAdapterCallEndsQueueAsBlockedAndUnconfirmed(HangPoint point)
    {
        var time = new FakeTimeProvider(Start);
        var scheduler = new QueueScheduler(new InMemoryJournal(), new NoProcessSupervisor(), Options, time);
        var hanging = new HangingAdapter(GameId.Genshin, point);
        var next = Completes(GameId.StarRail);
        var queue = scheduler.RunAsync([new QueueItem(GameId.Genshin, hanging, HangLimits), new QueueItem(GameId.StarRail, next)]);

        var result = await DriveAsync(queue, time, TimeSpan.FromHours(3));

        Assert.True(result is not null, $"queue still running after 3 virtual hours (hang at {point})");
        Assert.Equal(QueueStatus.Blocked, result!.Status);
        var run = result.Runs.Single();
        Assert.Equal(CleanupState.Unconfirmed, run.Cleanup);
        Assert.Equal(RunPhase.Blocked, run.Phase);
        // Reconcile: the engine did report completion; the outcome layer may say so, but the run is still Blocked with
        // cleanup Unconfirmed (three independent layers). Every other hang point has no completion evidence at all.
        if (point == HangPoint.Reconcile) Assert.Equal(RunOutcome.Success, run.Outcome);
        else Assert.NotEqual(RunOutcome.Success, run.Outcome);
        Assert.Equal(0, next.StartCount);
        Assert.False(scheduler.IsRunning);
        hanging.Release.TrySetResult();
    }

    [Theory]
    [InlineData(HangPoint.Preflight)]
    [InlineData(HangPoint.Start)]
    [InlineData(HangPoint.Stop)]
    [InlineData(HangPoint.Reconcile)]
    public async Task I28_EmergencyStopEndsQueueEvenWhenAdapterIgnoresCancellation(HangPoint point)
    {
        var time = new FakeTimeProvider(Start);
        var scheduler = new QueueScheduler(new InMemoryJournal(), new NoProcessSupervisor(), Options, time);
        var hanging = new HangingAdapter(GameId.Genshin, point);
        using var stop = new CancellationTokenSource();
        var queue = scheduler.RunAsync([new QueueItem(GameId.Genshin, hanging, HangLimits)], stop.Token);
        // Let the run reach the hung call (Stop: the user's stop is what triggers the hung RequestStopAsync).
        await Task.Delay(50);
        stop.Cancel();

        // Must end within the emergency grace (15 s) plus at most one 30 s bound, far below the 60 s preflight / 120 s start limits.
        var result = await DriveAsync(queue, time, TimeSpan.FromSeconds(45));

        Assert.True(result is not null, $"emergency stop did not end the queue within 45 virtual seconds (hang at {point})");
        Assert.Equal(CleanupState.Unconfirmed, result!.Runs.Single().Cleanup);
        Assert.Equal(QueueStatus.Blocked, result.Status);
        Assert.False(scheduler.IsRunning);
        hanging.Release.TrySetResult();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(what);
            await Task.Delay(5);
        }
    }

    private async Task<(LocalDatabase Db, QueueScheduler Scheduler, FakeTimeProvider Time, HangingAdapter Hanging, Guid RunId)>
        HangAndAbandonAsync(HangPoint point, string name)
    {
        var db = new LocalDatabase(Path.Combine(_root, name));
        await db.InitializeAsync();
        var time = new FakeTimeProvider(Start);
        var scheduler = new QueueScheduler(db, new NoProcessSupervisor(), Options, time);
        var hanging = new HangingAdapter(GameId.Genshin, point);
        var result = await DriveAsync(scheduler.RunAsync([new QueueItem(GameId.Genshin, hanging, HangLimits)]), time, TimeSpan.FromHours(1));
        Assert.NotNull(result);
        return (db, scheduler, time, hanging, result!.Runs.Single().RunId);
    }

    [Theory]
    [InlineData(HangPoint.Preflight)]
    [InlineData(HangPoint.Start)]
    [InlineData(HangPoint.Stop)]
    [InlineData(HangPoint.Reconcile)]
    public async Task I28_AbandonedCallKeepsBarrierClosedUntilItReturnsAndIsAcknowledged(HangPoint point)
    {
        var (db, scheduler, _, hanging, runId) = await HangAndAbandonAsync(point, $"barrier-{point}.db");

        // While the abandoned call is outstanding: queues refused, acknowledgement refused.
        var hazard = Assert.Single(scheduler.SessionHazards);
        Assert.True(hazard.PendingOperations > 0);
        var next = Completes(GameId.StarRail);
        var refused = await scheduler.RunAsync([new QueueItem(GameId.StarRail, next)]);
        Assert.Equal(QueueStatus.Blocked, refused.Status);
        Assert.Equal(ReasonCodes.AdapterUnresponsive, refused.BlockReason);
        Assert.Equal(0, next.StartCount);
        await Assert.ThrowsAsync<RunStillActiveException>(() => scheduler.AcknowledgeAsync(runId, "太早了", db, Start));

        // The call finally returns. That is not proof of cleanup: the run still needs the user's confirmation.
        hanging.Release.TrySetResult();
        await WaitUntilAsync(() => scheduler.SessionHazards.All(h => h.PendingOperations == 0), "abandoned call observed");
        Assert.True(Assert.Single(scheduler.SessionHazards).RequiresAcknowledgement);
        Assert.Equal(QueueStatus.Blocked, (await scheduler.RunAsync([new QueueItem(GameId.StarRail, next)])).Status);
        Assert.Equal(0, next.StartCount);
        Assert.Contains(await db.ReadEventsAsync(runId, 50), e => e.Kind == RunEventKind.Warning && e.Message.Contains("在时限之后"));

        await scheduler.AcknowledgeAsync(runId, "已确认", db, Start);
        Assert.Empty(scheduler.SessionHazards);
        Assert.Equal(QueueStatus.Completed, (await scheduler.RunAsync([new QueueItem(GameId.StarRail, next)])).Status);
        Assert.Equal(1, next.StartCount);
        var stored = (await db.ReadRecentRunsAsync(10)).Single(r => r.RunId == runId);
        Assert.Equal(CleanupState.Unconfirmed, stored.Cleanup); // the acknowledgement never rewrites the verdict
        Assert.Equal(RunPhase.Blocked, stored.Phase);
    }

    [Fact]
    public async Task I28_RunArrivingAfterStartWasAbandonedIsReleasedButNotTrusted()
    {
        var (_, scheduler, _, hanging, _) = await HangAndAbandonAsync(HangPoint.Start, "late-start.db");
        Assert.Null(hanging.Run);
        hanging.Release.TrySetResult();
        await WaitUntilAsync(() => hanging.Run is { Disposed: true }, "late run disposed");
        await WaitUntilAsync(() => scheduler.SessionHazards.All(h => h.PendingOperations == 0), "observed");
        Assert.Equal(0, hanging.Run!.StopRequests); // it was released, not driven
        Assert.Equal(CleanupState.Unconfirmed, Assert.Single(scheduler.SessionHazards).LastKnown!.Cleanup);
    }

    private sealed class NeverDisposedRun(Guid id) : IEngineRun
    {
        public TaskCompletionSource DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid RunId => id;
        public IAsyncEnumerable<RunEvent> Events => Empty();
        private static async IAsyncEnumerable<RunEvent> Empty() { await Task.CompletedTask; yield break; }
        public Task<RunVerdict> WaitForVerdictAsync(CancellationToken ct) =>
            Task.FromResult(VerdictPolicy.Decide(id, [new Evidence(id, EvidenceKind.CompletionReported, "test", DateTimeOffset.UtcNow)]));
        public Task<StopResult> RequestStopAsync(StopMode mode, CancellationToken ct) =>
            Task.FromResult(new StopResult(CleanupState.Pending, "test"));
        public Task<CleanupState> ReconcileAsync(CancellationToken ct) => Task.FromResult(CleanupState.Confirmed);
        public async ValueTask DisposeAsync()
        {
            DisposeEntered.TrySetResult();
            await Release.Task.ConfigureAwait(false);
        }
    }

    private sealed class NeverDisposedAdapter : IGameAdapter
    {
        public NeverDisposedRun? Run;
        public GameId Game => GameId.Genshin;
        public string RuleVersion => "never-disposed";
        public bool IsSimulation => true;
        public Task<PreflightResult> PreflightAsync(RunContext c, CancellationToken ct) => Task.FromResult(PreflightResult.Ok());
        public Task<IEngineRun> StartAsync(RunContext c, IProcessSupervisor s, CancellationToken ct) =>
            Task.FromResult<IEngineRun>(Run = new NeverDisposedRun(c.RunId));
    }

    private sealed class LateNeverDisposedAdapter : IGameAdapter
    {
        public TaskCompletionSource ReleaseStart { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public NeverDisposedRun? Run;
        public GameId Game => GameId.Genshin;
        public string RuleVersion => "late-never-disposed";
        public bool IsSimulation => true;
        public Task<PreflightResult> PreflightAsync(RunContext c, CancellationToken ct) => Task.FromResult(PreflightResult.Ok());
        public async Task<IEngineRun> StartAsync(RunContext c, IProcessSupervisor s, CancellationToken ct)
        {
            await ReleaseStart.Task.ConfigureAwait(false);
            return Run = new NeverDisposedRun(c.RunId);
        }
    }

    [Fact]
    public async Task H30_DisposeThatNeverReturnsEndsQueueButCannotBeAcknowledged()
    {
        var time = new FakeTimeProvider(Start);
        var adapter = new NeverDisposedAdapter();
        var scheduler = new QueueScheduler(new InMemoryJournal(), new NoProcessSupervisor(),
            Options with { DisposeTimeout = TimeSpan.FromSeconds(2) }, time);
        var queue = scheduler.RunAsync([new QueueItem(GameId.Genshin, adapter)]);
        try
        {
            await WaitUntilAsync(() => adapter.Run?.DisposeEntered.Task.IsCompleted == true, "dispose entered");
            var result = await DriveAsync(queue, time, TimeSpan.FromSeconds(10));
            Assert.NotNull(result);
            Assert.Equal(QueueStatus.Blocked, result!.Status);
            Assert.Equal(CleanupState.Unconfirmed, result.Runs.Single().Cleanup);
            var hazard = Assert.Single(scheduler.SessionHazards);
            Assert.True(hazard.RequiresVerifiedRecovery);
            Assert.Equal(QueueStatus.Blocked,
                (await scheduler.RunAsync([new QueueItem(GameId.StarRail, Completes(GameId.StarRail))])).Status);
        }
        finally { adapter.Run?.Release.TrySetResult(); }
    }

    [Fact]
    public async Task H30_LateDisposeTimeoutEndsPendingButKeepsVerifiedRecoveryGate()
    {
        var time = new FakeTimeProvider(Start);
        var adapter = new LateNeverDisposedAdapter();
        var db = new LocalDatabase(Path.Combine(_root, "late-dispose.db"));
        await db.InitializeAsync();
        var scheduler = new QueueScheduler(db, new NoProcessSupervisor(),
            Options with { StartTimeout = TimeSpan.FromSeconds(2), DisposeTimeout = TimeSpan.FromSeconds(2) }, time);
        var result = await DriveAsync(scheduler.RunAsync([new QueueItem(GameId.Genshin, adapter)]),
            time, TimeSpan.FromSeconds(15));
        Assert.NotNull(result);
        Assert.True(Assert.Single(scheduler.SessionHazards).PendingOperations > 0);
        adapter.ReleaseStart.TrySetResult();
        try
        {
            await WaitUntilAsync(() => adapter.Run?.DisposeEntered.Task.IsCompleted == true, "late dispose entered");
            for (var i = 0; i < 10 && scheduler.SessionHazards.Any(h => h.PendingOperations > 0); i++)
            {
                time.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(10);
            }
            var hazard = Assert.Single(scheduler.SessionHazards);
            Assert.Equal(0, hazard.PendingOperations);
            Assert.True(hazard.RequiresVerifiedRecovery);
            await Assert.ThrowsAsync<RunStillActiveException>(() =>
                scheduler.AcknowledgeAsync(result!.Runs.Single().RunId, "generic confirmation", db, Start));
            Assert.Equal(QueueStatus.Blocked,
                (await scheduler.RunAsync([new QueueItem(GameId.StarRail, Completes(GameId.StarRail))])).Status);
        }
        finally { adapter.Run?.Release.TrySetResult(); }
    }

    private sealed class HangingJournal(bool hangCount, bool hangEvents) : IRunJournal
    {
        private readonly InMemoryJournal _inner = new();
        private readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task PublishAsync(RunUpdate update, CancellationToken ct) => _inner.PublishAsync(update, ct);
        public Task AppendEventsAsync(IReadOnlyList<RunEvent> events, CancellationToken ct) =>
            hangEvents ? _never.Task : _inner.AppendEventsAsync(events, ct);
        public Task<int> CountUnresolvedAsync(CancellationToken ct) =>
            hangCount ? WaitForever() : _inner.CountUnresolvedAsync(ct);
        private async Task<int> WaitForever() { await _never.Task.ConfigureAwait(false); return 0; }
    }

    [Fact]
    public async Task M30_JournalGateTimeoutBlocksWithoutStartingAdapter()
    {
        var time = new FakeTimeProvider(Start);
        var adapter = Completes(GameId.Genshin);
        var scheduler = new QueueScheduler(new HangingJournal(hangCount: true, hangEvents: false),
            new NoProcessSupervisor(), Options with { JournalTimeout = TimeSpan.FromSeconds(2) }, time);
        var result = await DriveAsync(scheduler.RunAsync([new QueueItem(GameId.Genshin, adapter)]),
            time, TimeSpan.FromSeconds(10));
        Assert.NotNull(result);
        Assert.Equal(QueueStatus.Blocked, result!.Status);
        Assert.Equal(ReasonCodes.JournalUnavailable, result.BlockReason);
        Assert.Equal(0, adapter.StartCount);
        Assert.False(scheduler.IsRunning);
    }

    [Fact]
    public async Task H30_LateAuditWriteThatNeverReturnsDoesNotHoldPendingForever()
    {
        var time = new FakeTimeProvider(Start);
        var journal = new HangingJournal(hangCount: false, hangEvents: true);
        var scheduler = new QueueScheduler(journal, new NoProcessSupervisor(),
            Options with { PreflightTimeout = TimeSpan.FromSeconds(2), JournalTimeout = TimeSpan.FromSeconds(2) }, time);
        var hanging = new HangingAdapter(GameId.Genshin, HangPoint.Preflight);
        var result = await DriveAsync(scheduler.RunAsync([new QueueItem(GameId.Genshin, hanging)]),
            time, TimeSpan.FromSeconds(10));
        Assert.NotNull(result);
        hanging.Release.TrySetResult();
        for (var i = 0; i < 10 && scheduler.SessionHazards.Any(h => h.PendingOperations > 0); i++)
        {
            await Task.Delay(10);
            time.Advance(TimeSpan.FromSeconds(1));
        }
        var hazard = Assert.Single(scheduler.SessionHazards);
        Assert.Equal(0, hazard.PendingOperations);
        Assert.True(hazard.RequiresVerifiedRecovery);
        Assert.Equal(QueueStatus.Blocked,
            (await scheduler.RunAsync([new QueueItem(GameId.StarRail, Completes(GameId.StarRail))])).Status);
    }

    private sealed class LateFailure : Exception;

    [Fact]
    public async Task I28_LateExceptionFromAbandonedCallIsObserved()
    {
        var unobserved = 0;
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) =>
        {
            if (e.Exception.InnerExceptions.Any(x => x is LateFailure)) Interlocked.Increment(ref unobserved);
        };
        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            var (db, scheduler, _, hanging, runId) = await HangAndAbandonAsync(HangPoint.Preflight, "late-exception.db");
            hanging.Release.TrySetException(new LateFailure());
            await WaitUntilAsync(() => scheduler.SessionHazards.All(h => h.PendingOperations == 0), "observed");
            for (var i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); await Task.Delay(20); }
            Assert.Equal(0, unobserved);
            Assert.Contains(await db.ReadEventsAsync(runId, 50), e => e.Message.Contains("以异常结束（LateFailure）"));
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }
    }

    /// <summary>Blocks the calling thread inside PreflightAsync before returning any task.</summary>
    private sealed class SynchronouslyBlockingAdapter(ManualResetEventSlim gate) : IGameAdapter
    {
        public GameId Game => GameId.Genshin;
        public string RuleVersion => "sync-1";
        public bool IsSimulation => true;
        public int StartCount;
        public Task<PreflightResult> PreflightAsync(RunContext c, CancellationToken ct)
        {
            gate.Wait(); // ignores ct, blocks synchronously
            return Task.FromResult(PreflightResult.Ok());
        }
        public Task<IEngineRun> StartAsync(RunContext c, IProcessSupervisor s, CancellationToken ct)
        {
            Interlocked.Increment(ref StartCount);
            throw new EngineStartException(ReasonCodes.StartFailed, nothingStarted: true, "not used");
        }
    }

    [Fact]
    public async Task I28_SynchronouslyBlockingAdapterIsBoundedToo()
    {
        using var gate = new ManualResetEventSlim(false);
        var time = new FakeTimeProvider(Start);
        var scheduler = new QueueScheduler(new InMemoryJournal(), new NoProcessSupervisor(), Options, time);
        var adapter = new SynchronouslyBlockingAdapter(gate);
        try
        {
            var result = await DriveAsync(scheduler.RunAsync([new QueueItem(GameId.Genshin, adapter)]), time, TimeSpan.FromMinutes(5));
            Assert.NotNull(result);
            Assert.Equal(QueueStatus.Blocked, result!.Status);
            Assert.Equal(ReasonCodes.AdapterUnresponsive, result.Runs.Single().ReasonCode);
            Assert.Equal(0, adapter.StartCount);
            Assert.False(scheduler.IsRunning);
        }
        finally
        {
            gate.Set();
        }
    }

    // ---------------------------------------------------------------- H-30 (lead remediation)

    private sealed class FakeGroup(Guid runId) : ISupervisedProcessGroup
    {
        public volatile bool Terminated;
        public volatile bool Disposed;
        public Func<Task<CleanupState>>? Terminate;
        public Guid RunId => runId;
        public int RootProcessId => 4242;
        public DateTimeOffset RootStartTime => Start;
        public IAsyncEnumerable<string> OutputLines => Empty();
        private static async IAsyncEnumerable<string> Empty() { await Task.CompletedTask; yield break; }
        public int ActiveProcessCount => Terminated ? 0 : 1;
        public Task<int> WaitForRootExitAsync(CancellationToken ct) => new TaskCompletionSource<int>().Task.WaitAsync(ct);
        public Task<bool> SendInputLineAsync(string line, CancellationToken ct) => Task.FromResult(false);
        public Task<CleanupState> TerminateAllAsync(TimeSpan timeout, CancellationToken ct)
        {
            Terminated = true;
            return Terminate?.Invoke() ?? Task.FromResult(CleanupState.Confirmed);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    /// <summary>Supervisor whose launch blocks the calling thread (like CreateProcess + Job assignment).</summary>
    private sealed class BlockingSupervisor(ManualResetEventSlim entered, ManualResetEventSlim release) : IProcessSupervisor
    {
        public FakeGroup? Group;
        public Task<ISupervisedProcessGroup> StartAsync(ProcessLaunchSpec spec, Guid runId, CancellationToken ct)
        {
            entered.Set();
            release.Wait();
            return Task.FromResult<ISupervisedProcessGroup>(Group = new FakeGroup(runId));
        }
    }

    private sealed class FixedSupervisor(Func<Guid, FakeGroup> create) : IProcessSupervisor
    {
        public FakeGroup? Group;
        public Task<ISupervisedProcessGroup> StartAsync(ProcessLaunchSpec spec, Guid runId, CancellationToken ct) =>
            Task.FromResult<ISupervisedProcessGroup>(Group = create(runId));
    }

    /// <summary>Launches through the supplied supervisor, ignoring cancellation, then optionally hangs.</summary>
    private sealed class LaunchingAdapter(Task? hangAfterLaunch = null, Func<Guid, IEngineRun>? lateRun = null) : IGameAdapter
    {
        public Exception? LaunchError;
        public GameId Game => GameId.Genshin;
        public string RuleVersion => "launching-1";
        public bool IsSimulation => true;
        public Task<PreflightResult> PreflightAsync(RunContext c, CancellationToken ct) => Task.FromResult(PreflightResult.Ok());
        public async Task<IEngineRun> StartAsync(RunContext c, IProcessSupervisor s, CancellationToken ct)
        {
            try
            {
                await s.StartAsync(new ProcessLaunchSpec("fake.exe", [], "."), c.RunId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LaunchError = ex;
                throw;
            }
            if (hangAfterLaunch is not null) await hangAfterLaunch.ConfigureAwait(false);
            return lateRun?.Invoke(c.RunId) ?? new HangingRun(c.RunId, HangPoint.Reconcile, Task.CompletedTask);
        }
    }

    // H-30-1: an emergency stop while the supervisor is still creating the process. Neither the stop request nor
    // the scheduler waits for the creation; the queue ends with the hazard held, and the process that appears
    // afterwards is terminated instead of being handed to the adapter.
    [Fact]
    public async Task H30_EmergencyStopDuringLaunchNeverWaitsAndTheLaunchedGroupIsTerminated()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var time = new FakeTimeProvider(Start);
        var supervisor = new BlockingSupervisor(entered, release);
        var adapter = new LaunchingAdapter();
        var scheduler = new QueueScheduler(new InMemoryJournal(), supervisor,
            Options with { DisposeTimeout = TimeSpan.FromSeconds(2) }, time);
        using var stop = new CancellationTokenSource();
        var queue = scheduler.RunAsync([new QueueItem(GameId.Genshin, adapter)], stop.Token);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "launch not reached");
            // Repeated, concurrent stop requests from the UI token and the scheduler API.
            var stops = Task.WhenAll(Task.Run(stop.Cancel), Task.Run(scheduler.EmergencyStop),
                Task.Run(stop.Cancel), Task.Run(scheduler.EmergencyStop));
            await stops.WaitAsync(TimeSpan.FromSeconds(5));
            // The launch is still blocked: the queue must end anyway (emergency grace + bounded revoke).
            var result = await DriveAsync(queue, time, TimeSpan.FromSeconds(60));
            Assert.True(result is not null, "queue waited for a launch in progress");
            Assert.False(entered.IsSet && release.IsSet);
            var run = result!.Runs.Single();
            Assert.NotEqual(RunOutcome.Success, run.Outcome);
            Assert.Equal(CleanupState.Unconfirmed, run.Cleanup);
            Assert.True(Assert.Single(scheduler.SessionHazards).RequiresVerifiedRecovery);
            Assert.False(scheduler.IsRunning);
        }
        finally { release.Set(); }

        await WaitUntilAsync(() => supervisor.Group is { Terminated: true, Disposed: true }, "late group terminated");
        await WaitUntilAsync(() => adapter.LaunchError is not null, "adapter saw the refusal");
        var launchError = Assert.IsType<EngineStartException>(adapter.LaunchError);
        Assert.False(launchError.NothingStarted);
        await WaitUntilAsync(() => scheduler.SessionHazards.All(h => h.PendingOperations == 0), "late start observed");
        Assert.True(Assert.Single(scheduler.SessionHazards).RequiresVerifiedRecovery);
    }

    // H-30-2: safe barrier behaviour under repeated emergency stops while the stop call itself hangs.
    [Fact]
    public async Task H30_RepeatedEmergencyStopsEndTheQueueOnceAndKeepTheBarrierClosed()
    {
        var time = new FakeTimeProvider(Start);
        var scheduler = new QueueScheduler(new InMemoryJournal(), new NoProcessSupervisor(), Options, time);
        var hanging = new HangingAdapter(GameId.Genshin, HangPoint.Stop);
        using var stop = new CancellationTokenSource();
        var queue = scheduler.RunAsync([new QueueItem(GameId.Genshin, hanging, HangLimits)], stop.Token);
        try
        {
            await WaitUntilAsync(() => hanging.Run is not null, "run started");
            for (var i = 0; i < 5; i++)
            {
                scheduler.EmergencyStop();
                stop.Cancel();
                time.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(5);
            }
            var result = await DriveAsync(queue, time, TimeSpan.FromSeconds(60));
            Assert.NotNull(result);
            Assert.Equal(CleanupState.Unconfirmed, result!.Runs.Single().Cleanup);
            Assert.Equal(1, hanging.Run!.StopRequests); // escalation happens once, not once per click
            scheduler.EmergencyStop(); // after the queue ended: harmless
            var hazard = Assert.Single(scheduler.SessionHazards);
            Assert.True(hazard.PendingOperations > 0);
            Assert.Equal(QueueStatus.Blocked,
                (await scheduler.RunAsync([new QueueItem(GameId.StarRail, Completes(GameId.StarRail))])).Status);
        }
        finally { hanging.Release.TrySetResult(); }
    }

    // H-30-2: process termination, late-result disposal and the audit write all time out at once. Every wait is
    // bounded, bookkeeping ends, and the process hazard stays: nothing becomes Confirmed.
    [Fact]
    public async Task H30_ConcurrentCleanupTimeoutsAreAllBoundedAndNeverConfirmed()
    {
        var time = new FakeTimeProvider(Start);
        var startRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NeverDisposedRun? late = null;
        var supervisor = new FixedSupervisor(id => new FakeGroup(id) { Terminate = () => new TaskCompletionSource<CleanupState>().Task });
        var adapter = new LaunchingAdapter(startRelease.Task, id => late = new NeverDisposedRun(id));
        var journal = new HangingJournal(hangCount: false, hangEvents: true);
        var scheduler = new QueueScheduler(journal, supervisor, Options with
        {
            StartTimeout = TimeSpan.FromSeconds(2), DisposeTimeout = TimeSpan.FromSeconds(2), JournalTimeout = TimeSpan.FromSeconds(2),
        }, time);
        var result = await DriveAsync(scheduler.RunAsync([new QueueItem(GameId.Genshin, adapter)]), time, TimeSpan.FromSeconds(20));
        try
        {
            Assert.NotNull(result);
            Assert.Equal(QueueStatus.Blocked, result!.Status);
            Assert.Equal(CleanupState.Unconfirmed, result.Runs.Single().Cleanup);
            Assert.True(supervisor.Group!.Terminated);
            Assert.True(supervisor.Group.Disposed); // Job handle released (kill-on-close), still not proof
            var hazard = Assert.Single(scheduler.SessionHazards);
            Assert.True(hazard.RequiresVerifiedRecovery); // termination never confirmed
            Assert.True(hazard.PendingOperations > 0);

            startRelease.TrySetResult(); // the abandoned StartAsync returns a run whose disposal never ends
            await WaitUntilAsync(() => late?.DisposeEntered.Task.IsCompleted == true, "late dispose entered");
            for (var i = 0; i < 20 && scheduler.SessionHazards.Any(h => h.PendingOperations > 0); i++)
            {
                time.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(10);
            }
            hazard = Assert.Single(scheduler.SessionHazards);
            Assert.Equal(0, hazard.PendingOperations); // bookkeeping ended…
            Assert.True(hazard.RequiresVerifiedRecovery); // …the safety hazard did not
            await Assert.ThrowsAsync<RunStillActiveException>(() =>
                scheduler.AcknowledgeAsync(result.Runs.Single().RunId, "generic confirmation", new LocalDatabase(Path.Combine(_root, "unused.db")), Start));
            Assert.Equal(QueueStatus.Blocked,
                (await scheduler.RunAsync([new QueueItem(GameId.StarRail, Completes(GameId.StarRail))])).Status);
        }
        finally { late?.Release.TrySetResult(); startRelease.TrySetResult(); }
    }

    /// <summary>A late run whose disposal fails at a moment the test chooses.</summary>
    private sealed class ThrowingDisposeRun(Guid id) : IEngineRun
    {
        public TaskCompletionSource DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FailNow { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid RunId => id;
        public IAsyncEnumerable<RunEvent> Events => Empty();
        private static async IAsyncEnumerable<RunEvent> Empty() { await Task.CompletedTask; yield break; }
        public Task<RunVerdict> WaitForVerdictAsync(CancellationToken ct) => new TaskCompletionSource<RunVerdict>().Task.WaitAsync(ct);
        public Task<StopResult> RequestStopAsync(StopMode mode, CancellationToken ct) => Task.FromResult(new StopResult(CleanupState.Pending, "t"));
        public Task<CleanupState> ReconcileAsync(CancellationToken ct) => Task.FromResult(CleanupState.Confirmed);
        public async ValueTask DisposeAsync()
        {
            DisposeEntered.TrySetResult();
            await FailNow.Task.ConfigureAwait(false);
            throw new LateFailure();
        }
    }

    /// <summary>In-memory journal that signals when the scheduler's audit note for a late failure is stored.</summary>
    private sealed class AuditSignalJournal : IRunJournal
    {
        private readonly InMemoryJournal _inner = new();
        public TaskCompletionSource<RunEvent> LateFailureAudited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task PublishAsync(RunUpdate u, CancellationToken ct) => _inner.PublishAsync(u, ct);
        public async Task AppendEventsAsync(IReadOnlyList<RunEvent> events, CancellationToken ct)
        {
            await _inner.AppendEventsAsync(events, ct).ConfigureAwait(false);
            foreach (var e in events.Where(e => e.Message.Contains("释放迟到的结果时出错", StringComparison.Ordinal)))
                LateFailureAudited.TrySetResult(e);
        }
        public Task<int> CountUnresolvedAsync(CancellationToken ct) => _inner.CountUnresolvedAsync(ct);
    }

    private static Task WhenHazardsAsync(QueueScheduler scheduler, Func<IReadOnlyList<SessionHazard>, bool> condition)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check() { if (condition(scheduler.SessionHazards)) reached.TrySetResult(); }
        scheduler.HazardsChanged += Check;
        Check();
        return reached.Task.ContinueWith(t => { scheduler.HazardsChanged -= Check; return t; },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
    }

    private async Task LateResultThatThrowsOnceAsync()
    {
        var time = new FakeTimeProvider(Start);
        var journal = new AuditSignalJournal();
        var startRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ThrowingDisposeRun? late = null;
        var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new LaunchingAdapter(startRelease.Task, id => { late = new ThrowingDisposeRun(id); created.TrySetResult(); return late; });
        var scheduler = new QueueScheduler(journal, new FixedSupervisor(id => new FakeGroup(id)),
            Options with { StartTimeout = TimeSpan.FromSeconds(2) }, time);
        try
        {
            var result = await DriveAsync(scheduler.RunAsync([new QueueItem(GameId.Genshin, adapter)]), time, TimeSpan.FromSeconds(20));
            Assert.NotNull(result);
            Assert.True(Assert.Single(scheduler.SessionHazards).PendingOperations > 0);

            startRelease.TrySetResult(); // the abandoned StartAsync returns its run late
            await created.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await late!.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // While the late disposal is still running, its bookkeeping is still pending.
            Assert.True(Assert.Single(scheduler.SessionHazards).PendingOperations > 0);

            var drained = WhenHazardsAsync(scheduler, h => h.All(x => x.PendingOperations == 0));
            late.FailNow.TrySetResult(); // the late processing throws now
            // Observed: the scheduler caught the exception (its type is in the stored audit note) …
            var audit = await journal.LateFailureAudited.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains("（LateFailure）", audit.Message, StringComparison.Ordinal);
            Assert.Equal(result!.Runs.Single().RunId, audit.RunId);
            // … and the bookkeeping ended, but the safety hazard did not.
            await drained.WaitAsync(TimeSpan.FromSeconds(10));
            var hazard = Assert.Single(scheduler.SessionHazards);
            Assert.True(hazard.RequiresVerifiedRecovery);
            await Assert.ThrowsAsync<RunStillActiveException>(() =>
                scheduler.AcknowledgeAsync(hazard.RunId, "generic confirmation", new LocalDatabase(Path.Combine(_root, Guid.NewGuid() + ".db")), Start));
            var next = Completes(GameId.StarRail);
            Assert.Equal(QueueStatus.Blocked, (await scheduler.RunAsync([new QueueItem(GameId.StarRail, next)])).Status);
            Assert.Equal(0, next.StartCount);
        }
        finally
        {
            startRelease.TrySetResult();
            late?.FailNow.TrySetResult();
        }
    }

    // H-30-2: processing the late result throws. Explicit gates only: no GC, no global unobserved-exception hook.
    // Eight independent schedulers run concurrently to expose ordering assumptions.
    [Fact]
    public async Task H30_LateResultProcessingThatThrowsIsObservedAndKeepsTheHazard()
    {
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(LateResultThatThrowsOnceAsync)));
    }

    // H-30-2 controlled recovery — the gate protocol only. A hazard that needs verified recovery cannot be
    // acknowledged in-session. Ending VIvian closes every Job handle VIvian itself owns (kill-on-close), so after a
    // restart the stored run (Blocked, cleanup Unconfirmed) can be acknowledged. This test uses simulated groups:
    // it proves neither that any process exited nor anything about processes outside VIvian's Job (UAC, launchers,
    // scheduled tasks). The acknowledgement is the user's statement, never cleanup evidence: the stored cleanup stays
    // Unconfirmed. Real process identity checks and cross-privilege paths remain Pending (desktop validation).
    [Fact]
    public async Task H30_RestartIsTheControlledRecoveryPathForAnUnresolvedCleanup()
    {
        var time = new FakeTimeProvider(Start);
        var db = new LocalDatabase(Path.Combine(_root, "restart-recovery.db"));
        await db.InitializeAsync();
        var supervisor = new FixedSupervisor(id => new FakeGroup(id) { Terminate = () => Task.FromResult(CleanupState.Unconfirmed) });
        var startRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new LaunchingAdapter(startRelease.Task);
        var scheduler = new QueueScheduler(db, supervisor, Options with { StartTimeout = TimeSpan.FromSeconds(2) }, time);
        var result = await DriveAsync(scheduler.RunAsync([new QueueItem(GameId.Genshin, adapter)]), time, TimeSpan.FromSeconds(20));
        Assert.NotNull(result);
        var runId = result!.Runs.Single().RunId;
        Assert.True(Assert.Single(scheduler.SessionHazards).RequiresVerifiedRecovery);
        await Assert.ThrowsAsync<RunStillActiveException>(() => scheduler.AcknowledgeAsync(runId, "同一会话", db, Start));
        startRelease.TrySetResult();

        // "Restart": a new process reconciles interrupted rows and starts with an empty in-memory barrier.
        await db.ReconcileInterruptedRunsAsync(Start);
        var restarted = new QueueScheduler(db, new NoProcessSupervisor(), Options, time);
        var next = Completes(GameId.StarRail);
        Assert.Equal(QueueStatus.Blocked, (await restarted.RunAsync([new QueueItem(GameId.StarRail, next)])).Status);
        Assert.Equal(0, next.StartCount);
        await restarted.AcknowledgeAsync(runId, "重启后确认引擎已结束", db, Start);
        Assert.Equal(QueueStatus.Completed, (await restarted.RunAsync([new QueueItem(GameId.StarRail, next)])).Status);
        var stored = (await db.ReadRecentRunsAsync(10)).Single(r => r.RunId == runId);
        Assert.Equal(CleanupState.Unconfirmed, stored.Cleanup);
        Assert.NotEqual(RunOutcome.Success, stored.Outcome);
    }

    // M-30-1: a row an earlier session left open counts as unresolved; acknowledging it closes it first (never as
    // success) instead of failing forever.
    [Fact]
    public async Task M30_AcknowledgingARowLeftOpenByAnEarlierSessionClosesItFirst()
    {
        var db = new LocalDatabase(Path.Combine(_root, "left-open.db"));
        await db.InitializeAsync();
        var runId = Guid.NewGuid();
        await db.PublishAsync(new RunUpdate(runId, Guid.NewGuid(), GameId.Genshin, "r1", true, RunPhase.Running,
            null, CleanupState.Pending, null, Start), default);
        var scheduler = new QueueScheduler(db, new NoProcessSupervisor(), Options);
        var next = Completes(GameId.StarRail);
        Assert.Equal(QueueStatus.Blocked, (await scheduler.RunAsync([new QueueItem(GameId.StarRail, next)])).Status);

        await scheduler.AcknowledgeAsync(runId, "确认已关闭", db, Start);

        var stored = (await db.ReadRecentRunsAsync(10)).Single(r => r.RunId == runId);
        Assert.Equal(RunPhase.Blocked, stored.Phase);
        Assert.Equal(CleanupState.Unconfirmed, stored.Cleanup);
        Assert.Equal(RunOutcome.Indeterminate, stored.Outcome);
        Assert.NotNull(stored.AcknowledgedAt);
        Assert.False(scheduler.IsRunning);
        Assert.Equal(QueueStatus.Completed, (await scheduler.RunAsync([new QueueItem(GameId.StarRail, next)])).Status);
    }

    private sealed class ObservingJournal : IRunJournal
    {
        private readonly InMemoryJournal _inner = new();
        public QueueScheduler? Scheduler;
        public bool BarrierClosedWhenBlockedWasPublished;
        public Task PublishAsync(RunUpdate u, CancellationToken ct)
        {
            if (u.Phase == RunPhase.Blocked)
                BarrierClosedWhenBlockedWasPublished = Scheduler!.SessionHazards.Any(h => h.RunId == u.RunId && h.RequiresAcknowledgement);
            return _inner.PublishAsync(u, ct);
        }
        public Task AppendEventsAsync(IReadOnlyList<RunEvent> e, CancellationToken ct) => _inner.AppendEventsAsync(e, ct);
        public Task<int> CountUnresolvedAsync(CancellationToken ct) => _inner.CountUnresolvedAsync(ct);
    }

    // L-30-1: the barrier is closed before the Blocked state is stored or shown.
    [Fact]
    public async Task L30_BarrierClosesBeforeTheBlockedStateIsPublished()
    {
        var journal = new ObservingJournal();
        var scheduler = new QueueScheduler(journal, new NoProcessSupervisor(), Options);
        journal.Scheduler = scheduler;
        var first = Completes(GameId.Genshin);
        first.ReconcileResult = CleanupState.Unconfirmed;
        Assert.Equal(QueueStatus.Blocked, (await scheduler.RunAsync([new QueueItem(GameId.Genshin, first)])).Status);
        Assert.True(journal.BarrierClosedWhenBlockedWasPublished);
    }
}
