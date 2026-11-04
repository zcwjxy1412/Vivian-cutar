using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using VIvian.Core;
using VIvian.Core.Execution;
using VIvian.Infrastructure;
using Xunit;

namespace VIvian.Tests.Execution;

/// <summary>
/// Regression tests for #28 (non-cooperative adapter calls) and #29 (persistence failure bypassing the
/// unresolved-run gate). Adapter hangs are driven on a virtual clock, so results do not depend on machine speed.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class SafetyBarrierTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 4, 4, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vivian-barrier", Guid.NewGuid().ToString("N"));

    public SafetyBarrierTests() => Directory.CreateDirectory(_root);
    public void Dispose() => TestDirectories.DeleteWithRetry(_root);

    private static SchedulerOptions Options => new(
        GracefulStopTimeout: TimeSpan.FromSeconds(30), EmergencyStopTimeout: TimeSpan.FromSeconds(15),
        ReconcileTimeout: TimeSpan.FromSeconds(30), DefaultLimits: new RunLimits(TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(10)))
    {
        JournalTimeout = TimeSpan.FromDays(1), // see AdapterHangTests: virtual time must not race real SQLite I/O
    };

    private static ScriptedAdapter Completes(GameId game) =>
        new(game) { Script = r => { r.End(EvidenceKind.CompletionReported); return Task.CompletedTask; } };

    // ---------------------------------------------------------------- #29

    /// <summary>Real SQLite journal whose write of the final Blocked state fails (disk, lock or I/O error).</summary>
    private sealed class FailingTerminalJournal(LocalDatabase db) : IRunJournal
    {
        public bool Fail { get; set; } = true;
        public Task PublishAsync(RunUpdate u, CancellationToken ct) =>
            Fail && u.Phase == RunPhase.Blocked ? throw new SqliteException("simulated write failure", 10) : db.PublishAsync(u, ct);
        public Task AppendEventsAsync(IReadOnlyList<RunEvent> e, CancellationToken ct) => db.AppendEventsAsync(e, ct);
        public Task<int> CountUnresolvedAsync(CancellationToken ct) => db.CountUnresolvedAsync(ct);
    }

    [Fact]
    public async Task I29_FailedWriteOfUnconfirmedCleanupStillBlocksTheNextQueue()
    {
        var db = new LocalDatabase(Path.Combine(_root, "i29.db"));
        await db.InitializeAsync();
        var scheduler = new QueueScheduler(new FailingTerminalJournal(db), new NoProcessSupervisor(), Options);
        var first = Completes(GameId.Genshin);
        first.ReconcileResult = CleanupState.Unconfirmed;
        var r1 = await scheduler.RunAsync([new QueueItem(GameId.Genshin, first)]);
        Assert.Equal(QueueStatus.Blocked, r1.Status);
        Assert.True(r1.JournalFaulted);

        var next = Completes(GameId.StarRail);
        var r2 = await scheduler.RunAsync([new QueueItem(GameId.StarRail, next)]);

        Assert.Equal(QueueStatus.Blocked, r2.Status);
        Assert.Equal(ReasonCodes.UnresolvedRuns, r2.BlockReason);
        Assert.Equal(0, next.StartCount);
        Assert.False(scheduler.IsRunning);
    }

    [Fact]
    public async Task I29_NonTerminalPersistedRecordCountsAsUnresolved()
    {
        var db = new LocalDatabase(Path.Combine(_root, "nonterminal.db"));
        await db.InitializeAsync();
        await db.PublishAsync(new RunUpdate(Guid.NewGuid(), Guid.NewGuid(), GameId.Genshin, "r", true,
            RunPhase.Reconciling, RunOutcome.Success, CleanupState.Pending, ReasonCodes.Completed, Start), default);
        Assert.Equal(1, await db.CountUnresolvedAsync(default));

        var next = Completes(GameId.StarRail);
        var result = await new QueueScheduler(db, new NoProcessSupervisor(), Options).RunAsync([new QueueItem(GameId.StarRail, next)]);
        Assert.Equal(QueueStatus.Blocked, result.Status);
        Assert.Equal(0, next.StartCount);
    }

    private async Task<(LocalDatabase Db, FailingTerminalJournal Journal, QueueScheduler Scheduler, Guid RunId)> FailedWriteSetupAsync(string name)
    {
        var db = new LocalDatabase(Path.Combine(_root, name));
        await db.InitializeAsync();
        var journal = new FailingTerminalJournal(db);
        var scheduler = new QueueScheduler(journal, new NoProcessSupervisor(), Options);
        var first = Completes(GameId.Genshin);
        first.ReconcileResult = CleanupState.Unconfirmed;
        var r1 = await scheduler.RunAsync([new QueueItem(GameId.Genshin, first)]);
        return (db, journal, scheduler, r1.Runs.Single().RunId);
    }

    [Fact]
    public async Task I29_HazardIsVisibleAndMarkedNotPersisted()
    {
        var (db, _, scheduler, runId) = await FailedWriteSetupAsync("visible.db");
        var hazard = Assert.Single(scheduler.SessionHazards);
        Assert.Equal(runId, hazard.RunId);
        Assert.True(hazard.RequiresAcknowledgement);
        Assert.False(hazard.TerminalPersisted);
        Assert.Equal(0, hazard.PendingOperations);
        Assert.Equal(CleanupState.Unconfirmed, hazard.LastKnown!.Cleanup);
        // The database still holds the last non-terminal state, which it now reports as unresolved too.
        var stored = Assert.Single(await db.ReadUnresolvedAsync());
        Assert.Equal(RunPhase.Reconciling, stored.Phase);
        Assert.True(stored.IsUnresolved);
    }

    [Fact]
    public async Task I29_AcknowledgementWhilePersistenceStillFailsKeepsTheBarrierClosed()
    {
        var (db, _, scheduler, runId) = await FailedWriteSetupAsync("still-failing.db");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scheduler.AcknowledgeAsync(runId, "已确认", db, Start));
        Assert.Single(scheduler.SessionHazards);
        var next = Completes(GameId.StarRail);
        Assert.Equal(QueueStatus.Blocked, (await scheduler.RunAsync([new QueueItem(GameId.StarRail, next)])).Status);
        Assert.Equal(0, next.StartCount);
    }

    [Fact]
    public async Task I29_AcknowledgementAfterRecoveryPersistsFinalStateThenReopens()
    {
        var (db, journal, scheduler, runId) = await FailedWriteSetupAsync("recovered.db");
        journal.Fail = false; // e.g. the disk lock was released

        await scheduler.AcknowledgeAsync(runId, "已确认引擎和游戏都已关闭", db, Start);

        Assert.Empty(scheduler.SessionHazards);
        var record = (await db.ReadRecentRunsAsync(10)).Single(r => r.RunId == runId);
        Assert.Equal(RunPhase.Blocked, record.Phase);              // final state was written first
        Assert.Equal(CleanupState.Unconfirmed, record.Cleanup);    // verdict is not rewritten
        Assert.Equal("已确认引擎和游戏都已关闭", record.AcknowledgementNote);
        var next = Completes(GameId.StarRail);
        Assert.Equal(QueueStatus.Completed, (await scheduler.RunAsync([new QueueItem(GameId.StarRail, next)])).Status);
        Assert.Equal(1, next.StartCount);
    }

    [Fact]
    public async Task I29_AcknowledgingOnlyInTheDatabaseDoesNotOpenTheInSessionBarrier()
    {
        var db = new LocalDatabase(Path.Combine(_root, "direct.db"));
        await db.InitializeAsync();
        var scheduler = new QueueScheduler(db, new NoProcessSupervisor(), Options);
        var first = Completes(GameId.Genshin);
        first.ReconcileResult = CleanupState.Unconfirmed;
        var runId = (await scheduler.RunAsync([new QueueItem(GameId.Genshin, first)])).Runs.Single().RunId;

        await db.AcknowledgeAsync(runId, "绕过调度器确认", Start); // the database alone is not the authority in-session

        var next = Completes(GameId.StarRail);
        Assert.Equal(QueueStatus.Blocked, (await scheduler.RunAsync([new QueueItem(GameId.StarRail, next)])).Status);
        Assert.Equal(0, next.StartCount);
    }

    [Fact]
    public async Task I29_RestartReconciliationStillClosesAndAcknowledgesInterruptedRuns()
    {
        var (db, _, _, runId) = await FailedWriteSetupAsync("restart.db");
        // A new process (fresh scheduler, empty barrier) after the failed session.
        var reconciled = Assert.Single(await db.ReconcileInterruptedRunsAsync(Start));
        Assert.Equal(runId, reconciled.RunId);
        Assert.Equal(RunPhase.Blocked, reconciled.Phase);
        Assert.Equal(CleanupState.Unconfirmed, reconciled.Cleanup);
        // The outcome decided before the failure is kept, but it can never be presented as a clean finish.
        Assert.Equal(RunOutcome.Success, reconciled.Outcome);
        Assert.Equal(ReasonCodes.Completed, reconciled.ReasonCode);

        var restarted = new QueueScheduler(db, new NoProcessSupervisor(), Options);
        var next = Completes(GameId.StarRail);
        Assert.Equal(QueueStatus.Blocked, (await restarted.RunAsync([new QueueItem(GameId.StarRail, next)])).Status);
        await restarted.AcknowledgeAsync(runId, "重启后确认", db, Start);
        Assert.Equal(QueueStatus.Completed, (await restarted.RunAsync([new QueueItem(GameId.StarRail, next)])).Status);
    }
}
