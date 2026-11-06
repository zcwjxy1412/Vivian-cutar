using Microsoft.Data.Sqlite;
using VIvian.Core;

namespace VIvian.Infrastructure;

// Connections are short-lived. SQLite transactions provide cross-process protection.
public sealed partial class LocalDatabase : ISettingsStore, IDiagnosticStore
{
    private readonly string _connectionString;
    public LocalDatabase(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder
        { DataSource = Path.GetFullPath(path), Mode = SqliteOpenMode.ReadWriteCreate,
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

    public const int CurrentSchemaVersion = 3;

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

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            await cmd.ExecuteScalarAsync(ct);
        }
        using var tx = c.BeginTransaction();
        using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt64(await command.ExecuteScalarAsync(ct));
        if (version > CurrentSchemaVersion) throw new InvalidOperationException("数据库由更新版本创建，当前版本不会覆盖它。");
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
