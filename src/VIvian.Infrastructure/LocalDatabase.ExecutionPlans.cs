using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VIvian.Core.Execution;

namespace VIvian.Infrastructure;

public sealed partial class LocalDatabase : IExecutionPlanStore
{
    private const string SchemaV8 = """
        CREATE TABLE execution_plans(plan_id TEXT PRIMARY KEY, state TEXT NOT NULL, receipt_json TEXT NOT NULL);
        CREATE TABLE execution_plan_runs(run_id TEXT PRIMARY KEY,
            plan_id TEXT NOT NULL REFERENCES execution_plans(plan_id), ordinal INTEGER NOT NULL CHECK(ordinal>=0));
        CREATE INDEX ix_execution_plan_runs_plan ON execution_plan_runs(plan_id,ordinal);
        PRAGMA user_version=8;
        """;

    public async Task<ExecutionPlanSubmission> AcceptAsync(PreparedExecutionPlan plan, CancellationToken ct = default)
    {
        // Copy before the first await: neither caller list mutation nor time-zone representation changes the payload.
        var frozen = FreezePlan(plan);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(frozen))));
        await using var c = await OpenAsync(ct);
        using var tx = c.BeginTransaction(deferred: false);
        var previous = await ReadPlanAsync(c, tx, plan.PlanId, ct);
        if (previous is not null)
            return previous.PayloadHash == hash
                ? new(ExecutionPlanDisposition.Existing, previous)
                : new(ExecutionPlanDisposition.Conflict, null, "plan_payload_conflict");
        var now = DateTimeOffset.UtcNow;
        if (frozen.ExpiresAt <= now) return new(ExecutionPlanDisposition.Rejected, null, "plan_expired");
        var receipt = new ExecutionPlanReceipt(frozen, hash, ExecutionPlanState.Accepted, now, now,
            frozen.Items.Select(item => new ExecutionPlanItemReceipt(item, Guid.NewGuid(),
                ExecutionPlanItemState.Pending, [], null)).ToArray());
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO execution_plans(plan_id,state,receipt_json) VALUES($id,$state,$json);";
        AddReceipt(cmd, receipt);
        await cmd.ExecuteNonQueryAsync(ct);
        ct.ThrowIfCancellationRequested(); tx.Commit();
        return new(ExecutionPlanDisposition.Accepted, receipt);
    }

    public async Task<ExecutionPlanReceipt?> ReadAsync(Guid planId, CancellationToken ct = default)
    {
        RequireId(planId);
        await using var c = await OpenAsync(ct);
        return await ReadPlanAsync(c, null, planId, ct);
    }

    public async Task<bool> TryStartAsync(Guid planId, CancellationToken ct = default)
    {
        RequireId(planId);
        await using var c = await OpenAsync(ct); using var tx = c.BeginTransaction(deferred: false);
        var receipt = await ReadPlanAsync(c, tx, planId, ct);
        if (receipt?.State != ExecutionPlanState.Accepted) return false;
        await SavePlanAsync(c, tx, receipt with { State = ExecutionPlanState.Running }, ct);
        ct.ThrowIfCancellationRequested(); tx.Commit(); return true;
    }

    public Task StartItemAsync(Guid planId, int ordinal, CancellationToken ct = default) =>
        ChangePlanAsync(planId, receipt =>
        {
            RequireRunning(receipt);
            var item = GetItem(receipt, ordinal);
            if (item.State != ExecutionPlanItemState.Pending || receipt.Items.Take(ordinal).Any(x =>
                    x.State != ExecutionPlanItemState.Succeeded || x.Result?.AllowsNext != true) ||
                receipt.Items.Any(x => x.State == ExecutionPlanItemState.Running))
                throw new InvalidOperationException("计划项目不满足顺序或清理条件。");
            return ReplaceItem(receipt, item with { State = ExecutionPlanItemState.Running });
        }, ct);

    public async Task RegisterRunAsync(Guid planId, int ordinal, Guid runId, CancellationToken ct = default)
    {
        RequireId(planId); RequireId(runId);
        await using var c = await OpenAsync(ct); using var tx = c.BeginTransaction(deferred: false);
        var receipt = await ReadPlanAsync(c, tx, planId, ct) ?? throw new InvalidOperationException("计划不存在。");
        RequireRunning(receipt); var item = GetItem(receipt, ordinal);
        if (item.State != ExecutionPlanItemState.Running) throw new InvalidOperationException("项目尚未运行。");
        using var check = c.CreateCommand(); check.Transaction = tx;
        check.CommandText = "SELECT plan_id,ordinal FROM execution_plan_runs WHERE run_id=$run;";
        check.Parameters.AddWithValue("$run", runId.ToString("D"));
        using (var r = await check.ExecuteReaderAsync(ct))
        {
            if (await r.ReadAsync(ct))
            {
                if (r.GetString(0) == planId.ToString("D") && r.GetInt32(1) == ordinal) return;
                throw new InvalidOperationException("运行编号已关联另一计划项目。");
            }
        }
        using var insert = c.CreateCommand(); insert.Transaction = tx;
        insert.CommandText = "INSERT INTO execution_plan_runs(run_id,plan_id,ordinal) VALUES($run,$plan,$ordinal);";
        insert.Parameters.AddWithValue("$run", runId.ToString("D"));
        insert.Parameters.AddWithValue("$plan", planId.ToString("D")); insert.Parameters.AddWithValue("$ordinal", ordinal);
        await insert.ExecuteNonQueryAsync(ct);
        await SavePlanAsync(c, tx, ReplaceItem(receipt, item with { RunIds = [.. item.RunIds, runId] }), ct);
        ct.ThrowIfCancellationRequested(); tx.Commit();
    }

    public Task CompleteItemAsync(Guid planId, int ordinal, ExecutionPlanItemResult result, CancellationToken ct = default) =>
        ChangePlanAsync(planId, receipt =>
        {
            ArgumentNullException.ThrowIfNull(result);
            if (!Enum.IsDefined(result.Outcome) || !Enum.IsDefined(result.Cleanup) || string.IsNullOrWhiteSpace(result.ReasonCode))
                throw new ArgumentException("运行结果无效。", nameof(result));
            RequireRunning(receipt); var item = GetItem(receipt, ordinal);
            if (item.State != ExecutionPlanItemState.Running) throw new InvalidOperationException("项目不在运行中。");
            // Success without a pre-launch run association is never a verified success.
            if (result.AllowsNext && item.RunIds.Count == 0) throw new InvalidOperationException("成功结果缺少运行关联。");
            var state = result.AllowsNext ? ExecutionPlanItemState.Succeeded : result.Outcome switch
            {
                RunOutcome.Failed => ExecutionPlanItemState.Failed,
                RunOutcome.Cancelled => ExecutionPlanItemState.Cancelled,
                _ => ExecutionPlanItemState.Blocked
            };
            return ReplaceItem(receipt, item with { State = state, Result = result });
        }, ct);

    public Task CompleteAsync(Guid planId, ExecutionPlanState state, string? reasonCode, CancellationToken ct = default) =>
        ChangePlanAsync(planId, receipt =>
        {
            if (state is not (ExecutionPlanState.Succeeded or ExecutionPlanState.Stopped))
                throw new ArgumentException("只能显式完成或停止计划。", nameof(state));
            if (receipt.State is not (ExecutionPlanState.Accepted or ExecutionPlanState.Running))
                throw new InvalidOperationException("计划已经终结。");
            if (receipt.Items.Any(x => x.State == ExecutionPlanItemState.Running))
                throw new InvalidOperationException("运行中项目尚未记录终态。");
            if (state == ExecutionPlanState.Succeeded && receipt.Items.Any(x => x.State != ExecutionPlanItemState.Succeeded || x.Result?.AllowsNext != true))
                throw new InvalidOperationException("计划尚未全部确认成功。");
            return receipt with { State = state, ReasonCode = reasonCode, Items = receipt.Items.Select(x =>
                x.State == ExecutionPlanItemState.Pending ? x with { State = ExecutionPlanItemState.Skipped } : x).ToArray() };
        }, ct);

    public async Task<int> InterruptUnfinishedAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct); using var tx = c.BeginTransaction(deferred: false);
        var receipts = new List<ExecutionPlanReceipt>();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT receipt_json FROM execution_plans WHERE state IN ('Accepted','Running');";
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) receipts.Add(DecodePlan(r.GetString(0)));
        }
        foreach (var receipt in receipts)
            await SavePlanAsync(c, tx, receipt with { State = ExecutionPlanState.Interrupted,
                ReasonCode = ReasonCodes.InterruptedByCrash, Items = receipt.Items.Select(x => x.State switch
                {
                    ExecutionPlanItemState.Running => x with { State = ExecutionPlanItemState.Interrupted,
                        Result = new(RunOutcome.Indeterminate, CleanupState.Unconfirmed, false, ReasonCodes.InterruptedByCrash) },
                    ExecutionPlanItemState.Pending => x with { State = ExecutionPlanItemState.Skipped },
                    _ => x
                }).ToArray() }, ct);
        ct.ThrowIfCancellationRequested(); tx.Commit(); return receipts.Count;
    }

    private async Task ChangePlanAsync(Guid id, Func<ExecutionPlanReceipt, ExecutionPlanReceipt> change, CancellationToken ct)
    {
        RequireId(id);
        await using var c = await OpenAsync(ct); using var tx = c.BeginTransaction(deferred: false);
        var receipt = await ReadPlanAsync(c, tx, id, ct) ?? throw new InvalidOperationException("计划不存在。");
        await SavePlanAsync(c, tx, change(receipt), ct);
        ct.ThrowIfCancellationRequested(); tx.Commit();
    }

    private static async Task<ExecutionPlanReceipt?> ReadPlanAsync(SqliteConnection c, SqliteTransaction? tx, Guid id, CancellationToken ct)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT receipt_json FROM execution_plans WHERE plan_id=$id;";
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        return await cmd.ExecuteScalarAsync(ct) is string json ? DecodePlan(json) : null;
    }
    private static ExecutionPlanReceipt DecodePlan(string json)
    {
        try
        {
            var receipt = JsonSerializer.Deserialize<ExecutionPlanReceipt>(json)
                ?? throw new InvalidDataException("计划回执为空。");
            var frozen = FreezePlan(receipt.Plan);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(frozen))));
            if (!Enum.IsDefined(receipt.State) || receipt.PayloadHash != hash || receipt.Items is null ||
                receipt.Items.Count != frozen.Items.Count)
                throw new InvalidDataException("计划回执结构或摘要损坏。");
            var batches = new HashSet<Guid>(); var runs = new HashSet<Guid>();
            for (var i = 0; i < receipt.Items.Count; i++)
            {
                var row = receipt.Items[i];
                if (row is null || row.Item != frozen.Items[i] || !Enum.IsDefined(row.State) ||
                    row.ChildBatchId == Guid.Empty || !batches.Add(row.ChildBatchId) || row.RunIds is null ||
                    row.RunIds.Any(id => id == Guid.Empty || !runs.Add(id)))
                    throw new InvalidDataException("计划项目结构损坏。");
                var terminal = row.State is ExecutionPlanItemState.Succeeded or ExecutionPlanItemState.Failed or
                    ExecutionPlanItemState.Cancelled or ExecutionPlanItemState.Blocked or ExecutionPlanItemState.Interrupted;
                if (terminal != (row.Result is not null) || row.Result is { } result &&
                    (!Enum.IsDefined(result.Outcome) || !Enum.IsDefined(result.Cleanup) || string.IsNullOrWhiteSpace(result.ReasonCode)) ||
                    row.State == ExecutionPlanItemState.Succeeded && (row.Result?.AllowsNext != true || row.RunIds.Count == 0))
                    throw new InvalidDataException("计划结果结构损坏。");
            }
            if (receipt.Items.Count(x => x.State == ExecutionPlanItemState.Running) > 1 ||
                receipt.State != ExecutionPlanState.Running && receipt.Items.Any(x => x.State == ExecutionPlanItemState.Running) ||
                receipt.State == ExecutionPlanState.Succeeded && receipt.Items.Any(x => x.State != ExecutionPlanItemState.Succeeded))
                throw new InvalidDataException("计划状态结构损坏。");
            return receipt;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new InvalidDataException("计划回执损坏，禁止继续执行。", ex);
        }
    }
    private static async Task SavePlanAsync(SqliteConnection c, SqliteTransaction tx, ExecutionPlanReceipt receipt, CancellationToken ct)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "UPDATE execution_plans SET state=$state,receipt_json=$json WHERE plan_id=$id;";
        AddReceipt(cmd, receipt with { UpdatedAt = DateTimeOffset.UtcNow });
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("计划回执不存在。");
    }
    private static void AddReceipt(SqliteCommand cmd, ExecutionPlanReceipt receipt)
    {
        cmd.Parameters.AddWithValue("$id", receipt.Plan.PlanId.ToString("D"));
        cmd.Parameters.AddWithValue("$state", receipt.State.ToString());
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(receipt));
    }
    private static PreparedExecutionPlan FreezePlan(PreparedExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan); RequireId(plan.PlanId);
        if (plan.Items is null || plan.Items.Count is < 1 or > 32) throw new ArgumentException("计划需要 1–32 项。", nameof(plan));
        var items = plan.Items.ToArray();
        for (var i = 0; i < items.Length; i++)
        {
            var item = items[i];
            if (item is null || item.Ordinal != i || !Enum.IsDefined(item.Game) || item.AccountId == Guid.Empty ||
                string.IsNullOrWhiteSpace(item.AccountName) || string.IsNullOrWhiteSpace(item.ConfigurationRevision) ||
                item.TaskPlanRevision < 1 || item.TaskId != "routine" || item.ResourcePolicy != "saved_account_settings" ||
                string.IsNullOrWhiteSpace(item.ResourceSummary)) throw new ArgumentException("计划项目无效。", nameof(plan));
        }
        if (items.Select(x => (x.Game, x.AccountId)).Distinct().Count() != items.Length)
            throw new ArgumentException("计划包含重复账号。", nameof(plan));
        return plan with { ExpiresAt = plan.ExpiresAt.ToUniversalTime(), Items = items };
    }
    private static void RequireId(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("编号不能为空。", nameof(id));
    }
    private static void RequireRunning(ExecutionPlanReceipt receipt)
    {
        if (receipt.State != ExecutionPlanState.Running) throw new InvalidOperationException("计划不在运行中。");
    }
    private static ExecutionPlanItemReceipt GetItem(ExecutionPlanReceipt receipt, int ordinal) =>
        ordinal >= 0 && ordinal < receipt.Items.Count ? receipt.Items[ordinal] : throw new ArgumentOutOfRangeException(nameof(ordinal));
    private static ExecutionPlanReceipt ReplaceItem(ExecutionPlanReceipt receipt, ExecutionPlanItemReceipt item) =>
        receipt with { Items = receipt.Items.Select(x => x.Item.Ordinal == item.Item.Ordinal ? item : x).ToArray() };
}
