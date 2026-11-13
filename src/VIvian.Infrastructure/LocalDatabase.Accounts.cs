using Microsoft.Data.Sqlite;
using System.Text.Json;
using VIvian.Core;
using VIvian.Core.Engines;

namespace VIvian.Infrastructure;

public sealed record GameAccount(Guid Id, GameId Game, string Name, string Region, string? Uid, string InstallPath,
    bool Enabled, bool AutoRun, int Order, string LoginState, string VerificationState,
    DateTimeOffset? LastRunAt, string? LastResult, long Revision)
{
    public bool IsLegacyPlaceholder => Id == LocalDatabase.LegacyId(Game);
    public bool HasGenshinLoginBinding { get; init; }
}
public sealed record AccountSettingsSnapshot(Guid AccountId, IReadOnlyDictionary<string, string> Values, long Revision);
public sealed record AccountTaskPlan(Guid AccountId, bool DailyEnabled, long Revision);
public sealed record AccountBatch(Guid Id, GameId Game, DateTimeOffset StartedAt, DateTimeOffset UpdatedAt,
    string State, Guid? CurrentAccountId, string Detail, string Mode = "unified");
public sealed record AccountExecutionRecord(Guid Id, Guid BatchId, string AccountName, Guid? RunId,
    DateTimeOffset At, string Result, string Detail, Guid? AccountId = null);

public sealed partial class LocalDatabase
{
    public async Task<IReadOnlyList<GameAccount>> LoadAccountsAsync(GameId? game = null, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT a.account_id,a.game,a.display_name,a.region,a.uid,a.install_path,a.enabled,a.auto_run,a.sort_order,
                a.login_state,a.verification_state,a.last_run_at,a.last_result,a.revision,
                CASE WHEN p.detail=$importSource THEN p.masked_phone END, p.normalized_phone
            FROM game_accounts a LEFT JOIN account_login_profiles p ON a.game=$genshin
                AND a.account_id=p.account_id
            WHERE ($game IS NULL OR a.game=$game) ORDER BY a.game,a.sort_order,a.account_id;
            """;
        cmd.Parameters.AddWithValue("$game", game is null ? DBNull.Value : (object)(int)game);
        cmd.Parameters.AddWithValue("$genshin", (int)GameId.Genshin);
        cmd.Parameters.AddWithValue("$importSource", GenshinPhotoImportSource);
        var result = new List<GameAccount>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var idText = reader.GetString(0);
            // Stable IDs from the legacy migration are not GUIDs; map them deterministically without using names.
            var id = Guid.TryParse(idText, out var parsed) ? parsed : LegacyId((GameId)reader.GetInt32(1));
            var name = DisplayImportedGenshinName(reader.GetString(2), reader.IsDBNull(14) ? null : reader.GetString(14));
            result.Add(new(id, (GameId)reader.GetInt32(1), name, reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetInt32(6) != 0, reader.GetInt32(7) != 0,
                reader.GetInt32(8), reader.GetString(9), reader.GetString(10),
                reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11)),
                reader.IsDBNull(12) ? null : reader.GetString(12), reader.GetInt64(13))
            {
                HasGenshinLoginBinding = !reader.IsDBNull(15) && !string.IsNullOrWhiteSpace(reader.GetString(15)),
            });
        }
        return result;
    }

    internal static Guid LegacyId(GameId game) => game switch
    {
        GameId.Genshin => Guid.Parse("00000000-0000-4000-8000-000000000001"),
        GameId.StarRail => Guid.Parse("00000000-0000-4000-8000-000000000002"),
        _ => Guid.Parse("00000000-0000-4000-8000-000000000003"),
    };

    private static string DatabaseId(Guid id) => id == LegacyId(GameId.Genshin) ? "legacy-genshin"
        : id == LegacyId(GameId.StarRail) ? "legacy-star-rail"
        : id == LegacyId(GameId.ZenlessZoneZero) ? "legacy-zenless" : id.ToString("D");

    public async Task<GameAccount> AddAccountAsync(GameId game, string name, string region, string? uid,
        string installPath = "", CancellationToken ct = default)
    {
        ValidateAccount(name, region, uid);
        ValidateInstallPath(installPath);
        var id = Guid.NewGuid();
        await using var c = await OpenAsync(ct);
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO game_accounts(account_id,game,display_name,region,uid,install_path,sort_order) VALUES($id,$game,$name,$region,$uid,$path,(SELECT COALESCE(MAX(sort_order),-1)+1 FROM game_accounts WHERE game=$game)); INSERT INTO account_settings(account_id) VALUES($id); INSERT INTO account_task_plans(account_id,plan_json) VALUES($id,$plan);";
        cmd.Parameters.AddWithValue("$id", DatabaseId(id));
        cmd.Parameters.AddWithValue("$game", (int)game);
        cmd.Parameters.AddWithValue("$name", name.Trim());
        cmd.Parameters.AddWithValue("$region", region.Trim());
        cmd.Parameters.AddWithValue("$uid", uid is null ? DBNull.Value : (object)uid.Trim());
        cmd.Parameters.AddWithValue("$path", installPath.Trim());
        cmd.Parameters.AddWithValue("$plan", game == GameId.StarRail ? "[{\"Id\":\"routine\",\"Enabled\":true}]" : "[]");
        await cmd.ExecuteNonQueryAsync(ct);
        tx.Commit();
        return (await LoadAccountsAsync(game, ct)).Single(a => a.Id == id);
    }

    public async Task<GameAccount> SaveAccountAsync(GameAccount account, CancellationToken ct = default)
    {
        ValidateAccount(account.Name, account.Region, account.Uid);
        ValidateInstallPath(account.InstallPath);
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE game_accounts SET display_name=$name,region=$region,uid=$uid,install_path=$path,enabled=$enabled,auto_run=$auto,sort_order=$order,revision=revision+1 WHERE account_id=$id AND game=$game AND revision=$revision;";
        cmd.Parameters.AddWithValue("$id", DatabaseId(account.Id));
        cmd.Parameters.AddWithValue("$game", (int)account.Game);
        cmd.Parameters.AddWithValue("$name", account.Name.Trim());
        cmd.Parameters.AddWithValue("$region", account.Region.Trim());
        cmd.Parameters.AddWithValue("$uid", account.Uid is null ? DBNull.Value : (object)account.Uid.Trim());
        cmd.Parameters.AddWithValue("$path", account.InstallPath.Trim());
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
        ValidateInstallPath(account.InstallPath);
        await using var c = await OpenAsync(ct);
        using var tx = c.BeginTransaction();
        using (var profile = c.CreateCommand())
        {
            profile.Transaction = tx;
            profile.CommandText = "UPDATE game_accounts SET display_name=$name,region=$region,uid=$uid,install_path=$path,enabled=$enabled,auto_run=$auto,revision=revision+1 WHERE account_id=$id AND game=1 AND revision=$revision;";
            profile.Parameters.AddWithValue("$name", account.Name.Trim());
            profile.Parameters.AddWithValue("$region", account.Region.Trim());
            profile.Parameters.AddWithValue("$uid", account.Uid is null ? DBNull.Value : (object)account.Uid.Trim());
            profile.Parameters.AddWithValue("$path", account.InstallPath.Trim());
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
        ValidateStarRailAccountSettings(settings.Values);
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE account_settings SET settings_json=$json,revision=revision+1 WHERE account_id=$id AND revision=$revision
            AND EXISTS (SELECT 1 FROM game_accounts a WHERE a.account_id=$id AND a.game=$game);
            """;
        cmd.Parameters.AddWithValue("$game", (int)GameId.StarRail);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(settings.Values));
        cmd.Parameters.AddWithValue("$id", DatabaseId(settings.AccountId));
        cmd.Parameters.AddWithValue("$revision", settings.Revision);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new SettingsConflictException();
        return settings with { Revision = settings.Revision + 1 };
    }

    private static void ValidateStarRailAccountSettings(IReadOnlyDictionary<string, string> values)
    {
        var allowed = EngineCatalog.March7th.Parameters.Where(p => p.Access == ParameterAccess.VerifiedReadWrite)
            .ToDictionary(p => p.Path);
        foreach (var (key, value) in values)
            if (!allowed.TryGetValue(key, out var parameter) || !ParameterRules.IsValid(parameter, value))
                throw new ArgumentException("账号设置包含不支持的字段或值：" + key);
    }

    public async Task<AccountBatch> BeginBatchAsync(GameId game, string mode = "unified", CancellationToken ct = default,
        Guid? actionId = null)
    {
        if (mode is not ("unified" or "independent")) throw new ArgumentOutOfRangeException(nameof(mode));
        var now = DateTimeOffset.UtcNow;
        if (actionId == Guid.Empty) throw new ArgumentException("运行计划标识无效。", nameof(actionId));
        var batch = new AccountBatch(actionId ?? Guid.NewGuid(), game, now, now, "running", null, "等待账号登录", mode);
        await WriteBatchAsync(batch, ct, createOnly: actionId.HasValue);
        return batch;
    }

    public Task SaveBatchAsync(AccountBatch batch, CancellationToken ct = default) => WriteBatchAsync(batch, ct, createOnly: false);

    private async Task WriteBatchAsync(AccountBatch batch, CancellationToken ct, bool createOnly)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        const string insert = "INSERT INTO batch_execution_records(id,game,started_at,updated_at,state,current_account_id,detail,mode) VALUES($id,$game,$start,$update,$state,$account,$detail,$mode)";
        cmd.CommandText = insert + (createOnly ? " ON CONFLICT(id) DO NOTHING;" :
            " ON CONFLICT(id) DO UPDATE SET updated_at=excluded.updated_at,state=excluded.state,current_account_id=excluded.current_account_id,detail=excluded.detail,mode=excluded.mode;");
        cmd.Parameters.AddWithValue("$id", batch.Id.ToString("D"));
        cmd.Parameters.AddWithValue("$game", (int)batch.Game);
        cmd.Parameters.AddWithValue("$start", batch.StartedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$update", batch.UpdatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$state", batch.State);
        cmd.Parameters.AddWithValue("$account", batch.CurrentAccountId is null ? DBNull.Value : (object)DatabaseId(batch.CurrentAccountId.Value));
        cmd.Parameters.AddWithValue("$detail", batch.Detail);
        cmd.Parameters.AddWithValue("$mode", batch.Mode);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1)
            throw new InvalidOperationException("此运行计划已经提交，请查看批次记录。");
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

    /// <summary>Exact request lookup; absence is unknown, never a reason to substitute a recent batch.</summary>
    public async Task<AccountBatch?> LoadBatchAsync(Guid batchId, CancellationToken ct = default)
    {
        if (batchId == Guid.Empty) throw new ArgumentException("运行计划标识无效。", nameof(batchId));
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,game,started_at,updated_at,state,current_account_id,detail,mode FROM batch_execution_records WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", batchId.ToString("D"));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadBatch(reader) : null;
    }

    public async Task<IReadOnlyList<AccountBatch>> LoadRecentBatchesAsync(int limit = 20, CancellationToken ct = default, GameId? game = null)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id,game,started_at,updated_at,state,current_account_id,detail,mode FROM batch_execution_records
            WHERE ($game IS NULL OR game=$game) ORDER BY started_at DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$game", game is null ? DBNull.Value : (object)(int)game);
        cmd.Parameters.AddWithValue("$limit", limit);
        var result = new List<AccountBatch>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(ReadBatch(reader));
        return result;
    }

    private static AccountBatch ReadBatch(SqliteDataReader reader)
    {
        var accountText = reader.IsDBNull(5) ? null : reader.GetString(5);
        var accountId = accountText is null ? (Guid?)null : Guid.TryParse(accountText, out var parsed)
            ? parsed : accountText switch { "legacy-genshin" => LegacyId(GameId.Genshin),
                "legacy-star-rail" => LegacyId(GameId.StarRail), "legacy-zenless" => LegacyId(GameId.ZenlessZoneZero), _ => null };
        return new(Guid.Parse(reader.GetString(0)), (GameId)reader.GetInt32(1),
            DateTimeOffset.Parse(reader.GetString(2)), DateTimeOffset.Parse(reader.GetString(3)),
            reader.GetString(4), accountId, reader.GetString(6), reader.GetString(7));
    }

    public async Task<IReadOnlyList<AccountExecutionRecord>> LoadBatchResultsAsync(Guid batchId, CancellationToken ct = default,
        int limit = -1)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT h.id,COALESCE(a.display_name,'已删除档案'),h.run_id,h.at,h.result,h.detail,h.account_id,b.game
            FROM account_execution_history h JOIN batch_execution_records b ON b.id=h.batch_id
            LEFT JOIN game_accounts a ON a.account_id=h.account_id
            WHERE h.batch_id=$batch ORDER BY h.at,h.id LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$batch", batchId.ToString("D"));
        cmd.Parameters.AddWithValue("$limit", limit);
        var result = new List<AccountExecutionRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new(Guid.Parse(reader.GetString(0)), batchId, reader.GetString(1),
                reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)), DateTimeOffset.Parse(reader.GetString(3)),
                reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null :
                    Guid.TryParse(reader.GetString(6), out var id) ? id : LegacyId((GameId)reader.GetInt32(7))));
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

    private static void ValidateInstallPath(string path)
    {
        if (path.Length > 512 || path.Contains('\0') || (path.Length > 0 && !Path.IsPathFullyQualified(path.Trim())))
            throw new ArgumentException("账号专属安装目录须为有效的绝对路径。");
    }

    public sealed record AccountLoginProfile(Guid AccountId, string MaskedPhone, string NormalizedPhone,
        int RowOrder, double Confidence, string? ObservedUid, DateTimeOffset LastScanAt, string Detail);
    public sealed record AccountScanSession(Guid Id, GameId Game, string Source, int WindowWidth,
        int WindowHeight, int AccountCount, string Detail, DateTimeOffset CreatedAt);

    public async Task<IReadOnlyList<AccountLoginProfile>> LoadLoginProfilesAsync(GameId? game = null,
        CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT p.account_id,p.masked_phone,p.normalized_phone,p.row_order,p.confidence,p.observed_uid,p.last_scan_at,p.detail FROM account_login_profiles p JOIN game_accounts a ON a.account_id=p.account_id WHERE ($game IS NULL OR a.game=$game) ORDER BY a.game,p.row_order,p.account_id;";
        cmd.Parameters.AddWithValue("$game", game is null ? DBNull.Value : (object)(int)game);
        var result = new List<AccountLoginProfile>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var idText = reader.GetString(0);
            var id = Guid.TryParse(idText, out var parsed) ? parsed
                : idText switch { "legacy-genshin" => LegacyId(GameId.Genshin),
                    "legacy-star-rail" => LegacyId(GameId.StarRail),
                    "legacy-zenless" => LegacyId(GameId.ZenlessZoneZero), _ => Guid.Empty };
            if (id == Guid.Empty) continue;
            result.Add(new(id, reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetDouble(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), DateTimeOffset.Parse(reader.GetString(6)),
                reader.GetString(7)));
        }
        return result;
    }

    public async Task SaveLoginProfileAsync(Guid accountId, string maskedPhone, string normalizedPhone,
        int rowOrder, double confidence, string? observedUid, string detail, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(maskedPhone) || string.IsNullOrWhiteSpace(normalizedPhone))
            throw new ArgumentException("脱敏手机号不能为空。");
        if (rowOrder < 0) throw new ArgumentOutOfRangeException(nameof(rowOrder));
        await using var c = await OpenAsync(ct);
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO account_login_profiles(account_id,masked_phone,normalized_phone,row_order,confidence,observed_uid,last_scan_at,detail) VALUES($account,$masked,$normalized,$row,$confidence,$uid,$at,$detail) ON CONFLICT(account_id) DO UPDATE SET masked_phone=excluded.masked_phone,normalized_phone=excluded.normalized_phone,row_order=excluded.row_order,confidence=excluded.confidence,observed_uid=COALESCE(excluded.observed_uid,account_login_profiles.observed_uid),last_scan_at=excluded.last_scan_at,detail=excluded.detail; UPDATE game_accounts SET login_state='recognized',verification_state=CASE WHEN $uid IS NULL THEN verification_state ELSE 'uid_observed' END,revision=revision+1 WHERE account_id=$account;";
        cmd.Parameters.AddWithValue("$account", DatabaseId(accountId));
        cmd.Parameters.AddWithValue("$masked", maskedPhone.Trim());
        cmd.Parameters.AddWithValue("$normalized", normalizedPhone.Trim());
        cmd.Parameters.AddWithValue("$row", rowOrder);
        cmd.Parameters.AddWithValue("$confidence", confidence);
        cmd.Parameters.AddWithValue("$uid", observedUid is null ? DBNull.Value : (object)observedUid);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$detail", detail);
        await cmd.ExecuteNonQueryAsync(ct);
        tx.Commit();
    }

    public async Task SaveObservedUidAsync(Guid accountId, string uid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(uid) || uid.Length > 40) throw new ArgumentException("UID 格式无效。");
        await using var c = await OpenAsync(ct);
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE account_login_profiles SET observed_uid=$uid,last_scan_at=$at WHERE account_id=$account; UPDATE game_accounts SET verification_state='uid_observed',revision=revision+1 WHERE account_id=$account;";
        cmd.Parameters.AddWithValue("$uid", uid.Trim());
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$account", DatabaseId(accountId));
        await cmd.ExecuteNonQueryAsync(ct);
        tx.Commit();
    }

    public async Task RecordScanSessionAsync(GameId game, string source, int width, int height, int count,
        string detail, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO account_scan_sessions(id,game,source,window_width,window_height,account_count,detail,created_at) VALUES($id,$game,$source,$w,$h,$count,$detail,$at);";
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        cmd.Parameters.AddWithValue("$game", (int)game);
        cmd.Parameters.AddWithValue("$source", source);
        cmd.Parameters.AddWithValue("$w", width);
        cmd.Parameters.AddWithValue("$h", height);
        cmd.Parameters.AddWithValue("$count", count);
        cmd.Parameters.AddWithValue("$detail", detail);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<AccountScanSession>> LoadScanSessionsAsync(int limit = 20,
        CancellationToken ct = default)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,game,source,window_width,window_height,account_count,detail,created_at FROM account_scan_sessions ORDER BY created_at DESC LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", limit);
        var result = new List<AccountScanSession>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new(Guid.Parse(reader.GetString(0)), (GameId)reader.GetInt32(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetString(6),
                DateTimeOffset.Parse(reader.GetString(7))));
        return result;
    }

}
