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
}
