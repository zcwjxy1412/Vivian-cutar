using Microsoft.Data.Sqlite;
using System.Text.Json;
using VIvian.Core;
using VIvian.Core.Engines;

namespace VIvian.Infrastructure;

public sealed record GameAccount(Guid Id, GameId Game, string Name, string Region, string? Uid,
    bool Enabled, bool AutoRun, int Order, string LoginState, string VerificationState,
    DateTimeOffset? LastRunAt, string? LastResult, long Revision);
public sealed record AccountSettingsSnapshot(Guid AccountId, IReadOnlyDictionary<string, string> Values, long Revision);
public sealed record AccountTaskPlan(Guid AccountId, bool DailyEnabled, long Revision);
public sealed record AccountBatch(Guid Id, GameId Game, DateTimeOffset StartedAt, DateTimeOffset UpdatedAt,
    string State, Guid? CurrentAccountId, string Detail);
public sealed record AccountExecutionRecord(Guid Id, Guid BatchId, string AccountName, Guid? RunId,
    DateTimeOffset At, string Result, string Detail);

public sealed partial class LocalDatabase
{
    public async Task<IReadOnlyList<GameAccount>> LoadAccountsAsync(GameId? game = null, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT account_id,game,display_name,region,uid,enabled,auto_run,sort_order,login_state,verification_state,last_run_at,last_result,revision FROM game_accounts WHERE ($game IS NULL OR game=$game) ORDER BY game,sort_order,account_id;";
        cmd.Parameters.AddWithValue("$game", game is null ? DBNull.Value : (object)(int)game);
        var result = new List<GameAccount>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var idText = reader.GetString(0);
            // Stable IDs from the legacy migration are not GUIDs; map them deterministically without using names.
            var id = Guid.TryParse(idText, out var parsed) ? parsed : LegacyId((GameId)reader.GetInt32(1));
            result.Add(new(id, (GameId)reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt32(5) != 0, reader.GetInt32(6) != 0,
                reader.GetInt32(7), reader.GetString(8), reader.GetString(9),
                reader.IsDBNull(10) ? null : DateTimeOffset.Parse(reader.GetString(10)),
                reader.IsDBNull(11) ? null : reader.GetString(11), reader.GetInt64(12)));
        }
        return result;
    }

    private static Guid LegacyId(GameId game) => game switch
    {
        GameId.Genshin => Guid.Parse("00000000-0000-4000-8000-000000000001"),
        GameId.StarRail => Guid.Parse("00000000-0000-4000-8000-000000000002"),
        _ => Guid.Parse("00000000-0000-4000-8000-000000000003"),
    };

    private static string DatabaseId(Guid id) => id == LegacyId(GameId.Genshin) ? "legacy-genshin"
        : id == LegacyId(GameId.StarRail) ? "legacy-star-rail"
        : id == LegacyId(GameId.ZenlessZoneZero) ? "legacy-zenless" : id.ToString("D");

    public async Task<GameAccount> AddAccountAsync(GameId game, string name, string region, string? uid, CancellationToken ct = default)
    {
        ValidateAccount(name, region, uid);
        var id = Guid.NewGuid();
        await using var c = await OpenAsync(ct);
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO game_accounts(account_id,game,display_name,region,uid,sort_order) VALUES($id,$game,$name,$region,$uid,(SELECT COALESCE(MAX(sort_order),-1)+1 FROM game_accounts WHERE game=$game)); INSERT INTO account_settings(account_id) VALUES($id); INSERT INTO account_task_plans(account_id,plan_json) VALUES($id,$plan);";
        cmd.Parameters.AddWithValue("$id", DatabaseId(id));
        cmd.Parameters.AddWithValue("$game", (int)game);
        cmd.Parameters.AddWithValue("$name", name.Trim());
        cmd.Parameters.AddWithValue("$region", region.Trim());
        cmd.Parameters.AddWithValue("$uid", uid is null ? DBNull.Value : (object)uid.Trim());
        cmd.Parameters.AddWithValue("$plan", game == GameId.StarRail ? "[{\"Id\":\"routine\",\"Enabled\":true}]" : "[]");
        await cmd.ExecuteNonQueryAsync(ct);
        tx.Commit();
        return (await LoadAccountsAsync(game, ct)).Single(a => a.Id == id);
    }

    public async Task<GameAccount> SaveAccountAsync(GameAccount account, CancellationToken ct = default)
    {
        ValidateAccount(account.Name, account.Region, account.Uid);
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE game_accounts SET display_name=$name,region=$region,uid=$uid,enabled=$enabled,auto_run=$auto,sort_order=$order,revision=revision+1 WHERE account_id=$id AND game=$game AND revision=$revision;";
        cmd.Parameters.AddWithValue("$id", DatabaseId(account.Id));
        cmd.Parameters.AddWithValue("$game", (int)account.Game);
        cmd.Parameters.AddWithValue("$name", account.Name.Trim());
        cmd.Parameters.AddWithValue("$region", account.Region.Trim());
        cmd.Parameters.AddWithValue("$uid", account.Uid is null ? DBNull.Value : (object)account.Uid.Trim());
        cmd.Parameters.AddWithValue("$enabled", account.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$auto", account.AutoRun ? 1 : 0);
        cmd.Parameters.AddWithValue("$order", account.Order);
        cmd.Parameters.AddWithValue("$revision", account.Revision);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new SettingsConflictException();
        return account with { Revision = account.Revision + 1 };
    }

    public async Task<(GameAccount Account, AccountTaskPlan Plan)> SaveAccountAndPlanAsync(
        GameAccount account, AccountTaskPlan plan, CancellationToken ct = default)
    {
        if (account.Id != plan.AccountId || account.Game != GameId.StarRail)
            throw new ArgumentException("账号与任务计划不匹配。");
        ValidateAccount(account.Name, account.Region, account.Uid);
        await using var c = await OpenAsync(ct);
        using var tx = c.BeginTransaction();
        using (var profile = c.CreateCommand())
        {
            profile.Transaction = tx;
            profile.CommandText = "UPDATE game_accounts SET display_name=$name,region=$region,uid=$uid,enabled=$enabled,auto_run=$auto,revision=revision+1 WHERE account_id=$id AND game=1 AND revision=$revision;";
            profile.Parameters.AddWithValue("$name", account.Name.Trim());
            profile.Parameters.AddWithValue("$region", account.Region.Trim());
            profile.Parameters.AddWithValue("$uid", account.Uid is null ? DBNull.Value : (object)account.Uid.Trim());
            profile.Parameters.AddWithValue("$enabled", account.Enabled ? 1 : 0);
            profile.Parameters.AddWithValue("$auto", account.AutoRun ? 1 : 0);
            profile.Parameters.AddWithValue("$id", DatabaseId(account.Id));
            profile.Parameters.AddWithValue("$revision", account.Revision);
            if (await profile.ExecuteNonQueryAsync(ct) != 1) throw new SettingsConflictException();
        }
        using (var task = c.CreateCommand())
        {
            task.Transaction = tx;
            task.CommandText = "UPDATE account_task_plans SET plan_json=$json,revision=revision+1 WHERE account_id=$id AND revision=$revision;";
            task.Parameters.AddWithValue("$json", plan.DailyEnabled ? "[{\"Id\":\"routine\",\"Enabled\":true}]" : "[]");
            task.Parameters.AddWithValue("$id", DatabaseId(plan.AccountId));
            task.Parameters.AddWithValue("$revision", plan.Revision);
            if (await task.ExecuteNonQueryAsync(ct) != 1) throw new SettingsConflictException();
        }
        tx.Commit();
        return (account with { Revision = account.Revision + 1 }, plan with { Revision = plan.Revision + 1 });
    }

    public async Task DeleteAccountAsync(Guid id, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM game_accounts WHERE account_id=$id;";
        cmd.Parameters.AddWithValue("$id", DatabaseId(id));
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("账号档案不存在。数据库中的记录未更改。");
        tx.Commit();
    }

    public async Task SwapAccountOrderAsync(GameAccount first, GameAccount second, CancellationToken ct = default)
    {
        if (first.Game != second.Game || first.Id == second.Id) throw new ArgumentException("只能调整同一游戏内两个不同账号的顺序。");
        await using var c = await OpenAsync(ct);
        using var tx = c.BeginTransaction();
        foreach (var (account, order) in new[] { (first, second.Order), (second, first.Order) })
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE game_accounts SET sort_order=$order,revision=revision+1 WHERE account_id=$id AND game=$game AND revision=$revision;";
            cmd.Parameters.AddWithValue("$order", order);
            cmd.Parameters.AddWithValue("$id", DatabaseId(account.Id));
            cmd.Parameters.AddWithValue("$game", (int)account.Game);
            cmd.Parameters.AddWithValue("$revision", account.Revision);
            if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new SettingsConflictException();
        }
        tx.Commit();
    }

    public async Task<AccountSettingsSnapshot> LoadAccountSettingsAsync(Guid id, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT settings_json,revision FROM account_settings WHERE account_id=$id;";
        cmd.Parameters.AddWithValue("$id", DatabaseId(id));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("账号设置不存在。");
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(0)) ?? [];
        return new(id, values, reader.GetInt64(1));
    }

    public async Task<AccountTaskPlan> LoadAccountTaskPlanAsync(Guid id, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT plan_json,revision FROM account_task_plans WHERE account_id=$id;";
        cmd.Parameters.AddWithValue("$id", DatabaseId(id));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("账号任务计划不存在。");
        var entries = JsonSerializer.Deserialize<List<StoredEntry>>(reader.GetString(0)) ?? [];
        return new(id, entries.Any(e => e.Id == "routine" && e.Enabled), reader.GetInt64(1));
    }

    public async Task<AccountTaskPlan> SaveAccountTaskPlanAsync(AccountTaskPlan plan, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE account_task_plans SET plan_json=$json,revision=revision+1 WHERE account_id=$id AND revision=$revision;";
        cmd.Parameters.AddWithValue("$json", plan.DailyEnabled ? "[{\"Id\":\"routine\",\"Enabled\":true}]" : "[]");
        cmd.Parameters.AddWithValue("$id", DatabaseId(plan.AccountId));
        cmd.Parameters.AddWithValue("$revision", plan.Revision);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new SettingsConflictException();
        return plan with { Revision = plan.Revision + 1 };
    }

    public async Task<AccountSettingsSnapshot> SaveAccountSettingsAsync(AccountSettingsSnapshot settings, CancellationToken ct = default)
    {
        var allowed = EngineCatalog.March7th.Parameters.Where(p => p.Access == ParameterAccess.VerifiedReadWrite)
            .ToDictionary(p => p.Path);
        foreach (var (key, value) in settings.Values)
            if (!allowed.TryGetValue(key, out var parameter) || !ParameterRules.IsValid(parameter, value))
                throw new ArgumentException("账号设置包含不支持的字段或值：" + key);
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE account_settings SET settings_json=$json,revision=revision+1 WHERE account_id=$id AND revision=$revision;";
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(settings.Values));
        cmd.Parameters.AddWithValue("$id", DatabaseId(settings.AccountId));
        cmd.Parameters.AddWithValue("$revision", settings.Revision);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new SettingsConflictException();
        return settings with { Revision = settings.Revision + 1 };
    }

    public async Task<AccountBatch> BeginBatchAsync(GameId game, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var batch = new AccountBatch(Guid.NewGuid(), game, now, now, "running", null, "等待账号登录核对");
        await SaveBatchAsync(batch, ct);
        return batch;
    }

    public async Task SaveBatchAsync(AccountBatch batch, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO batch_execution_records(id,game,started_at,updated_at,state,current_account_id,detail) VALUES($id,$game,$start,$update,$state,$account,$detail) ON CONFLICT(id) DO UPDATE SET updated_at=excluded.updated_at,state=excluded.state,current_account_id=excluded.current_account_id,detail=excluded.detail;";
        cmd.Parameters.AddWithValue("$id", batch.Id.ToString("D"));
        cmd.Parameters.AddWithValue("$game", (int)batch.Game);
        cmd.Parameters.AddWithValue("$start", batch.StartedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$update", batch.UpdatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$state", batch.State);
        cmd.Parameters.AddWithValue("$account", batch.CurrentAccountId is null ? DBNull.Value : (object)DatabaseId(batch.CurrentAccountId.Value));
        cmd.Parameters.AddWithValue("$detail", batch.Detail);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordAccountResultAsync(Guid batchId, Guid accountId, Guid? runId, string result,
        string detail, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO account_execution_history(id,batch_id,account_id,run_id,at,result,detail) VALUES($id,$batch,$account,$run,$at,$result,$detail); UPDATE game_accounts SET last_run_at=$at,last_result=$result WHERE account_id=$account;";
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        cmd.Parameters.AddWithValue("$batch", batchId.ToString("D"));
        cmd.Parameters.AddWithValue("$account", DatabaseId(accountId));
        cmd.Parameters.AddWithValue("$run", runId is null ? DBNull.Value : (object)runId.Value.ToString("D"));
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$result", result);
        cmd.Parameters.AddWithValue("$detail", detail);
        await cmd.ExecuteNonQueryAsync(ct);
        tx.Commit();
    }

    public async Task<IReadOnlyList<AccountBatch>> LoadRecentBatchesAsync(int limit = 20, CancellationToken ct = default)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,game,started_at,updated_at,state,current_account_id,detail FROM batch_execution_records ORDER BY started_at DESC LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", limit);
        var result = new List<AccountBatch>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var accountText = reader.IsDBNull(5) ? null : reader.GetString(5);
            var accountId = accountText is null ? (Guid?)null : Guid.TryParse(accountText, out var parsed)
                ? parsed : accountText switch { "legacy-genshin" => LegacyId(GameId.Genshin),
                    "legacy-star-rail" => LegacyId(GameId.StarRail), "legacy-zenless" => LegacyId(GameId.ZenlessZoneZero), _ => null };
            result.Add(new(Guid.Parse(reader.GetString(0)), (GameId)reader.GetInt32(1),
                DateTimeOffset.Parse(reader.GetString(2)), DateTimeOffset.Parse(reader.GetString(3)),
                reader.GetString(4), accountId, reader.GetString(6)));
        }
        return result;
    }

    public async Task<IReadOnlyList<AccountExecutionRecord>> LoadBatchResultsAsync(Guid batchId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT h.id,COALESCE(a.display_name,'已删除档案'),h.run_id,h.at,h.result,h.detail FROM account_execution_history h LEFT JOIN game_accounts a ON a.account_id=h.account_id WHERE h.batch_id=$batch ORDER BY h.at,h.id;";
        cmd.Parameters.AddWithValue("$batch", batchId.ToString("D"));
        var result = new List<AccountExecutionRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new(Guid.Parse(reader.GetString(0)), batchId, reader.GetString(1),
                reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)), DateTimeOffset.Parse(reader.GetString(3)),
                reader.GetString(4), reader.GetString(5)));
        return result;
    }

    public async Task ReconcileInterruptedBatchesAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE batch_execution_records SET state='interrupted',updated_at=$at,detail='VIvian 意外退出；恢复前需重新核对游戏登录状态和上游清理状态。' WHERE state='running'; UPDATE game_accounts SET login_state='unverified',verification_state='unverified' WHERE verification_state='manual_verified';";
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordAccountSwitchAsync(Guid? from, Guid to, string state, string detail,
        CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO account_switch_history(id,from_account_id,to_account_id,at,state,detail) VALUES($id,$from,$to,$at,$state,$detail); UPDATE game_accounts SET login_state=$state,verification_state=$state WHERE account_id=$to;";
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        cmd.Parameters.AddWithValue("$from", from is null ? DBNull.Value : (object)DatabaseId(from.Value));
        cmd.Parameters.AddWithValue("$to", DatabaseId(to));
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$state", state);
        cmd.Parameters.AddWithValue("$detail", detail);
        await cmd.ExecuteNonQueryAsync(ct);
        tx.Commit();
    }

    private static void ValidateAccount(string name, string region, string? uid)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80) throw new ArgumentException("账号名称须为 1–80 个字符。");
        if (region.Length > 80 || uid?.Length > 40) throw new ArgumentException("区服或 UID 太长。");
    }
}
