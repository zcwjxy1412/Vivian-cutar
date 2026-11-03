using Microsoft.Data.Sqlite;
using VIvian.Core;
using VIvian.Core.Execution;
using VIvian.Infrastructure;
using Xunit;

namespace VIvian.Tests.Execution;

public sealed class RunStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vivian-tests", Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_root, "test.db");
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    public RunStoreTests() => Directory.CreateDirectory(_root);
    public void Dispose() => TestDirectories.DeleteWithRetry(_root);

    private async Task<LocalDatabase> CreateAsync()
    {
        var db = new LocalDatabase(DbPath);
        await db.InitializeAsync();
        return db;
    }

    private static RunUpdate Update(Guid run, RunPhase phase, RunOutcome? outcome = null,
        CleanupState cleanup = CleanupState.NotRequired, string? reason = null, int minutes = 0, GameId game = GameId.Genshin)
        => new(run, QueueId, game, "rules-1", true, phase, outcome, cleanup, reason, T0.AddMinutes(minutes));

    private static readonly Guid QueueId = Guid.NewGuid();

    private async Task<long> ScalarAsync(string sql)
    {
        await using var c = new SqliteConnection("Pooling=False;Data Source=" + DbPath);
        await c.OpenAsync();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task M1DatabaseUpgradesToV2AndKeepsSettingsAndDiagnostics()
    {
        // Exactly the schema M1 (v0.1.0) created.
        await using (var c = new SqliteConnection("Pooling=False;Data Source=" + DbPath))
        {
            await c.OpenAsync();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE engine_settings(game INTEGER PRIMARY KEY CHECK(game BETWEEN 0 AND 2),
                    install_path TEXT NOT NULL, revision INTEGER NOT NULL CHECK(revision>=0));
                INSERT INTO engine_settings VALUES(0,'E:\游戏 目录\原神',3),(1,'',0),(2,'D:\ZZZ',1);
                CREATE TABLE diagnostics(id TEXT PRIMARY KEY, started_at TEXT NOT NULL, summary TEXT NOT NULL);
                CREATE INDEX ix_diagnostics_time ON diagnostics(started_at DESC);
                INSERT INTO diagnostics VALUES('d1','2026-11-01T00:00:00.0000000+00:00','基础自检通过');
                PRAGMA user_version=1;
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var db = await CreateAsync();

        Assert.Equal(2, await ScalarAsync("PRAGMA user_version;"));
        var settings = await db.LoadSettingsAsync();
        Assert.Equal(@"E:\游戏 目录\原神", settings[0].InstallPath);
        Assert.Equal(3, settings[0].Revision);
        Assert.Equal(@"D:\ZZZ", settings[2].InstallPath);
        Assert.Equal("d1", (await db.ReadRecentAsync(10)).Single().Id);
        Assert.Empty(await db.ReadRecentRunsAsync(10));
    }

    [Fact]
    public async Task FreshDatabaseIsCreatedAtCurrentVersionAndReinitializeIsHarmless()
    {
        var db = await CreateAsync();
        var run = Guid.NewGuid();
        await db.PublishAsync(Update(run, RunPhase.Pending), default);
        await db.InitializeAsync();
        Assert.Equal(LocalDatabase.CurrentSchemaVersion, await ScalarAsync("PRAGMA user_version;"));
        Assert.Single(await db.ReadRecentRunsAsync(10));
    }

    [Fact]
    public async Task FailedMigrationRollsBackCompletely()
    {
        await using (var c = new SqliteConnection("Pooling=False;Data Source=" + DbPath))
        {
            await c.OpenAsync();
            using var cmd = c.CreateCommand();
            // A v1 database that already has a conflicting 'runs' table makes the v2 step fail.
            cmd.CommandText = """
                CREATE TABLE engine_settings(game INTEGER PRIMARY KEY, install_path TEXT NOT NULL, revision INTEGER NOT NULL);
                INSERT INTO engine_settings VALUES(0,'a',0),(1,'b',0),(2,'c',0);
                CREATE TABLE diagnostics(id TEXT PRIMARY KEY, started_at TEXT NOT NULL, summary TEXT NOT NULL);
                CREATE TABLE runs(x INTEGER);
                PRAGMA user_version=1;
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<SqliteException>(() => new LocalDatabase(DbPath).InitializeAsync());
        Assert.Equal(1, await ScalarAsync("PRAGMA user_version;"));
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM sqlite_master WHERE name='run_events';"));
        Assert.Equal(3, (await new LocalDatabase(DbPath).LoadSettingsAsync()).Count);
    }

    [Fact]
    public async Task NewerSchemaVersionIsRejectedWithoutChanges()
    {
        await CreateAsync();
        await using (var c = new SqliteConnection("Pooling=False;Data Source=" + DbPath))
        {
            await c.OpenAsync();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA user_version=3;";
            await cmd.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => new LocalDatabase(DbPath).InitializeAsync());
        Assert.Equal(3, await ScalarAsync("PRAGMA user_version;"));
    }

    [Fact]
    public async Task PublishedUpdatesKeepLatestStateAndFullTransitionHistory()
    {
        var db = await CreateAsync();
        var run = Guid.NewGuid();
        await db.PublishAsync(Update(run, RunPhase.Pending), default);
        await db.PublishAsync(Update(run, RunPhase.Preflight, minutes: 1), default);
        await db.PublishAsync(Update(run, RunPhase.Starting, cleanup: CleanupState.Pending, minutes: 2), default);
        await db.PublishAsync(Update(run, RunPhase.Running, cleanup: CleanupState.Pending, minutes: 3), default);
        await db.PublishAsync(Update(run, RunPhase.Finished, RunOutcome.Success, CleanupState.Confirmed, ReasonCodes.Completed, 9), default);

        var record = (await db.ReadRecentRunsAsync(10)).Single();
        Assert.Equal(RunPhase.Finished, record.Phase);
        Assert.Equal(RunOutcome.Success, record.Outcome);
        Assert.Equal(CleanupState.Confirmed, record.Cleanup);
        Assert.True(record.IsSimulation);
        Assert.Equal(T0, record.CreatedAt);
        Assert.Equal(T0.AddMinutes(9), record.UpdatedAt);
        Assert.Equal(5, await ScalarAsync("SELECT COUNT(*) FROM run_transitions;"));
    }

    [Fact]
    public async Task TerminalRunCannotBeOverwritten()
    {
        var db = await CreateAsync();
        var run = Guid.NewGuid();
        await db.PublishAsync(Update(run, RunPhase.Pending), default);
        await db.PublishAsync(Update(run, RunPhase.Finished, RunOutcome.Failed, CleanupState.Confirmed, ReasonCodes.ReportedFailure), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.PublishAsync(Update(run, RunPhase.Finished, RunOutcome.Success, CleanupState.Confirmed, ReasonCodes.Completed), default));
        Assert.Equal(RunOutcome.Failed, (await db.ReadRecentRunsAsync(1)).Single().Outcome);
    }

    [Fact]
    public async Task RunIdentityCannotChange()
    {
        var db = await CreateAsync();
        var run = Guid.NewGuid();
        await db.PublishAsync(Update(run, RunPhase.Pending), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.PublishAsync(Update(run, RunPhase.Preflight, game: GameId.StarRail), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.PublishAsync(Update(run, RunPhase.Preflight) with { IsSimulation = false }, default));
        Assert.Equal(GameId.Genshin, (await db.ReadRecentRunsAsync(1)).Single().Game);
    }

    [Fact]
    public async Task EventsAreStoredInOrderTruncatedAndBoundToExistingRuns()
    {
        var db = await CreateAsync();
        var run = Guid.NewGuid();
        await db.PublishAsync(Update(run, RunPhase.Pending), default);
        await db.AppendEventsAsync(Enumerable.Range(0, 5)
            .Select(i => new RunEvent(run, T0.AddSeconds(i), RunEventKind.Output, "line " + i)).ToList(), default);
        await db.AppendEventsAsync([new RunEvent(run, T0.AddSeconds(9), RunEventKind.Warning, new string('x', 10_000))], default);

        var latest = await db.ReadEventsAsync(run, 3);
        Assert.Equal(new[] { "line 3", "line 4" }, latest.Take(2).Select(e => e.Message));
        Assert.Equal(LocalDatabase.MaxEventMessageLength + 1, latest[2].Message.Length);
        Assert.Equal(RunEventKind.Warning, latest[2].Kind);

        // A batch containing an event for an unknown run is rejected as a whole.
        await Assert.ThrowsAsync<SqliteException>(() => db.AppendEventsAsync(
            [new RunEvent(run, T0, RunEventKind.Output, "valid"), new RunEvent(Guid.NewGuid(), T0, RunEventKind.Output, "orphan")], default));
        Assert.Equal(6, (await db.ReadEventsAsync(run, 100)).Count);
    }

    [Fact]
    public async Task CrashReconciliationNeverInventsSuccess()
    {
        var db = await CreateAsync();
        Guid pending = Guid.NewGuid(), preflight = Guid.NewGuid(), starting = Guid.NewGuid(), running = Guid.NewGuid(),
            stopping = Guid.NewGuid(), reconcilingSuccess = Guid.NewGuid(), finished = Guid.NewGuid();
        await db.PublishAsync(Update(pending, RunPhase.Pending), default);
        await db.PublishAsync(Update(preflight, RunPhase.Preflight), default);
        await db.PublishAsync(Update(starting, RunPhase.Starting, cleanup: CleanupState.Pending), default);
        await db.PublishAsync(Update(running, RunPhase.Running, cleanup: CleanupState.Pending), default);
        await db.PublishAsync(Update(stopping, RunPhase.Stopping, cleanup: CleanupState.Pending), default);
        await db.PublishAsync(Update(reconcilingSuccess, RunPhase.Reconciling, RunOutcome.Success, CleanupState.Pending, ReasonCodes.Completed), default);
        await db.PublishAsync(Update(finished, RunPhase.Finished, RunOutcome.Success, CleanupState.Confirmed, ReasonCodes.Completed), default);

        var reconciled = await new LocalDatabase(DbPath).ReconcileInterruptedRunsAsync(T0.AddHours(1));
        Assert.Equal(6, reconciled.Count);
        var all = (await db.ReadRecentRunsAsync(50)).ToDictionary(r => r.RunId);

        foreach (var id in new[] { pending, preflight })
        {
            Assert.Equal(RunPhase.Finished, all[id].Phase);
            Assert.Equal(RunOutcome.Cancelled, all[id].Outcome);
            Assert.Equal(CleanupState.NotRequired, all[id].Cleanup);
            Assert.Equal(ReasonCodes.InterruptedByCrash, all[id].ReasonCode);
        }
        foreach (var id in new[] { starting, running, stopping })
        {
            Assert.Equal(RunPhase.Blocked, all[id].Phase);
            Assert.Equal(RunOutcome.Indeterminate, all[id].Outcome);
            Assert.Equal(CleanupState.Unconfirmed, all[id].Cleanup);
            Assert.Equal(ReasonCodes.InterruptedByCrash, all[id].ReasonCode);
        }
        // The outcome decided before the crash is kept, but cleanup is still unknown.
        Assert.Equal(RunOutcome.Success, all[reconcilingSuccess].Outcome);
        Assert.Equal(CleanupState.Unconfirmed, all[reconcilingSuccess].Cleanup);
        Assert.Equal(RunPhase.Blocked, all[reconcilingSuccess].Phase);
        Assert.Equal(T0, all[finished].UpdatedAt);

        Assert.Equal(4, await db.CountUnresolvedAsync(default));
        Assert.Equal(6, await ScalarAsync("SELECT COUNT(*) FROM run_transitions WHERE source='crash-reconcile';"));
        Assert.Empty(await db.ReconcileInterruptedRunsAsync(T0.AddHours(2)));
    }

    [Fact]
    public async Task AcknowledgementResolvesWithoutRewritingTheVerdict()
    {
        var db = await CreateAsync();
        var run = Guid.NewGuid();
        await db.PublishAsync(Update(run, RunPhase.Running, cleanup: CleanupState.Pending), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.AcknowledgeAsync(run, "too early", T0));
        await db.ReconcileInterruptedRunsAsync(T0.AddMinutes(5));
        Assert.Single(await db.ReadUnresolvedAsync());

        await db.AcknowledgeAsync(run, "已手动确认引擎和游戏都已关闭", T0.AddMinutes(6));

        Assert.Equal(0, await db.CountUnresolvedAsync(default));
        var record = (await db.ReadRecentRunsAsync(1)).Single();
        Assert.Equal(RunOutcome.Indeterminate, record.Outcome);
        Assert.Equal(CleanupState.Unconfirmed, record.Cleanup);
        Assert.Equal("已手动确认引擎和游戏都已关闭", record.AcknowledgementNote);
        Assert.False(record.IsUnresolved);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.AcknowledgeAsync(run, "again", T0));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.AcknowledgeAsync(Guid.NewGuid(), "unknown", T0));
        await Assert.ThrowsAsync<ArgumentException>(() => db.AcknowledgeAsync(run, new string('x', 501), T0));
    }

    [Fact]
    public async Task SchedulerPersistsAFullQueueThroughSqlite()
    {
        var db = await CreateAsync();
        var adapter = new ScriptedAdapter(GameId.StarRail)
        {
            Script = run => { run.Emit("开始"); run.Emit("完成"); run.End(EvidenceKind.CompletionReported); return Task.CompletedTask; },
        };
        var result = await new QueueScheduler(db, new NoProcessSupervisor()).RunAsync([new QueueItem(GameId.StarRail, adapter)]);

        var record = (await db.ReadRecentRunsAsync(5)).Single();
        Assert.Equal(result.Runs.Single().RunId, record.RunId);
        Assert.Equal(RunOutcome.Success, record.Outcome);
        Assert.Equal(new[] { "开始", "完成" }, (await db.ReadEventsAsync(record.RunId, 10)).Select(e => e.Message));
        Assert.Equal(6, await ScalarAsync("SELECT COUNT(*) FROM run_transitions;"));
        Assert.False(result.JournalFaulted);
    }

    [Fact]
    public async Task CrashMidRunBlocksNewQueuesUntilAcknowledged()
    {
        var db = await CreateAsync();
        var started = new TaskCompletionSource();
        var hung = new ScriptedAdapter(GameId.Genshin) { Script = _ => { started.SetResult(); return Task.CompletedTask; } };
        var crashed = new QueueScheduler(db, new NoProcessSupervisor());
        var abandoned = crashed.RunAsync([new QueueItem(GameId.Genshin, hung)]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // A new process starts while the old one "crashed" with its run still marked Running.
        var restarted = new LocalDatabase(DbPath);
        var reconciled = await restarted.ReconcileInterruptedRunsAsync(DateTimeOffset.UtcNow);
        Assert.Equal(RunPhase.Blocked, reconciled.Single().Phase);

        var next = new ScriptedAdapter(GameId.StarRail) { Script = r => { r.End(EvidenceKind.CompletionReported); return Task.CompletedTask; } };
        var scheduler = new QueueScheduler(restarted, new NoProcessSupervisor());
        var refused = await scheduler.RunAsync([new QueueItem(GameId.StarRail, next)]);
        Assert.Equal(QueueStatus.Blocked, refused.Status);
        Assert.Equal(ReasonCodes.UnresolvedRuns, refused.BlockReason);
        Assert.Equal(0, next.StartCount);

        await restarted.AcknowledgeAsync(reconciled.Single().RunId, "确认已关闭", DateTimeOffset.UtcNow);
        var allowed = await scheduler.RunAsync([new QueueItem(GameId.StarRail, next)]);
        Assert.Equal(QueueStatus.Completed, allowed.Status);

        crashed.EmergencyStop();
        await abandoned.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
