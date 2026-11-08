using Microsoft.Data.Sqlite;
using VIvian.Core;

namespace VIvian.Infrastructure;

// Connections are short-lived. SQLite transactions provide cross-process protection.
public sealed partial class LocalDatabase : ISettingsStore, IDiagnosticStore
{
    private readonly string _connectionString;
    private readonly string _databasePath;
    public LocalDatabase(string path)
    {
        _databasePath = Path.GetFullPath(path);
        _connectionString = new SqliteConnectionStringBuilder
        { DataSource = _databasePath, Mode = SqliteOpenMode.ReadWriteCreate,
          DefaultTimeout = 5, Pooling = false }.ToString();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;";
            await command.ExecuteNonQueryAsync(ct);
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    public const int CurrentSchemaVersion = 6;

    // M1 schema, kept verbatim so that fresh databases and upgraded ones end up identical.
    private const string SchemaV1 = """
        CREATE TABLE engine_settings(game INTEGER PRIMARY KEY CHECK(game BETWEEN 0 AND 2),
            install_path TEXT NOT NULL, revision INTEGER NOT NULL CHECK(revision>=0));
        INSERT INTO engine_settings VALUES(0,'',0),(1,'',0),(2,'',0);
        CREATE TABLE diagnostics(id TEXT PRIMARY KEY, started_at TEXT NOT NULL, summary TEXT NOT NULL);
        CREATE INDEX ix_diagnostics_time ON diagnostics(started_at DESC);
        PRAGMA user_version=1;
        """;

    // M2: run records, their full transition history, and run events.
    private const string SchemaV2 = """
        CREATE TABLE runs(
            run_id TEXT PRIMARY KEY, queue_id TEXT NOT NULL,
            game INTEGER NOT NULL CHECK(game BETWEEN 0 AND 2), rule_version TEXT NOT NULL,
            is_simulation INTEGER NOT NULL CHECK(is_simulation IN (0,1)),
            phase TEXT NOT NULL, outcome TEXT NULL, cleanup TEXT NOT NULL, reason_code TEXT NULL,
            created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
            acknowledged_at TEXT NULL, acknowledgement_note TEXT NULL);
        CREATE INDEX ix_runs_created ON runs(created_at DESC);
        CREATE INDEX ix_runs_open ON runs(phase) WHERE phase NOT IN ('Finished','Blocked');
        CREATE TABLE run_transitions(
            id INTEGER PRIMARY KEY AUTOINCREMENT, run_id TEXT NOT NULL REFERENCES runs(run_id),
            phase TEXT NOT NULL, outcome TEXT NULL, cleanup TEXT NOT NULL, reason_code TEXT NULL,
            at TEXT NOT NULL, source TEXT NOT NULL);
        CREATE INDEX ix_run_transitions_run ON run_transitions(run_id, id);
        CREATE TABLE run_events(
            id INTEGER PRIMARY KEY AUTOINCREMENT, run_id TEXT NOT NULL REFERENCES runs(run_id),
            at TEXT NOT NULL, kind TEXT NOT NULL, message TEXT NOT NULL);
        CREATE INDEX ix_run_events_run ON run_events(run_id, id);
        PRAGMA user_version=2;
        """;

    // M3 pre-desktop: the user's task plans and application preferences.
    private const string SchemaV3 = """
        CREATE TABLE task_plans(game INTEGER PRIMARY KEY CHECK(game BETWEEN 0 AND 2),
            plan_json TEXT NOT NULL, rule_version TEXT NOT NULL, revision INTEGER NOT NULL CHECK(revision>=1));
        CREATE TABLE app_preferences(id INTEGER PRIMARY KEY CHECK(id=1),
            log_retention_days INTEGER NOT NULL CHECK(log_retention_days BETWEEN 3 AND 90),
            include_run_history INTEGER NOT NULL CHECK(include_run_history IN (0,1)),
            release_archive_path TEXT NOT NULL, revision INTEGER NOT NULL CHECK(revision>=0));
        INSERT INTO app_preferences VALUES(1,14,1,'',0);
        PRAGMA user_version=3;
        """;

    private const string SchemaV4 = """
        CREATE TABLE game_accounts(
            account_id TEXT PRIMARY KEY, game INTEGER NOT NULL CHECK(game BETWEEN 0 AND 2),
            display_name TEXT NOT NULL, region TEXT NOT NULL DEFAULT '', uid TEXT NULL,
            enabled INTEGER NOT NULL DEFAULT 1 CHECK(enabled IN (0,1)),
            auto_run INTEGER NOT NULL DEFAULT 0 CHECK(auto_run IN (0,1)),
            sort_order INTEGER NOT NULL DEFAULT 0,
            login_state TEXT NOT NULL DEFAULT 'unverified', verification_state TEXT NOT NULL DEFAULT 'unverified',
            last_run_at TEXT NULL, last_result TEXT NULL, revision INTEGER NOT NULL DEFAULT 1);
        CREATE INDEX ix_game_accounts_order ON game_accounts(game,sort_order,account_id);
        CREATE TABLE account_settings(account_id TEXT PRIMARY KEY REFERENCES game_accounts(account_id) ON DELETE CASCADE,
            settings_json TEXT NOT NULL DEFAULT '{}', revision INTEGER NOT NULL DEFAULT 1);
        CREATE TABLE account_task_plans(account_id TEXT PRIMARY KEY REFERENCES game_accounts(account_id) ON DELETE CASCADE,
            plan_json TEXT NOT NULL DEFAULT '[]', revision INTEGER NOT NULL DEFAULT 1);
        CREATE TABLE account_execution_history(id TEXT PRIMARY KEY, batch_id TEXT NOT NULL,
            account_id TEXT NULL REFERENCES game_accounts(account_id) ON DELETE SET NULL,
            run_id TEXT NULL, at TEXT NOT NULL, result TEXT NOT NULL, detail TEXT NOT NULL);
        CREATE TABLE account_switch_history(id TEXT PRIMARY KEY, from_account_id TEXT NULL, to_account_id TEXT NOT NULL,
            at TEXT NOT NULL, state TEXT NOT NULL, detail TEXT NOT NULL);
        CREATE TABLE batch_execution_records(id TEXT PRIMARY KEY, game INTEGER NOT NULL, started_at TEXT NOT NULL,
            updated_at TEXT NOT NULL, state TEXT NOT NULL, current_account_id TEXT NULL, detail TEXT NOT NULL);
        INSERT INTO game_accounts(account_id,game,display_name,sort_order)
            VALUES('legacy-genshin',0,'原有账号',0),('legacy-star-rail',1,'原有账号',0),('legacy-zenless',2,'原有账号',0);
        INSERT INTO account_settings(account_id) SELECT account_id FROM game_accounts;
        INSERT INTO account_task_plans(account_id,plan_json)
            SELECT 'legacy-genshin',COALESCE((SELECT plan_json FROM task_plans WHERE game=0),'[]') UNION ALL
            SELECT 'legacy-star-rail',COALESCE((SELECT plan_json FROM task_plans WHERE game=1),'[]') UNION ALL
            SELECT 'legacy-zenless',COALESCE((SELECT plan_json FROM task_plans WHERE game=2),'[]');
        PRAGMA user_version=4;
        """;

    private const string SchemaV5 = """
        ALTER TABLE game_accounts ADD COLUMN install_path TEXT NOT NULL DEFAULT '';
        PRAGMA user_version=5;
        """;

    private const string SchemaV6 = """
        CREATE TABLE account_login_profiles(
            account_id TEXT PRIMARY KEY REFERENCES game_accounts(account_id) ON DELETE CASCADE,
            masked_phone TEXT NOT NULL, normalized_phone TEXT NOT NULL,
            row_order INTEGER NOT NULL DEFAULT 0 CHECK(row_order>=0),
            confidence REAL NOT NULL DEFAULT 0,
            observed_uid TEXT NULL, last_scan_at TEXT NOT NULL, detail TEXT NOT NULL DEFAULT '');
        CREATE INDEX ix_account_login_phone ON account_login_profiles(normalized_phone);
        CREATE TABLE account_scan_sessions(
            id TEXT PRIMARY KEY, game INTEGER NOT NULL CHECK(game BETWEEN 0 AND 2),
            source TEXT NOT NULL, window_width INTEGER NOT NULL, window_height INTEGER NOT NULL,
            account_count INTEGER NOT NULL, detail TEXT NOT NULL, created_at TEXT NOT NULL);
        ALTER TABLE batch_execution_records ADD COLUMN mode TEXT NOT NULL DEFAULT 'unified';
        PRAGMA user_version=6;
        """;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            await cmd.ExecuteScalarAsync(ct);
        }
        // SQLite's online backup includes committed WAL pages and is recoverable even if this process crashes
        // during the migration. Never replace an earlier backup with a possibly damaged later copy.
        using (var versionCommand = c.CreateCommand())
        {
            versionCommand.CommandText = "PRAGMA user_version;";
            var oldVersion = Convert.ToInt32(await versionCommand.ExecuteScalarAsync(ct));
            var backupPath = _databasePath + (oldVersion == 4 ? ".pre-v5.bak"
                : oldVersion == 5 ? ".pre-v6.bak" : ".pre-m5.1.bak");
            if (oldVersion is > 0 and < CurrentSchemaVersion && !File.Exists(backupPath))
            {
                await using var backup = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = backupPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
                await backup.OpenAsync(ct);
                c.BackupDatabase(backup);
                using var check = backup.CreateCommand();
                check.CommandText = "PRAGMA integrity_check;";
                if ((string?)await check.ExecuteScalarAsync(ct) != "ok")
                    throw new IOException("数据库迁移备份未通过完整性检查，已取消迁移。");
            }
        }
        using var tx = c.BeginTransaction();
        using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt64(await command.ExecuteScalarAsync(ct));
        if (version > CurrentSchemaVersion) throw DatabaseVersionTooNew.Create(checked((int)version), CurrentSchemaVersion);
        // Each step upgrades exactly one version inside the same transaction; any failure rolls back everything.
        if (version == 0)
        {
            command.CommandText = SchemaV1;
            await command.ExecuteNonQueryAsync(ct);
        }
        if (version <= 1)
        {
            command.CommandText = SchemaV2;
            await command.ExecuteNonQueryAsync(ct);
        }
        if (version <= 2)
        {
            command.CommandText = SchemaV3;
            await command.ExecuteNonQueryAsync(ct);
        }
        if (version <= 3)
        {
            command.CommandText = SchemaV4;
            await command.ExecuteNonQueryAsync(ct);
        }
        if (version <= 4)
        {
            command.CommandText = SchemaV5;
            await command.ExecuteNonQueryAsync(ct);
        }
        if (version <= 5)
        {
            command.CommandText = SchemaV6;
            await command.ExecuteNonQueryAsync(ct);
        }
        tx.Commit();
    }

    public async Task<IReadOnlyList<EngineSetting>> LoadSettingsAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT game,install_path,revision FROM engine_settings ORDER BY game;";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var result = new List<EngineSetting>();
        while (await r.ReadAsync(ct)) result.Add(new((GameId)r.GetInt32(0), r.GetString(1), r.GetInt64(2)));
        SettingsValidation.Validate(result);
        return result;
    }

    public async Task SaveSettingsAsync(IReadOnlyList<EngineSetting> settings, CancellationToken ct = default)
    {
        SettingsValidation.Validate(settings);
        ct.ThrowIfCancellationRequested();
        await using var c = await OpenAsync(ct);
        using var tx = c.BeginTransaction();
        foreach (var item in settings)
        {
            ct.ThrowIfCancellationRequested();
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE engine_settings SET install_path=$path,revision=revision+1
                WHERE game=$game AND revision=$revision;
                """;
            cmd.Parameters.AddWithValue("$path", item.InstallPath);
            cmd.Parameters.AddWithValue("$game", (int)item.Game);
            cmd.Parameters.AddWithValue("$revision", item.Revision);
            if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new SettingsConflictException();
        }
        ct.ThrowIfCancellationRequested();
        tx.Commit();
    }

    public async Task<EngineSetting> SaveSettingAsync(EngineSetting setting, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(setting);
        // Reuse the full validation rules on a single row.
        SettingsValidation.Validate([setting,
            .. Enum.GetValues<GameId>().Where(g => g != setting.Game).Select(g => new EngineSetting(g, "", 0))]);
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE engine_settings SET install_path=$path,revision=revision+1
            WHERE game=$game AND revision=$revision RETURNING revision;
            """;
        cmd.Parameters.AddWithValue("$path", setting.InstallPath);
        cmd.Parameters.AddWithValue("$game", (int)setting.Game);
        cmd.Parameters.AddWithValue("$revision", setting.Revision);
        var revision = await cmd.ExecuteScalarAsync(ct);
        if (revision is not long next) throw new SettingsConflictException();
        return setting with { Revision = next };
    }

    public async Task AppendAsync(DiagnosticRecord record, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO diagnostics(id,started_at,summary) VALUES($id,$at,$summary);";
        cmd.Parameters.AddWithValue("$id", record.Id);
        cmd.Parameters.AddWithValue("$at", record.StartedAt.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$summary", record.Summary);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<DiagnosticRecord>> ReadRecentAsync(int limit, CancellationToken ct = default)
    {
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,started_at,summary FROM diagnostics ORDER BY started_at DESC,id DESC LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", limit);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var result = new List<DiagnosticRecord>();
        while (await r.ReadAsync(ct)) result.Add(new(r.GetString(0), DateTimeOffset.Parse(r.GetString(1)), r.GetString(2)));
        return result;
    }
}

/// <summary>
/// The data directory was last used by a newer VIvian. The database is left untouched. The exception type stays
/// <see cref="InvalidOperationException"/> (an existing contract); the versions travel in <see cref="Exception.Data"/>.
/// </summary>
public static class DatabaseVersionTooNew
{
    public const string FoundKey = "VIvian.DatabaseVersion.Found";
    public const string SupportedKey = "VIvian.DatabaseVersion.Supported";

    public static InvalidOperationException Create(int found, int supported)
    {
        var ex = new InvalidOperationException($"数据库由更新版本创建（v{found}），当前版本只支持到 v{supported}，不会覆盖它。");
        ex.Data[FoundKey] = found;
        ex.Data[SupportedKey] = supported;
        return ex;
    }

    public static bool TryRead(Exception ex, out int found, out int supported)
    {
        found = ex.Data[FoundKey] is int f ? f : 0;
        supported = ex.Data[SupportedKey] is int s ? s : 0;
        return found > 0;
    }
}
