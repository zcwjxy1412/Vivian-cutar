using VIvian.Core;
using VIvian.Core.Execution;
using VIvian.Infrastructure;
using Xunit;

namespace VIvian.Tests.Execution;

/// <summary>
/// M4-F1: a sequence of manually started real runs through the real scheduler and SQLite journal (in-memory engine,
/// no processes). Each run is admitted on its own; an unconfirmed cleanup blocks the next one until reconciled.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class ManualTestRepeatTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vivian-manual", Guid.NewGuid().ToString("N"));
    public ManualTestRepeatTests() => Directory.CreateDirectory(_root);
    public void Dispose() => TestDirectories.DeleteWithRetry(_root);

    private static readonly SchedulerOptions Options = new(
        GracefulStopTimeout: TimeSpan.FromSeconds(2), EmergencyStopTimeout: TimeSpan.FromSeconds(2),
        ReconcileTimeout: TimeSpan.FromSeconds(2), DefaultLimits: new RunLimits(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)))
    { JournalTimeout = TimeSpan.FromDays(1) };

    /// <summary>A non-simulation adapter, admitted only by the explicit predicate (like March7thAdapter in the app).</summary>
    private sealed class ManualRealAdapter(ScriptedAdapter inner) : IGameAdapter
    {
        public ScriptedAdapter Inner => inner;
        public GameId Game => GameId.StarRail;
        public string RuleVersion => "m7a-test";
        public bool IsSimulation => false;
        public Task<PreflightResult> PreflightAsync(RunContext c, CancellationToken ct) => inner.PreflightAsync(c, ct);
        public Task<IEngineRun> StartAsync(RunContext c, IProcessSupervisor s, CancellationToken ct) => inner.StartAsync(c, s, ct);
    }

    private static ManualRealAdapter Real(Func<ScriptedRun, Task> script, CleanupState cleanup = CleanupState.Confirmed) =>
        new(new ScriptedAdapter(GameId.StarRail) { Script = script, ReconcileResult = cleanup });

    private static Func<ScriptedRun, Task> ReportsFailure => run =>
    {
        run.Emit("测试完成！成功 0 个，失败 1 个，共 39 个界面");
        run.EndWith(new RunVerdict(RunOutcome.Failed, ReasonCodes.ReportedFailure,
            [new Evidence(run.RunId, EvidenceKind.Note, "界面测试失败", DateTimeOffset.UtcNow)]));
        return Task.CompletedTask;
    };

    private static Func<ScriptedRun, Task> Completes => run =>
    {
        run.End(EvidenceKind.CompletionReported);
        return Task.CompletedTask;
    };

    private async Task<(LocalDatabase Db, QueueScheduler Scheduler)> CreateAsync(string name)
    {
        var db = new LocalDatabase(Path.Combine(_root, name + ".db"));
        await db.InitializeAsync();
        return (db, new QueueScheduler(db, new NoProcessSupervisor(), Options, admitReal: a => a is ManualRealAdapter));
    }

    private static QueueItem Item(IGameAdapter a) => new(GameId.StarRail, a);

    [Fact]
    public async Task FailedRunWithConfirmedCleanupAllowsAnotherManualRun()
    {
        var (db, scheduler) = await CreateAsync("repeat");
        var first = await scheduler.RunAsync([Item(Real(ReportsFailure))]);
        var run1 = Assert.Single(first.Runs);
        Assert.Equal((RunPhase.Finished, RunOutcome.Failed, CleanupState.Confirmed), (run1.Phase, run1.Outcome, run1.Cleanup));
        Assert.False(run1.IsSimulation);
        Assert.Equal(0, await db.CountUnresolvedAsync(default));

        var secondAdapter = Real(Completes);
        var second = await scheduler.RunAsync([Item(secondAdapter)]);
        Assert.Equal(1, secondAdapter.Inner.StartCount);
        Assert.Equal(RunOutcome.Success, Assert.Single(second.Runs).Outcome);
        Assert.NotEqual(run1.RunId, second.Runs[0].RunId);
        // Both runs keep their own records.
        var stored = await db.ReadRecentRunsAsync(10);
        Assert.Equal(2, stored.Count(r => !r.IsSimulation));
    }

    [Fact]
    public async Task UnconfirmedCleanupBlocksTheNextRunUntilReconciled()
    {
        var (db, scheduler) = await CreateAsync("blocked");
        var first = await scheduler.RunAsync([Item(Real(ReportsFailure, CleanupState.Unconfirmed))]);
        Assert.Equal(QueueStatus.Blocked, first.Status);
        var blockedRun = Assert.Single(first.Runs);
        Assert.Equal(CleanupState.Unconfirmed, blockedRun.Cleanup);
        Assert.Equal(RunPhase.Blocked, blockedRun.Phase);

        var refused = Real(Completes);
        var second = await scheduler.RunAsync([Item(refused)]);
        Assert.Equal(QueueStatus.Blocked, second.Status);
        Assert.Equal(ReasonCodes.UnresolvedRuns, second.BlockReason);
        Assert.Equal(0, refused.Inner.StartCount);

        await scheduler.AcknowledgeAsync(blockedRun.RunId, "用户核对任务进程已退出。", db, DateTimeOffset.UtcNow);
        var allowed = Real(Completes);
        var third = await scheduler.RunAsync([Item(allowed)]);
        Assert.Equal(1, allowed.Inner.StartCount);
        Assert.Equal(QueueStatus.Completed, third.Status);
    }

    [Fact]
    public async Task EmergencyStopEndsCancelledAndOnlyConfirmedCleanupFreesTheNextRun()
    {
        var (db, scheduler) = await CreateAsync("stop");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = scheduler.RunAsync([Item(Real(run => { run.Emit("working"); started.TrySetResult(); return Task.CompletedTask; }))]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        scheduler.EmergencyStop();
        var stopped = Assert.Single((await running.WaitAsync(TimeSpan.FromSeconds(20))).Runs);
        Assert.Equal(RunOutcome.Cancelled, stopped.Outcome);
        Assert.Equal(CleanupState.Confirmed, stopped.Cleanup);

        var next = Real(Completes);
        Assert.Equal(QueueStatus.Completed, (await scheduler.RunAsync([Item(next)])).Status);

        // The same stop with an unconfirmed cleanup keeps the barrier closed.
        var started2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running2 = scheduler.RunAsync([Item(Real(run => { started2.TrySetResult(); return Task.CompletedTask; }, CleanupState.Unconfirmed))]);
        await started2.Task.WaitAsync(TimeSpan.FromSeconds(10));
        scheduler.EmergencyStop();
        Assert.Equal(CleanupState.Unconfirmed, Assert.Single((await running2.WaitAsync(TimeSpan.FromSeconds(20))).Runs).Cleanup);
        var refused = Real(Completes);
        Assert.Equal(QueueStatus.Blocked, (await scheduler.RunAsync([Item(refused)])).Status);
        Assert.Equal(0, refused.Inner.StartCount);
        Assert.Equal(1, await db.CountUnresolvedAsync(default));
    }

    [Fact]
    public async Task RealAdapterIsRefusedWithoutTheExplicitAdmission()
    {
        var db = new LocalDatabase(Path.Combine(_root, "closed.db"));
        await db.InitializeAsync();
        var adapter = Real(Completes);
        var result = await new QueueScheduler(db, new NoProcessSupervisor(), Options).RunAsync([Item(adapter)]);
        Assert.Equal(0, adapter.Inner.StartCount);
        Assert.Equal(ReasonCodes.RealAdapterUnverified, Assert.Single(result.Runs).ReasonCode);
    }
}
