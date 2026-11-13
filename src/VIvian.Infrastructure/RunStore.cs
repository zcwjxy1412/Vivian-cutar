using System.Globalization;
using Microsoft.Data.Sqlite;
using VIvian.Core;
using VIvian.Core.Execution;

namespace VIvian.Infrastructure;

// Run persistence for M2. Every published update also lands in run_transitions, so the history of a run
// (including crash reconciliation) is auditable and never silently rewritten.
public sealed partial class LocalDatabase : IRunJournal, IRunHistory
{
    public const int MaxEventMessageLength = 4000;
    public const int MaxAcknowledgementLength = 500;
    // Unresolved: cleanup unconfirmed and not acknowledged, or a run that never reached a terminal phase (its final
    // write failed or the app stopped). Single-instance + one queue at a time: at queue start no row is legitimately
    // in progress, so a non-terminal row always means the outcome of an earlier run is unknown (#29).
    private const string UnresolvedCondition =
        "((cleanup='Unconfirmed' AND acknowledged_at IS NULL) OR phase NOT IN ('Finished','Blocked'))";
    private const string RunColumns = "run_id,queue_id,game,rule_version,is_simulation,phase,outcome,cleanup,reason_code,created_at,updated_at,acknowledged_at,acknowledgement_note";

    public async Task PublishAsync(RunUpdate update, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        using var tx = c.BeginTransaction();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            // Identity columns can never change after the first write; a mismatch updates nothing and is rejected.
            cmd.CommandText = """
                INSERT INTO runs(run_id,queue_id,game,rule_version,is_simulation,phase,outcome,cleanup,reason_code,created_at,updated_at)
                VALUES($run,$queue,$game,$rule,$sim,$phase,$outcome,$cleanup,$reason,$at,$at)
                ON CONFLICT(run_id) DO UPDATE SET phase=excluded.phase, outcome=excluded.outcome,
                    cleanup=excluded.cleanup, reason_code=excluded.reason_code, updated_at=excluded.updated_at
                WHERE runs.queue_id=excluded.queue_id AND runs.game=excluded.game
                    AND runs.is_simulation=excluded.is_simulation AND runs.rule_version=excluded.rule_version
                    AND runs.phase NOT IN ('Finished','Blocked');
                """;
            cmd.Parameters.AddWithValue("$run", update.RunId.ToString("D"));
            cmd.Parameters.AddWithValue("$queue", update.QueueId.ToString("D"));
            cmd.Parameters.AddWithValue("$game", (int)update.Game);
            cmd.Parameters.AddWithValue("$rule", update.RuleVersion);
            cmd.Parameters.AddWithValue("$sim", update.IsSimulation ? 1 : 0);
            AddState(cmd, update.Phase, update.Outcome, update.Cleanup, update.ReasonCode);
            cmd.Parameters.AddWithValue("$at", Format(update.At));
            if (await cmd.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException("运行记录已结束或标识不一致，拒绝覆盖。");
        }
        await InsertTransitionAsync(c, tx, update.RunId, update.Phase, update.Outcome, update.Cleanup,
            update.ReasonCode, update.At, "scheduler", ct);
        tx.Commit();
    }

    public async Task AppendEventsAsync(IReadOnlyList<RunEvent> events, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0) return;
        await using var c = await OpenAsync(ct);
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO run_events(run_id,at,kind,message) VALUES($run,$at,$kind,$message);";
        var run = cmd.Parameters.Add("$run", SqliteType.Text);
        var at = cmd.Parameters.Add("$at", SqliteType.Text);
        var kind = cmd.Parameters.Add("$kind", SqliteType.Text);
        var message = cmd.Parameters.Add("$message", SqliteType.Text);
        foreach (var runEvent in events)
        {
            run.Value = runEvent.RunId.ToString("D");
            at.Value = Format(runEvent.At);
            kind.Value = runEvent.Kind.ToString();
            message.Value = runEvent.Message.Length > MaxEventMessageLength
                ? runEvent.Message[..MaxEventMessageLength] + "…" : runEvent.Message;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        tx.Commit();
    }

    public async Task<int> CountUnresolvedAsync(CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM runs WHERE {UnresolvedCondition};";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    public Task<IReadOnlyList<RunRecord>> ReadRecentRunsAsync(int limit, CancellationToken ct = default)
    {
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        return QueryRunsAsync($"SELECT {RunColumns} FROM runs ORDER BY created_at DESC, run_id DESC LIMIT $limit;",
            cmd => cmd.Parameters.AddWithValue("$limit", limit), ct);
    }

    public Task<IReadOnlyList<RunRecord>> ReadBoundRunsAsync(GameId game, Guid? accountId, int limit, CancellationToken ct = default)
    {
        if (limit is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(limit));
        return QueryRunsAsync($"""
            SELECT {RunColumns} FROM runs
            WHERE game=$game AND ($account IS NULL OR EXISTS (
                SELECT 1 FROM account_execution_history h
                JOIN batch_execution_records b ON b.id=h.batch_id AND b.game=runs.game
                WHERE h.account_id=$account AND h.run_id=runs.run_id))
            ORDER BY created_at DESC, run_id DESC LIMIT $limit;
            """, cmd =>
            {
                cmd.Parameters.AddWithValue("$game", (int)game);
                cmd.Parameters.AddWithValue("$account", accountId is { } id ? DatabaseId(id) : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("$limit", limit);
            }, ct);
    }

    public Task<IReadOnlyList<RunRecord>> ReadUnresolvedAsync(CancellationToken ct = default) =>
        QueryRunsAsync($"SELECT {RunColumns} FROM runs WHERE {UnresolvedCondition} ORDER BY created_at;",
            _ => { }, ct);

    public async Task<IReadOnlyList<RunEvent>> ReadEventsAsync(Guid runId, int limit, CancellationToken ct = default)
    {
        if (limit is < 1 or > 5000) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        // Latest events, returned oldest first.
        cmd.CommandText = """
            SELECT run_id,at,kind,message FROM
                (SELECT id,run_id,at,kind,message FROM run_events WHERE run_id=$run ORDER BY id DESC LIMIT $limit)
            ORDER BY id;
            """;
        cmd.Parameters.AddWithValue("$run", runId.ToString("D"));
        cmd.Parameters.AddWithValue("$limit", limit);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var result = new List<RunEvent>();
        while (await r.ReadAsync(ct))
            result.Add(new RunEvent(Guid.Parse(r.GetString(0)), Parse(r.GetString(1)),
                Enum.Parse<RunEventKind>(r.GetString(2)), r.GetString(3)));
        return result;
    }

    public async Task<IReadOnlyList<RunRecord>> ReconcileInterruptedRunsAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        using var tx = c.BeginTransaction();
        var open = new List<(string RunId, RunPhase Phase, RunOutcome? Outcome, string? Reason)>();
        using (var select = c.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText = "SELECT run_id,phase,outcome,reason_code FROM runs WHERE phase NOT IN ('Finished','Blocked');";
            await using var r = await select.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                open.Add((r.GetString(0), Enum.Parse<RunPhase>(r.GetString(1)),
                    r.IsDBNull(2) ? null : Enum.Parse<RunOutcome>(r.GetString(2)), r.IsDBNull(3) ? null : r.GetString(3)));
        }
        foreach (var run in open)
        {
            // Never started: nothing can be running. Started: a process may survive the crash, so cleanup is
            // unconfirmed and the run is blocked. An outcome decided before the crash is kept; a missing one
            // becomes Indeterminate. Nothing is ever marked successful here.
            var neverStarted = run.Phase is RunPhase.Pending or RunPhase.Preflight;
            var phase = neverStarted ? RunPhase.Finished : RunPhase.Blocked;
            var outcome = neverStarted ? RunOutcome.Cancelled : run.Outcome ?? RunOutcome.Indeterminate;
            var cleanup = neverStarted ? CleanupState.NotRequired : CleanupState.Unconfirmed;
            var reason = run.Outcome is null ? ReasonCodes.InterruptedByCrash : run.Reason;
            using var update = c.CreateCommand();
            update.Transaction = tx;
            update.CommandText = """
                UPDATE runs SET phase=$phase,outcome=$outcome,cleanup=$cleanup,reason_code=$reason,updated_at=$at
                WHERE run_id=$run AND phase NOT IN ('Finished','Blocked');
                """;
            AddState(update, phase, outcome, cleanup, reason);
            update.Parameters.AddWithValue("$at", Format(now));
            update.Parameters.AddWithValue("$run", run.RunId);
            await update.ExecuteNonQueryAsync(ct);
            await InsertTransitionAsync(c, tx, Guid.Parse(run.RunId), phase, outcome, cleanup, reason, now, "crash-reconcile", ct);
        }
        tx.Commit();
        if (open.Count == 0) return [];
        var ids = open.Select(o => o.RunId).ToHashSet();
        return (await ReadAllRunsAsync(ct)).Where(r => ids.Contains(r.RunId.ToString("D"))).ToList();
    }

    public async Task AcknowledgeAsync(Guid runId, string note, DateTimeOffset at, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(note);
        note = note.Trim();
        if (note.Length > MaxAcknowledgementLength || note.Any(ch => char.IsControl(ch) && ch is not ('\n' or '\r')))
            throw new ArgumentException("备注过长或包含控制字符。", nameof(note));
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE runs SET acknowledged_at=$at, acknowledgement_note=$note
            WHERE run_id=$run AND cleanup='Unconfirmed' AND acknowledged_at IS NULL
                AND phase IN ('Finished','Blocked');
            """;
        cmd.Parameters.AddWithValue("$at", Format(at));
        cmd.Parameters.AddWithValue("$note", note);
        cmd.Parameters.AddWithValue("$run", runId.ToString("D"));
        if (await cmd.ExecuteNonQueryAsync(ct) != 1)
            throw new InvalidOperationException("该运行记录不存在、仍在进行中，或无需确认。");
    }

    private Task<IReadOnlyList<RunRecord>> ReadAllRunsAsync(CancellationToken ct) =>
        QueryRunsAsync($"SELECT {RunColumns} FROM runs;", _ => { }, ct);

    private async Task<IReadOnlyList<RunRecord>> QueryRunsAsync(string sql, Action<SqliteCommand> bind, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        bind(cmd);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var result = new List<RunRecord>();
        while (await r.ReadAsync(ct))
        {
            result.Add(new RunRecord(
                Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), (GameId)r.GetInt32(2), r.GetString(3),
                r.GetInt32(4) == 1, Enum.Parse<RunPhase>(r.GetString(5)),
                r.IsDBNull(6) ? null : Enum.Parse<RunOutcome>(r.GetString(6)), Enum.Parse<CleanupState>(r.GetString(7)),
                r.IsDBNull(8) ? null : r.GetString(8), Parse(r.GetString(9)), Parse(r.GetString(10)),
                r.IsDBNull(11) ? null : Parse(r.GetString(11)), r.IsDBNull(12) ? null : r.GetString(12)));
        }
        return result;
    }

    private static async Task InsertTransitionAsync(SqliteConnection c, SqliteTransaction tx, Guid runId, RunPhase phase,
        RunOutcome? outcome, CleanupState cleanup, string? reason, DateTimeOffset at, string source, CancellationToken ct)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO run_transitions(run_id,phase,outcome,cleanup,reason_code,at,source)
            VALUES($run,$phase,$outcome,$cleanup,$reason,$at,$source);
            """;
        cmd.Parameters.AddWithValue("$run", runId.ToString("D"));
        AddState(cmd, phase, outcome, cleanup, reason);
        cmd.Parameters.AddWithValue("$at", Format(at));
        cmd.Parameters.AddWithValue("$source", source);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void AddState(SqliteCommand cmd, RunPhase phase, RunOutcome? outcome, CleanupState cleanup, string? reason)
    {
        cmd.Parameters.AddWithValue("$phase", phase.ToString());
        cmd.Parameters.AddWithValue("$outcome", (object?)outcome?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cleanup", cleanup.ToString());
        cmd.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
