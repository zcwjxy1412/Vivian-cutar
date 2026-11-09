using Microsoft.Data.Sqlite;
using VIvian.Infrastructure.Engines;
using VIvian.Infrastructure.Processes;

namespace VIvian.Infrastructure;

public static class UnifiedProfilePreparation
{
    public const string InstalledProfileName = "VIvian-UserData";
    private const string DatabaseName = "vivian.db";
    private const int PreparedApplicationId = 0x5649564E; // VIVN

    public static string InstalledDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), InstalledProfileName);

    public static async Task PrepareFromExistingProfilesAsync(string destination, CancellationToken ct)
    {
        var local = Path.GetDirectoryName(InstalledDirectory)!;
        var official = Path.Combine(local, "VIvian-M5-DailyTest");
        var isolated = Path.Combine(local, "VIvian-M6-StarRailTest");
        var output = Path.GetFullPath(destination);
        if (Within(output, official) || Within(output, isolated) || Within(output, InstalledDirectory))
            throw new ArgumentException("请指定独立的暂存目录，稍后再切换正式入口。");
        using var officialInstance = SingleInstanceGuard.Acquire(official);
        using var isolatedInstance = SingleInstanceGuard.Acquire(isolated);
        if (!officialInstance.IsOwner || !isolatedInstance.IsOwner)
            throw new InvalidOperationException("请退出正式版和原 M6 隔离版，再准备合流库。");
        using var desktop = DesktopOperationLease.AcquireAt(DesktopOperationLease.LockPath, official, requireExisting: true);
        foreach (var root in new[] { official, isolated })
        {
            if (File.Exists(Path.Combine(root, "configuration-recovery", "temporary-config-recovery.json")) ||
                File.Exists(Path.Combine(root, "protocol-recovery.json")))
                throw new InvalidOperationException("原档案仍有配置待恢复，请在原入口完成恢复。");
        }
        await PrepareAsync(Path.Combine(official, DatabaseName), Path.Combine(isolated, DatabaseName), output, ct)
            .ConfigureAwait(false);
    }

    public static async Task PrepareAsync(string officialDatabase, string genshinDatabase, string destination, CancellationToken ct)
    {
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (Directory.Exists(output) || File.Exists(output))
            throw new IOException("合流库暂存目录已存在，请为本次准备选择新目录。");
        var parent = Path.GetDirectoryName(output) ?? throw new ArgumentException("需要本地暂存目录。");
        if (!Directory.Exists(parent) || ReadOnlyFileAccess.FindReparsePoint(parent) is not null ||
            new DriveInfo(Path.GetPathRoot(parent)!).DriveType != DriveType.Fixed)
            throw new IOException("合流库暂存目录须位于本地固定磁盘上，且父目录已存在并且不含链接。");

        var staging = Path.Combine(parent, ".vivian-prepare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var target = Path.Combine(staging, DatabaseName);
        var source = Path.Combine(staging, "incoming.db");
        try
        {
            CopySnapshot(officialDatabase, target, 6);
            ct.ThrowIfCancellationRequested();
            CopySnapshot(genshinDatabase, source, LocalDatabase.CurrentSchemaVersion);
            await new LocalDatabase(target).InitializeAsync(ct).ConfigureAwait(false);
            MergeGenshin(target, source, ct);
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(source + suffix);
            VerifyPrepared(staging);
            ct.ThrowIfCancellationRequested();
            Directory.Move(staging, output);
        }
        finally
        {
            // Only delete files of this invocation, never the requested output or either source.
            if (Directory.Exists(staging) && ReadOnlyFileAccess.FindReparsePoint(staging) is null)
            {
                foreach (var name in new[] { DatabaseName, DatabaseName + ".pre-v7.bak", "incoming.db" })
                    foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                        File.Delete(Path.Combine(staging, name + suffix));
                Directory.Delete(staging);
            }
        }
    }

    public static void VerifyPrepared(string directory)
    {
        using var database = OpenExisting(Path.Combine(directory, DatabaseName), SqliteOpenMode.ReadOnly);
        if (Number(database, "PRAGMA user_version;") != LocalDatabase.CurrentSchemaVersion ||
            Number(database, "PRAGMA application_id;") != PreparedApplicationId)
            throw new InvalidDataException("需要已验证的 VIvian 合流库。请先完成数据准备。");
        VerifyIntegrity(database, transaction: null);
    }

    private static void CopySnapshot(string originalPath, string snapshotPath, int expectedVersion)
    {
        using var original = OpenExisting(originalPath, SqliteOpenMode.ReadOnly);
        if (Number(original, "PRAGMA user_version;") != expectedVersion)
            throw new InvalidDataException("来源数据库版本与合流方案不符。");
        using var snapshot = Connect(snapshotPath, SqliteOpenMode.ReadWriteCreate);
        original.BackupDatabase(snapshot);
        if (Number(snapshot, "PRAGMA user_version;") != expectedVersion)
            throw new InvalidDataException("来源版本在生成快照期间变化，合流库未发布。");
        VerifyIntegrity(snapshot, transaction: null);
        if (Number(snapshot, """
            SELECT EXISTS(SELECT 1 FROM runs WHERE cleanup='Pending' OR (cleanup='Unconfirmed' AND acknowledged_at IS NULL)
                OR phase NOT IN ('Finished','Blocked'))
                OR EXISTS(SELECT 1 FROM batch_execution_records WHERE state='running');
            """) != 0)
            throw new InvalidOperationException("来源数据库中有未完成的运行或清理，请在原入口处理。");
    }

    private static void MergeGenshin(string target, string source, CancellationToken ct)
    {
        using var database = OpenExisting(target, SqliteOpenMode.ReadWrite);
        using (var attach = database.CreateCommand())
        {
            attach.CommandText = "PRAGMA foreign_keys=ON; ATTACH DATABASE $source AS incoming;";
            attach.Parameters.AddWithValue("$source", source);
            attach.ExecuteNonQuery();
        }
        using var tx = database.BeginTransaction(deferred: false);
        using var sql = database.CreateCommand();
        sql.Transaction = tx;
        sql.CommandText = """
            CREATE TEMP TABLE import_accounts AS
                SELECT account_id FROM incoming.game_accounts WHERE game=0 AND account_id<>'legacy-genshin';
            """;
        sql.ExecuteNonQuery();
        sql.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM incoming.account_login_profiles p JOIN import_accounts ids USING (account_id)
                GROUP BY p.normalized_phone HAVING count(*)>1
            ) OR EXISTS(
                SELECT 1 FROM incoming.account_login_profiles p JOIN import_accounts ids USING (account_id)
                JOIN main.account_login_profiles existing ON existing.normalized_phone=p.normalized_phone
                JOIN main.game_accounts a ON a.account_id=existing.account_id
                WHERE a.game=0 AND a.account_id<>p.account_id
            );
            """;
        if ((long)sql.ExecuteScalar()! != 0)
            throw new InvalidDataException("原神登录标识冲突，合流库未发布。");
        sql.CommandText = "SELECT account_id FROM import_accounts;";
        using (var accounts = sql.ExecuteReader())
            while (accounts.Read())
                if (!Guid.TryParse(accounts.GetString(0), out var id) || id == Guid.Empty)
                    throw new InvalidDataException("来源原神账号编号无效。");

        foreach (var (table, key, filter) in new[]
        {
            ("game_accounts", "account_id", ""), ("account_login_profiles", "account_id", ""),
            ("account_settings", "account_id", ""), ("account_task_plans", "account_id", ""),
            ("account_configuration_audit", "proposal_id", " WHERE s.game=0"),
        })
        {
            ct.ThrowIfCancellationRequested();
            var changed = $"SELECT s.* FROM incoming.{table} s JOIN import_accounts ids USING(account_id){filter} " +
                $"EXCEPT SELECT * FROM main.{table}";
            sql.CommandText = $"SELECT EXISTS(SELECT 1 FROM ({changed}) delta JOIN main.{table} old USING({key}));";
            if ((long)sql.ExecuteScalar()! != 0)
                throw new InvalidDataException($"原神资料在 {table} 有不同版本的同一记录，合流库未发布。");
            sql.CommandText = $"INSERT INTO main.{table} {changed};";
            sql.ExecuteNonQuery();
        }
        VerifyIntegrity(database, tx);
        sql.CommandText = $"PRAGMA application_id={PreparedApplicationId};";
        sql.ExecuteNonQuery();
        ct.ThrowIfCancellationRequested();
        tx.Commit();
    }

    private static void VerifyIntegrity(SqliteConnection database, SqliteTransaction? transaction)
    {
        using var sql = database.CreateCommand();
        sql.Transaction = transaction;
        sql.CommandText = "PRAGMA main.quick_check;";
        if ((string?)sql.ExecuteScalar() != "ok")
            throw new InvalidDataException("数据库完整性检查失败。");
        sql.CommandText = "PRAGMA main.foreign_key_check;";
        using var violations = sql.ExecuteReader();
        if (violations.Read()) throw new InvalidDataException("数据库引用关系不完整。");
    }

    private static long Number(SqliteConnection database, string query)
    {
        using var sql = database.CreateCommand();
        sql.CommandText = query;
        return (long)sql.ExecuteScalar()!;
    }

    private static SqliteConnection OpenExisting(string path, SqliteOpenMode mode)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path) || ReadOnlyFileAccess.FindReparsePoint(path) is not null)
            throw new IOException("缺少所需的 VIvian 数据库，或数据库路径含链接。");
        return Connect(path, mode);
    }

    private static SqliteConnection Connect(string path, SqliteOpenMode mode)
    {
        var database = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = mode, DefaultTimeout = 5, Pooling = false }.ToString());
        try { database.Open(); return database; }
        catch { database.Dispose(); throw; }
    }

    private static bool Within(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
