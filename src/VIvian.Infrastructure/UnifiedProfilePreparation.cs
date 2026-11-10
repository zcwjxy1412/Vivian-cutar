using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using VIvian.Infrastructure.Engines;
using VIvian.Infrastructure.Processes;

namespace VIvian.Infrastructure;

public static class UnifiedProfilePreparation
{
    public const string InstalledProfileName = "VIvian-UserData";
    private const string DatabaseName = "vivian.db";
    private const int PreparedApplicationId = 0x5649564E; // VIVN
    private const int Win32GetHandle = 29; // SQLITE_FCNTL_WIN32_GET_HANDLE

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

    public static async Task CreateGenshinRewardAcceptanceSnapshotAsync(string destination, CancellationToken ct)
    {
        var installed = InstalledDirectory;
        var parent = Path.Combine(Path.GetDirectoryName(installed)!, "VIvian-M6-GenshinAcceptance");
        var output = Path.GetFullPath(destination);
        if (!string.Equals(Path.GetDirectoryName(output), parent, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(output), "N", out var attempt) || attempt == Guid.Empty)
            throw new ArgumentException("原神领奖验收需要独立的单次数据目录。", nameof(destination));
        if (ReadOnlyFileAccess.FindReparsePoint(parent) is not null)
            throw new IOException("原神验收目录含链接，拒绝创建快照。");
        using var instance = SingleInstanceGuard.Acquire(installed);
        if (!instance.IsOwner)
            throw new InvalidOperationException("请先关闭正式合流程序，再准备原神验收快照。");
        using var desktop = DesktopOperationLease.Acquire(installed);
        if (File.Exists(Path.Combine(installed, "configuration-recovery", "temporary-config-recovery.json")))
            throw new InvalidOperationException("正式档案的临时配置待恢复，无法开始原神验收。");
        Directory.CreateDirectory(parent);
        await CopyPreparedProfileAsync(installed, output, ct).ConfigureAwait(false);
    }

    internal static Task CopyPreparedProfileAsync(string sourceDirectory, string destination, CancellationToken ct) =>
        PublishAsync(destination, (target, _) =>
        {
            CopySnapshot(Path.Combine(sourceDirectory, DatabaseName), target, LocalDatabase.CurrentSchemaVersion,
                requirePrepared: true);
            return Task.CompletedTask;
        }, ct);

    public static Task PrepareAsync(string officialDatabase, string genshinDatabase, string destination, CancellationToken ct) =>
        PublishAsync(destination, async (target, source) =>
        {
            CopySnapshot(officialDatabase, target, 6);
            ct.ThrowIfCancellationRequested();
            CopySnapshot(genshinDatabase, source, LocalDatabase.CurrentSchemaVersion);
            await new LocalDatabase(target, requirePreparedProfile: false, requirePhysicalPath: true)
                .InitializeAsync(ct).ConfigureAwait(false);
            MergeGenshin(target, source, ct);
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(source + suffix);
        }, ct);

    private static async Task PublishAsync(string destination, Func<string, string, Task> populate, CancellationToken ct)
    {
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (Directory.Exists(output) || File.Exists(output))
            throw new IOException("合流库暂存目录已存在，请为本次准备选择新目录。");
        var parent = Path.GetDirectoryName(output) ?? throw new ArgumentException("需要本地暂存目录。");
        if (!Directory.Exists(parent) || ReadOnlyFileAccess.FindReparsePoint(parent) is not null ||
            new DriveInfo(Path.GetPathRoot(parent)!).DriveType != DriveType.Fixed)
            throw new IOException("合流库暂存目录须位于本地固定磁盘上，且父目录已存在并且不含链接。");
        using var parentHandle = ReadOnlyFileAccess.OpenDirectoryForRead(parent, FileShare.ReadWrite);
        RequirePhysicalDirectory(parentHandle, parent);
        RequirePhysicalWrites(parent);

        var staging = Path.Combine(parent, ".vivian-prepare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        using var stagedHandle = ReadOnlyFileAccess.OpenDirectoryForRead(staging, FileShare.ReadWrite);
        var stagedIdentity = ReadOnlyFileAccess.Identity(stagedHandle);
        var target = Path.Combine(staging, DatabaseName);
        var source = Path.Combine(staging, "incoming.db");
        try
        {
            RequirePhysicalDirectory(stagedHandle, staging);
            if (stagedIdentity is null) throw new IOException("无法核对合流暂存目录的身份。");
            RequirePhysicalWrites(staging);
            ct.ThrowIfCancellationRequested();
            await populate(target, source).ConfigureAwait(false);
            VerifyPrepared(staging);
            ct.ThrowIfCancellationRequested();
            RequirePhysicalDirectory(stagedHandle, staging);
            stagedHandle.Dispose();
            Directory.Move(staging, output);
            using var published = ReadOnlyFileAccess.OpenDirectoryForRead(output, FileShare.ReadWrite);
            try
            {
                RequirePhysicalDirectory(published, output);
                if (ReadOnlyFileAccess.Identity(published) != stagedIdentity)
                    throw new IOException("合流结果目录与核验过的暂存目录不一致。");
                VerifyPrepared(output);
            }
            catch
            {
                if (ReadOnlyFileAccess.Identity(published) == stagedIdentity &&
                    ReadOnlyFileAccess.FindReparsePoint(output) is null && !Directory.Exists(staging))
                {
                    published.Dispose();
                    Directory.Move(output, staging);
                }
                throw;
            }
        }
        finally
        {
            stagedHandle.Dispose();
            // Only delete files of this invocation, never the requested output or either source.
            if (stagedIdentity is not null && Directory.Exists(staging) && ReadOnlyFileAccess.FindReparsePoint(staging) is null)
            {
                using var remaining = ReadOnlyFileAccess.OpenDirectoryForRead(staging, FileShare.ReadWrite);
                if (ReadOnlyFileAccess.Identity(remaining) == stagedIdentity)
                {
                    foreach (var name in new[] { DatabaseName, DatabaseName + ".pre-v7.bak", "incoming.db" })
                        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                            File.Delete(Path.Combine(staging, name + suffix));
                    remaining.Dispose();
                    Directory.Delete(staging);
                }
            }
        }
    }

    public static void VerifyPrepared(string directory)
    {
        using var database = OpenExisting(Path.Combine(directory, DatabaseName), SqliteOpenMode.ReadOnly);
        RequirePreparedDatabase(database);
        VerifyIntegrity(database, transaction: null);
        RequirePhysicalWrites(directory);
    }

    internal static void RequirePreparedDatabase(SqliteConnection database)
    {
        if (Number(database, "PRAGMA user_version;") != LocalDatabase.CurrentSchemaVersion ||
            Number(database, "PRAGMA application_id;") != PreparedApplicationId)
            throw new InvalidDataException("需要已验证的 VIvian 合流库。请先完成数据准备。");
    }

    private static void CopySnapshot(string originalPath, string snapshotPath, int expectedVersion, bool requirePrepared = false)
    {
        using var original = OpenExisting(originalPath, SqliteOpenMode.ReadOnly);
        if (requirePrepared) RequirePreparedDatabase(original);
        if (Number(original, "PRAGMA user_version;") != expectedVersion)
            throw new InvalidDataException("来源数据库版本与合流方案不符。");
        using var snapshot = Connect(snapshotPath, SqliteOpenMode.ReadWriteCreate);
        original.BackupDatabase(snapshot);
        RequirePhysicalCompanions(originalPath);
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
        RequirePhysicalDatabasePath(database, source, "incoming");
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
        try
        {
            database.Open();
            RequirePhysicalDatabasePath(database, path);
            return database;
        }
        catch { database.Dispose(); throw; }
    }

    internal static void RequirePhysicalDatabasePath(SqliteConnection database, string path, string schema = "main")
    {
        if (!OperatingSystem.IsWindows()) return;
        if (database.Handle is not { } sqlite || SqliteFileControl(sqlite, schema, Win32GetHandle, out var nativeHandle) != 0)
            throw new IOException("无法核对 SQLite 已打开文件的物理路径。");

        // SQLite owns this borrowed handle and closes it with the connection.
        using var file = new SafeFileHandle(nativeHandle, ownsHandle: false);
        if (file.IsInvalid || !ReadOnlyFileAccess.HandlePathMatches(file, path) ||
            ReadOnlyFileAccess.LinkCount(file) != 1)
            throw new IOException("SQLite 数据库路径被重定向或存在硬链接，请从 Explorer 原生会话重试。");
        RequirePhysicalCompanions(path);
    }

    private static void RequirePhysicalCompanions(string path)
    {
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var companion = path + suffix;
            try
            {
                using var file = File.OpenHandle(companion, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (!ReadOnlyFileAccess.HandlePathMatches(file, companion) ||
                    (OperatingSystem.IsWindows() && ReadOnlyFileAccess.LinkCount(file) != 1))
                    throw new IOException("SQLite 辅助文件路径被重定向或存在硬链接，请从 Explorer 原生会话重试。");
            }
            catch (FileNotFoundException) { } // A checkpoint can remove a companion before it is opened.
        }
    }

    internal static void RequirePhysicalWrites(string directory)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var handle = ReadOnlyFileAccess.OpenDirectoryForRead(directory, FileShare.ReadWrite);
        RequirePhysicalDirectory(handle, directory);
        // MSIX can open an existing directory natively but redirect files subsequently created inside it.
        var path = Path.Combine(directory, ".vivian-write-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
        using var probe = File.OpenHandle(path, FileMode.CreateNew, FileAccess.Write, FileShare.Delete, FileOptions.DeleteOnClose);
        if (!ReadOnlyFileAccess.HandlePathMatches(probe, path))
            throw new IOException("合流目录的新文件写入被重定向，请从 Explorer 原生会话重试。");
    }

    private static void RequirePhysicalDirectory(SafeFileHandle directory, string path)
    {
        if (directory.IsInvalid || !ReadOnlyFileAccess.HandlePathMatches(directory, path))
            throw new IOException("合流目录的物理路径不一致，请从 Explorer 原生会话重试。");
    }

    [DllImport("e_sqlite3", EntryPoint = "sqlite3_file_control", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern int SqliteFileControl(SQLitePCL.sqlite3 database,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string schema, int operation, out IntPtr argument);

    private static bool Within(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
