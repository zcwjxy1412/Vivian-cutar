using VIvian.Core.Execution;
using VIvian.Core;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Accounts;
using VIvian.Infrastructure.Engines.OneDragon;
using VIvian.Tests.Execution;
using Xunit;

namespace VIvian.Tests.Engines.OneDragon;

public sealed class OneDragonJournalBoundaryTests
{
    [Fact]
    public async Task UserStopReachesOwnedCleanupWhileRunningWriteIsStillBlocked()
    {
        await using var fixture = new OneDragonFixture();
        using var journal = new GatedJournal("Running", true);
        using var cancel = new CancellationTokenSource();
        var batch = OneDragonAccountBatch.RunAsync(journal,
            fixture.Adapter(OneDragonArguments.OneDragon(instances: [2]).Request), fixture.Supervisor,
            fixture.Root, Guid.NewGuid(), new(456, 2, ["coffee"], "138****11"), null, cancel.Token,
            journalTimeout: TimeSpan.FromSeconds(10));
        try
        {
            await journal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (fixture.Supervisor.Group!.TerminateCalls == 0) await Task.Delay(5, limit.Token);
            Assert.False(journal.Returned.Task.IsCompleted);
            journal.Release.Set();
            var result = await batch.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(RunOutcome.Cancelled, result.Outcome);
            Assert.Equal(1, fixture.Supervisor.Group.DisposeCalls);
        }
        finally { journal.Release.Set(); await journal.Returned.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task ExpiredQueuedWritesAreNotInvokedAfterLateWriterReturns()
    {
        var boundary = new OneDragonJournalBoundary(TimeSpan.FromMilliseconds(100));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var laterCalls = 0;
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => boundary.WriteAsync(async _ =>
            { await release.Task; returned.SetResult(); }));
            await Assert.ThrowsAsync<TimeoutException>(() => boundary.WriteAsync(_ =>
            { Interlocked.Increment(ref laterCalls); return Task.CompletedTask; }));
            release.SetResult();
            await returned.Task;
            // A fresh operation passes the same serial gate after all earlier work.
            await boundary.WriteAsync(_ => Task.CompletedTask);
            Assert.Equal(0, laterCalls);
        }
        finally { release.TrySetResult(); }
    }
    [Fact]
    public async Task HungJournalStopsDurablePlanBeforeSecondChild()
    {
        await using var fixture = new OneDragonFixture();
        using var journal = new GatedJournal("Running", true);
        var db = new LocalDatabase(Path.Combine(fixture.Root, "plan.db"));
        await db.InitializeAsync();
        var calls = 0;
        var plan = new PreparedExecutionPlan(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5),
            Enumerable.Range(0, 2).Select(i => new ExecutionPlanItem(i, GameId.ZenlessZoneZero, Guid.NewGuid(),
                "synthetic", "routine", "revision", 1, "saved_account_settings", "synthetic")).ToArray());
        var coordinator = new PersistentExecutionPlanCoordinator(db, async (item, register, ct) =>
        {
            calls++;
            var result = await OneDragonAccountBatch.RunAsync(journal,
                fixture.Adapter(OneDragonArguments.OneDragon(instances: [2]).Request), fixture.Supervisor,
                fixture.Root, item.ChildBatchId, new(456, 2, ["coffee"], "138****11"), null, ct,
                register, TimeSpan.FromMilliseconds(200));
            return new(item.ChildBatchId, result.Success, result.Success ? 1 : 0, result.Success ? 0 : 1,
                result.Detail, Outcome: result.Outcome, EngineCleanupConfirmed: result.CleanupConfirmed,
                ConfigurationRestored: result.ConfigurationRestored);
        });
        try
        {
            await coordinator.SubmitAsync(plan);
            await coordinator.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, calls);
            Assert.Equal(1, fixture.Supervisor.Group!.DisposeCalls);
            var receipt = await db.ReadAsync(plan.PlanId);
            Assert.Equal(ExecutionPlanState.Stopped, receipt!.State);
            Assert.Equal(CleanupState.Unconfirmed, receipt.Items[0].Result!.Cleanup);
            Assert.Equal(ExecutionPlanItemState.Skipped, receipt.Items[1].State);
        }
        finally { journal.Release.Set(); await journal.Returned.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task TimedOutWriterCannotRaceOrRegressPersistedTerminalState()
    {
        await using var fixture = new OneDragonFixture();
        var db = new LocalDatabase(Path.Combine(fixture.Root, "journal.db"));
        await db.InitializeAsync();
        var id = Guid.NewGuid();
        RunUpdate Update(RunPhase phase) => new(id, id, GameId.ZenlessZoneZero, "synthetic", true, phase,
            null, CleanupState.Unconfirmed, null, DateTimeOffset.UtcNow);
        await db.PublishAsync(Update(RunPhase.Starting), default);
        var boundary = new OneDragonJournalBoundary(TimeSpan.FromMilliseconds(200));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var pending = boundary.WriteAsync(async _ =>
            {
                entered.SetResult();
                await release.Task;
                await db.PublishAsync(Update(RunPhase.Running), default); // Deliberately ignores cancellation.
                returned.SetResult();
            });
            await entered.Task;
            await Assert.ThrowsAsync<TimeoutException>(() => pending);
            var terminal = boundary.WriteAsync(ct => db.PublishAsync(Update(RunPhase.Blocked), ct));
            release.SetResult();
            await terminal;
            await returned.Task;
            await Assert.ThrowsAsync<InvalidOperationException>(() => boundary.WriteAsync(ct => db.PublishAsync(Update(RunPhase.Running), ct)));
            Assert.Equal(RunPhase.Blocked, Assert.Single(await db.ReadRecentRunsAsync(10)).Phase);
        }
        finally { release.TrySetResult(); }
    }
    [Theory]
    [InlineData("Starting", true)]
    [InlineData("Starting", false)]
    [InlineData("Running", true)]
    [InlineData("Running", false)]
    [InlineData("events", true)]
    [InlineData("events", false)]
    [InlineData("Finished", true)]
    [InlineData("Finished", false)]
    public async Task HungWritesAreBoundedAndOwnedRunIsReleasedOnce(string stage, bool synchronous)
    {
        await using var fixture = new OneDragonFixture();
        using var journal = new GatedJournal(stage, synchronous);
        var batch = Task.Run(() => OneDragonAccountBatch.RunAsync(journal,
            fixture.Adapter(OneDragonArguments.OneDragon(instances: [2]).Request), fixture.Supervisor,
            fixture.Root, Guid.NewGuid(), new(456, 2, ["coffee"], "138****11"), null, default,
            journalTimeout: TimeSpan.FromMilliseconds(200)));
        try
        {
            if (stage != "Starting")
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (fixture.Supervisor.Group is null) await Task.Delay(5, timeout.Token);
                var group = fixture.Supervisor.Group;
                group.Write($"VIVIAN-ZZZ|{group.RunId:D}|start|5151|2");
                if (stage == "Finished")
                {
                    group.Write($"VIVIAN-ZZZ|{group.RunId:D}|entered", $"VIVIAN-ZZZ|{group.RunId:D}|task|coffee|success",
                        $"VIVIAN-ZZZ|{group.RunId:D}|end|success");
                    group.Exit(0);
                }
            }
            await journal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<TimeoutException>(() => batch.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(batch.IsCompleted);
            if (stage == "Starting") Assert.Null(fixture.Supervisor.Group);
            else
            {
                Assert.Equal(1, fixture.Supervisor.Group!.DisposeCalls);
                if (stage is "Running" or "events") Assert.True(fixture.Supervisor.Group.TerminateCalls > 0);
            }
            Assert.True(journal.MaxActive <= 1);
        }
        finally
        {
            journal.Release.Set();
            await journal.Returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try { await batch.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticFailurePreservesOriginalStartException(bool hang)
    {
        await using var fixture = new OneDragonFixture();
        using var journal = new GatedJournal("Blocked", synchronous: true, fail: !hang);
        var original = new EngineStartException("synthetic_start", true, "original startup failure");
        try
        {
            var error = await Assert.ThrowsAsync<EngineStartException>(() => Task.Run(() => OneDragonAccountBatch.RunAsync(journal,
                fixture.Adapter(OneDragonArguments.OneDragon(instances: [2]).Request), new StartFailure(original),
                fixture.Root, Guid.NewGuid(), new(456, 2, ["coffee"], "138****11"), null, default,
                journalTimeout: TimeSpan.FromMilliseconds(200))).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Same(original, error);
        }
        finally { journal.Release.Set(); await journal.Returned.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private sealed class StartFailure(EngineStartException error) : IProcessSupervisor
    {
        public Task<ISupervisedProcessGroup> StartAsync(ProcessLaunchSpec spec, Guid id, CancellationToken ct) => throw error;
    }

    internal sealed class GatedJournal(string stage, bool synchronous, bool fail = false) : IRunJournal, IDisposable
    {
        public ManualResetEventSlim Release { get; } = new(false);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public InMemoryJournal Store { get; } = new();
        private int _active;
        public int MaxActive;
        private async Task GateAsync(string current)
        {
            var active = Interlocked.Increment(ref _active);
            MaxActive = Math.Max(MaxActive, active);
            try
            {
                if (current != stage) return;
                Entered.TrySetResult();
                if (fail) throw new IOException("diagnostic unavailable");
                if (synchronous) Release.Wait();
                else while (!Release.IsSet) await Task.Delay(10);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
        public async Task PublishAsync(RunUpdate update, CancellationToken ct)
        {
            try { await GateAsync(update.Phase.ToString()); await Store.PublishAsync(update, ct); }
            finally { if (update.Phase.ToString() == stage) Returned.TrySetResult(); }
        }
        public async Task AppendEventsAsync(IReadOnlyList<RunEvent> events, CancellationToken ct)
        {
            try { await GateAsync("events"); await Store.AppendEventsAsync(events, ct); }
            finally { if (stage == "events") Returned.TrySetResult(); }
        }
        public Task<int> CountUnresolvedAsync(CancellationToken ct) => Store.CountUnresolvedAsync(ct);
        public void Dispose() => Release.Set(); // A late worker may still observe the event after the bounded caller exits.
    }
}
