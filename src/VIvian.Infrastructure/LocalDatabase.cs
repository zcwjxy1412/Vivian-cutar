using Microsoft.Data.Sqlite;
using VIvian.Core;

namespace VIvian.Infrastructure;

// Connections are short-lived. SQLite transactions provide cross-process protection.
public sealed class LocalDatabase : ISettingsStore, IDiagnosticStore
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
        if (version > 1) throw new InvalidOperationException("数据库由更新版本创建，当前版本不会覆盖它。");
        if (version == 0)
        {
            command.CommandText = """
                CREATE TABLE engine_settings(game INTEGER PRIMARY KEY CHECK(game BETWEEN 0 AND 2),
                    install_path TEXT NOT NULL, revision INTEGER NOT NULL CHECK(revision>=0));
                INSERT INTO engine_settings VALUES(0,'',0),(1,'',0),(2,'',0);
                CREATE TABLE diagnostics(id TEXT PRIMARY KEY, started_at TEXT NOT NULL, summary TEXT NOT NULL);
                CREATE INDEX ix_diagnostics_time ON diagnostics(started_at DESC);
                PRAGMA user_version=1;
                """;
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
