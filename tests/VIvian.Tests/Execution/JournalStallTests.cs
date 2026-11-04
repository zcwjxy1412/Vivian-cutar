using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using VIvian.Core;
using VIvian.Core.Execution;
using Xunit;

namespace VIvian.Tests.Execution;

/// <summary>
/// Persistence that never completes, and the stop / launch-permission race (Codex review of #33 and #20).
/// Driven on a virtual clock with explicit gates; nothing depends on machine speed or GC timing.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class JournalStallTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 4, 4, 0, 0, TimeSpan.Zero);

    private static SchedulerOptions Options => new(
        GracefulStopTimeout: TimeSpan.FromSeconds(30), EmergencyStopTimeout: TimeSpan.FromSeconds(15),
        ReconcileTimeout: TimeSpan.FromSeconds(30), DefaultLimits: new RunLimits(TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(10)))
    {
        JournalTimeout = TimeSpan.FromSeconds(5),
    };

    private static RunLimits Long => new(TimeSpan.FromHours(2), TimeSpan.FromHours(2));

    /// <summary>Journal whose writes of the chosen phases block until released (or fail when told to).</summary>
    public sealed class StallingJournal(params RunPhase[] stallPhases) : IRunJournal
    {
        private readonly InMemoryJournal _inner = new();
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StallEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RunPhase? FailPhase { get; set; }
        public bool FailStalledWritesOnRelease { get; set; }
        public ConcurrentQueue<RunUpdate> Committed { get; } = new();
        public int Concurrent;
        private int _stalled;
        /// <summary>Writes currently held at the stall gate on purpose.</summary>
        public int Stalled => Volatile.Read(ref _stalled);
        public int MaxConcurrent;

        public async Task PublishAsync(RunUpdate update, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref Concurrent);
            InterlockedMax(ref MaxConcurrent, now);
            try
            {
                if (stallPhases.Contains(update.Phase) && !Release.Task.IsCompleted)
                {
                    Interlocked.Increment(ref _stalled);
                    StallEntered.TrySetResult();
                    try { await Release.Task.ConfigureAwait(false); } // ignores ct on purpose
                    finally { Interlocked.Decrement(ref _stalled); }
                    if (FailStalledWritesOnRelease) throw new IOException("stalled write failed");
                }
                if (update.Phase == FailPhase) throw new IOException("write failed");
                await _inner.PublishAsync(update, ct).ConfigureAwait(false);
                Committed.Enqueue(update);
            }
            finally { Interlocked.Decrement(ref Concurrent); }
        }

        public Task AppendEventsAsync(IReadOnlyList<RunEvent> events, CancellationToken ct) => _inner.AppendEventsAsync(events, ct);
        public Task<int> CountUnresolvedAsync(CancellationToken ct) => _inner.CountUnresolvedAsync(ct);

        private static void InterlockedMax(ref int target, int value)
        {
            int seen;
            while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
        }
    }

    /// <summary>
    /// Advances virtual time one second per step, but only once every write that is not deliberately stalled has
    /// finished: healthy writes never race the virtual clock, so only the injected stalls can time out.
    /// </summary>
    private static async Task<QueueResult?> DriveAsync(Task<QueueResult> queue, FakeTimeProvider time, TimeSpan budget,
        QueueScheduler scheduler, Func<int>? deliberatelyStalled = null)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < budget && !queue.IsCompleted; elapsed += TimeSpan.FromSeconds(1))
        {
            await VirtualClock.WaitForHealthyWritesAsync(scheduler, deliberatelyStalled);
            await Task.Delay(2);
            time.Advance(TimeSpan.FromSeconds(1));
        }
        for (var i = 0; i < 200 && !queue.IsCompleted; i++) await Task.Delay(5);
        return queue.IsCompleted ? await queue : null;
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

    private static ScriptedAdapter Completes(GameId game) =>
        new(game) { Script = r => { r.End(EvidenceKind.CompletionReported); return Task.CompletedTask; } };

    private static void AssertMonotonic(IEnumerable<RunUpdate> committed, Guid runId)
    {
        var phases = committed.Where(u => u.RunId == runId).Select(u => (int)Rank(u.Phase)).ToList();
        for (var i = 1; i < phases.Count; i++)
            Assert.True(phases[i] >= phases[i - 1], $"state written out of order: {string.Join(",", phases)}");
    }

    private static int Rank(RunPhase phase) => phase switch
    {
        RunPhase.Pending => 0, RunPhase.Preflight => 1, RunPhase.Starting => 2, RunPhase.Running => 3,
        RunPhase.Stopping => 4, RunPhase.Reconciling => 5, _ => 6,
    };

    // ---------------------------------------------------------------- P0-1: journal writes that never complete

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunningWriteHangsButEmergencyStopStillTerminatesAndReleasesTheRun(bool viaCallerToken)
    {
        var time = new FakeTimeProvider(Start);
        var journal = new StallingJournal(RunPhase.Running);
        var scheduler = new QueueScheduler(journal, new NoProcessSupervisor(), Options, time);
        var adapter = new ScriptedAdapter(GameId.Genshin); // runs until stopped
        using var caller = new CancellationTokenSource();
        var queue = scheduler.RunAsync([new QueueItem(GameId.Genshin, adapter, Long)], caller.Token);
        try
        {
            await journal.StallEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // Application shutdown (caller token) or the stop button, while SQLite is stalled.
            if (viaCallerToken) await DedicatedThread.Run(caller.Cancel); else scheduler.EmergencyStop();
            var result = await DriveAsync(queue, time, TimeSpan.FromMinutes(3), scheduler, () => journal.Stalled);

            Assert.True(result is not null, "queue stayed occupied behind a stalled journal write");
            Assert.False(scheduler.IsRunning);
            var run = adapter.LastRun!;
            Assert.Contains(run.StopRequests, r => r.Mode == StopMode.Emergency);
            Assert.True(run.Disposed);
            // Persistence is uncertain: the next queue must not start.
            var hazard = Assert.Single(scheduler.SessionHazards);
            Assert.True(hazard.PersistencePending > 0);
            var next = Completes(GameId.StarRail);
            var refused = await scheduler.RunAsync([new QueueItem(GameId.StarRail, next)]);
            Assert.Equal(QueueStatus.Blocked, refused.Status);
            Assert.Equal(ReasonCodes.JournalUnavailable, refused.BlockReason);
            Assert.Equal(0, next.StartCount);
        }
        finally { journal.Release.TrySetResult(); }

        // The stalled write completes late: every later state follows it in order, the terminal state wins.
        await WaitUntilAsync(() => scheduler.SessionHazards.All(h => h.PersistencePending == 0), "late writes drained");
        var runId = adapter.LastRun!.RunId;
        AssertMonotonic(journal.Committed, runId);
        Assert.Contains(journal.Committed.Last(u => u.RunId == runId).Phase, new[] { RunPhase.Finished, RunPhase.Blocked });
        Assert.True(journal.MaxConcurrent <= 1, "two state writes ran at the same time");
    }

    [Fact]
    public async Task StoppingWriteHangsButEscalationReconciliationAndDisposalAreReached()
    {
        var time = new FakeTimeProvider(Start);
        var journal = new StallingJournal(RunPhase.Stopping);
        var scheduler = new QueueScheduler(journal, new NoProcessSupervisor(), Options, time);
        var adapter = new ScriptedAdapter(GameId.Genshin) { StopReaction = StopReaction.IgnoreGraceful };
        var queue = scheduler.RunAsync([new QueueItem(GameId.Genshin, adapter, new RunLimits(TimeSpan.FromSeconds(30), TimeSpan.FromHours(1)))]);
        try
        {
            var result = await DriveAsync(queue, time, TimeSpan.FromMinutes(5), scheduler, () => journal.Stalled); // task timeout → graceful → emergency
            Assert.True(result is not null, "queue stayed occupied behind a stalled Stopping write");
            var run = adapter.LastRun!;
            Assert.Contains(run.StopRequests, r => r.Mode == StopMode.Graceful);
            Assert.Contains(run.StopRequests, r => r.Mode == StopMode.Emergency); // escalation was reached
            Assert.True(run.Disposed);
            Assert.True(Assert.Single(scheduler.SessionHazards).PersistencePending > 0);
        }
        finally { journal.Release.TrySetResult(); }
    }

    [Fact]
    public async Task ReconcilingWriteHangsButDisposalIsReachedAndTheNextQueueWaits()
    {
        var time = new FakeTimeProvider(Start);
        var journal = new StallingJournal(RunPhase.Reconciling);
        var scheduler = new QueueScheduler(journal, new NoProcessSupervisor(), Options, time);
        var adapter = Completes(GameId.Genshin);
        var queue = scheduler.RunAsync([new QueueItem(GameId.Genshin, adapter)]);
        try
        {
            var result = await DriveAsync(queue, time, TimeSpan.FromMinutes(2), scheduler, () => journal.Stalled);
            Assert.True(result is not null);
            Assert.True(adapter.LastRun!.Disposed);
            Assert.True(result!.JournalFaulted);
            var next = Completes(GameId.StarRail);
            Assert.Equal(QueueStatus.Blocked, (await scheduler.RunAsync([new QueueItem(GameId.StarRail, next)])).Status);
            Assert.Equal(0, next.StartCount);
        }
        finally { journal.Release.TrySetResult(); }
        await WaitUntilAsync(() => scheduler.SessionHazards.All(h => h.PersistencePending == 0), "late writes drained");
        AssertMonotonic(journal.Committed, adapter.LastRun!.RunId);
    }

    [Fact]
    public async Task TerminalWriteHangsThenCompletesLateAndOnlyThenTheNextQueueMayStart()
    {
        var time = new FakeTimeProvider(Start);
        var journal = new StallingJournal(RunPhase.Finished);
        var scheduler = new QueueScheduler(journal, new NoProcessSupervisor(), Options, time);
        var adapter = Completes(GameId.Genshin);
        var result = await DriveAsync(scheduler.RunAsync([new QueueItem(GameId.Genshin, adapter)]), time, TimeSpan.FromMinutes(2), scheduler, () => journal.Stalled);
        Assert.True(result is not null);
        var next = Completes(GameId.StarRail);
        Assert.Equal(QueueStatus.Blocked, (await scheduler.RunAsync([new QueueItem(GameId.StarRail, next)])).Status);
        await Assert.ThrowsAsync<RunStillActiveException>(() =>
            scheduler.AcknowledgeAsync(result!.Runs.Single().RunId, "太早", new NoHistory(), Start));

        journal.Release.TrySetResult();
        await WaitUntilAsync(() => scheduler.SessionHazards.Count == 0, "terminal write landed; hazard cleared");
        Assert.Equal(RunPhase.Finished, journal.Committed.Last().Phase);
        Assert.Equal(QueueStatus.Completed, (await scheduler.RunAsync([new QueueItem(GameId.StarRail, next)])).Status);
    }

    [Fact]
    public async Task StalledWriteThatFailsLateKeepsTheQueueBlockedUntilAcknowledged()
    {
        var time = new FakeTimeProvider(Start);
        var journal = new StallingJournal(RunPhase.Running) { FailStalledWritesOnRelease = true };
        var scheduler = new QueueScheduler(journal, new NoProcessSupervisor(), Options, time);
        var adapter = Completes(GameId.Genshin);
        var result = await DriveAsync(scheduler.RunAsync([new QueueItem(GameId.Genshin, adapter)]), time, TimeSpan.FromMinutes(2), scheduler, () => journal.Stalled);
        Assert.True(result is not null);
        journal.Release.TrySetResult();
        await WaitUntilAsync(() => scheduler.SessionHazards.All(h => h.PersistencePending == 0), "late failure observed");
        var hazard = Assert.Single(scheduler.SessionHazards);
        Assert.True(hazard.RequiresAcknowledgement); // a write after the process started failed: never assumed fine
        Assert.Equal(QueueStatus.Blocked,
            (await scheduler.RunAsync([new QueueItem(GameId.StarRail, Completes(GameId.StarRail))])).Status);
    }

    [Fact]
    public async Task WriteFailureAfterTheProcessStartedBlocksTheNextQueueUntilAcknowledged()
    {
        var journal = new StallingJournal { FailPhase = RunPhase.Running };
        var scheduler = new QueueScheduler(journal, new NoProcessSupervisor(), Options);
        var adapter = Completes(GameId.Genshin);
        var result = await scheduler.RunAsync([new QueueItem(GameId.Genshin, adapter)]);
        Assert.True(result.JournalFaulted);
        var hazard = Assert.Single(scheduler.SessionHazards);
        Assert.True(hazard.RequiresAcknowledgement);
        var next = Completes(GameId.StarRail);
        Assert.Equal(QueueStatus.Blocked, (await scheduler.RunAsync([new QueueItem(GameId.StarRail, next)])).Status);
        Assert.Equal(0, next.StartCount);

        // The terminal state did reach the journal and cleanup was confirmed: the user's confirmation only clears
        // the in-session gate; it records no cleanup evidence of its own.
        await scheduler.AcknowledgeAsync(hazard.RunId, "已核对", new NoHistory(), Start);
        Assert.Empty(scheduler.SessionHazards);
        Assert.Equal(QueueStatus.Completed, (await scheduler.RunAsync([new QueueItem(GameId.StarRail, next)])).Status);
    }

    private sealed class NoHistory : IRunHistory
    {
        public Task<IReadOnlyList<RunRecord>> ReadRecentRunsAsync(int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RunRecord>>([]);
        public Task<IReadOnlyList<RunRecord>> ReadUnresolvedAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RunRecord>>([]);
        public Task<IReadOnlyList<RunEvent>> ReadEventsAsync(Guid runId, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RunEvent>>([]);
        public Task<IReadOnlyList<RunRecord>> ReconcileInterruptedRunsAsync(DateTimeOffset now, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RunRecord>>([]);
        public Task AcknowledgeAsync(Guid runId, string note, DateTimeOffset at, CancellationToken ct = default) =>
            throw new InvalidOperationException("no stored row to acknowledge");
    }

    // ---------------------------------------------------------------- P0-2: stop signal vs launch permission

    private sealed class Group(Guid runId) : ISupervisedProcessGroup
    {
        public Guid RunId => runId;
        public int RootProcessId => 7;
        public DateTimeOffset RootStartTime => Start;
        public IAsyncEnumerable<string> OutputLines => Empty();
        private static async IAsyncEnumerable<string> Empty() { await Task.CompletedTask; yield break; }
        public int ActiveProcessCount => 0;
        public Task<int> WaitForRootExitAsync(CancellationToken ct) => Task.FromResult(0);
        public Task<bool> SendInputLineAsync(string line, CancellationToken ct) => Task.FromResult(false);
        public Task<CleanupState> TerminateAllAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(CleanupState.Confirmed);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CountingSupervisor : IProcessSupervisor
    {
        public int Calls;
        public Task<ISupervisedProcessGroup> StartAsync(ProcessLaunchSpec spec, Guid runId, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult<ISupervisedProcessGroup>(new Group(runId));
        }
    }

    /// <summary>
    /// Registers a cancellation callback that blocks, then — once the test says so — asks for a process, with its
    /// own token or with <see cref="CancellationToken.None"/>.
    /// </summary>
    private sealed class BlockingCallbackAdapter(bool passNoToken) : IGameAdapter
    {
        public readonly ManualResetEventSlim CallbackRelease = new();
        public readonly TaskCompletionSource CallbackEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Registered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource LaunchNow = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<Exception?> LaunchOutcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public GameId Game => GameId.Genshin;
        public string RuleVersion => "blocking-callback-1";
        public bool IsSimulation => true;
        public Task<PreflightResult> PreflightAsync(RunContext c, CancellationToken ct) => Task.FromResult(PreflightResult.Ok());
        public async Task<IEngineRun> StartAsync(RunContext c, IProcessSupervisor s, CancellationToken ct)
        {
            using var registration = ct.Register(() =>
            {
                CallbackEntered.TrySetResult();
                CallbackRelease.Wait(TimeSpan.FromSeconds(30)); // a misbehaving adapter callback
            });
            Registered.TrySetResult();
            await LaunchNow.Task.ConfigureAwait(false);
            try
            {
                await s.StartAsync(new ProcessLaunchSpec("fake.exe", [], "."), c.RunId,
                    passNoToken ? CancellationToken.None : ct).ConfigureAwait(false);
                LaunchOutcome.TrySetResult(null);
            }
            catch (Exception ex)
            {
                LaunchOutcome.TrySetResult(ex);
                throw;
            }
            throw new EngineStartException(ReasonCodes.StartFailed, nothingStarted: false, "test never runs an engine");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StopRevokesLaunchPermissionEvenWhileACancellationCallbackBlocks(bool passNoToken)
    {
        var time = new FakeTimeProvider(Start);
        var supervisor = new CountingSupervisor();
        var adapter = new BlockingCallbackAdapter(passNoToken);
        var scheduler = new QueueScheduler(new InMemoryJournal(), supervisor, Options, time);
        var queue = scheduler.RunAsync([new QueueItem(GameId.Genshin, adapter)]);
        Task? trigger = null;
        try
        {
            await adapter.Registered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // 1. The stop request begins; 2. the adapter's callback blocks the cancellation inside it.
            trigger = DedicatedThread.Run(scheduler.EmergencyStop);
            await adapter.CallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(trigger.IsCompleted);
            // 3. A concurrent StartAsync asks for a process while the callback is still blocked.
            adapter.LaunchNow.TrySetResult();
            var outcome = await adapter.LaunchOutcome.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // 4. Permission had already been revoked: nothing reached the supervisor.
            var refused = Assert.IsType<EngineStartException>(outcome);
            Assert.True(refused.NothingStarted);
            Assert.Equal(0, supervisor.Calls);
            // 5. The scheduler stays responsive: the queue ends while the callback is still blocked.
            var result = await DriveAsync(queue, time, TimeSpan.FromSeconds(60), scheduler);
            Assert.True(result is not null, "queue did not end while a cancellation callback blocked");
            Assert.False(trigger.IsCompleted);
        }
        finally
        {
            adapter.CallbackRelease.Set();
            adapter.LaunchNow.TrySetResult();
        }
        await trigger!.WaitAsync(TimeSpan.FromSeconds(10));
        scheduler.EmergencyStop(); // repeated stop after the end: harmless
        Assert.Equal(0, supervisor.Calls);
        Assert.False(scheduler.IsRunning);
    }
}
