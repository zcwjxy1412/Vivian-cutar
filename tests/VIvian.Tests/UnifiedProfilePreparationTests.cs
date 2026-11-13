using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Engines;
using VIvian.Infrastructure.Engines.BetterGi;
using Xunit;
using Xunit.Abstractions;

namespace VIvian.Tests;

public sealed class UnifiedProfilePreparationTests(ITestOutputHelper testOutput) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vivian-unified-tests-").FullName;

    [Fact]
    public void PhysicalDatabaseCheckUsesTheSqliteHandle()
    {
        if (!OperatingSystem.IsWindows()) return;
        var actual = Path.Combine(_root, "actual.db");
        var expected = Path.Combine(_root, "expected.db");
        using var database = Open(actual);
        UnifiedProfilePreparation.RequirePhysicalDatabasePath(database, actual);
        Assert.Throws<IOException>(() => UnifiedProfilePreparation.RequirePhysicalDatabasePath(database, expected));
        Assert.False(File.Exists(expected));
    }

    [Fact]
    public void APhysicalDatabaseCannotUseAnAliasedCompanion()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_root, "native.db");
        var other = Path.Combine(_root, "other-file");
        File.WriteAllText(other, "fixture");
        using var database = Open(path);
        Assert.True(CreateHardLink(path + "-wal", other, IntPtr.Zero));
        var error = Assert.Throws<IOException>(() => UnifiedProfilePreparation.RequirePhysicalDatabasePath(database, path));
        Assert.Contains("辅助文件", error.Message);
        Assert.Equal("fixture", File.ReadAllText(other));
    }

    [Fact]
    public void PreparationDirectoryCannotBeRenamedWhileItIsPinned()
    {
        if (!OperatingSystem.IsWindows()) return;
        var staging = Directory.CreateDirectory(Path.Combine(_root, "staging")).FullName;
        var output = Path.Combine(_root, "published");
        using (var pinned = ReadOnlyFileAccess.OpenDirectoryForRead(staging, FileShare.ReadWrite))
        {
            Assert.False(pinned.IsInvalid);
            Assert.ThrowsAny<IOException>(() => Directory.Move(staging, output));
        }
        Directory.Move(staging, output);
        Assert.True(Directory.Exists(output));
    }

    [Fact]
    public void AppDataProbeDetectsRedirectedNewFiles()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var path = Path.Combine(directory, "vivian-redirection-probe-" + Guid.NewGuid().ToString("N"));
        using (var sample = File.OpenHandle(path, FileMode.CreateNew, FileAccess.Write, FileShare.Delete, FileOptions.DeleteOnClose))
        {
            var redirected = !ReadOnlyFileAccess.HandlePathMatches(sample, path);
            testOutput.WriteLine($"New AppData files redirected: {redirected}");
            if (redirected)
            {
                using var parent = ReadOnlyFileAccess.OpenDirectoryForRead(directory, FileShare.ReadWrite | FileShare.Delete);
                var error = Assert.Throws<IOException>(() => UnifiedProfilePreparation.RequirePhysicalWrites(directory));
                if (ReadOnlyFileAccess.HandlePathMatches(parent, directory))
                    Assert.Contains("新文件写入被重定向", error.Message);
            }
            else UnifiedProfilePreparation.RequirePhysicalWrites(directory);
        }
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task AppDataRedirectionCannotPublishOrUseAShadowDatabase()
    {
        if (!OperatingSystem.IsWindows()) return;
        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "vivian-redirection-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        try
        {
            var v6 = Path.Combine(_root, "v6.db");
            var v7 = Path.Combine(_root, "v7.db");
            await new LocalDatabase(v6).InitializeAsync();
            using (var old = Open(v6))
                Execute(old, "DROP TABLE account_configuration_audit; PRAGMA user_version=6;");
            await new LocalDatabase(v7).InitializeAsync();

            var output = Path.Combine(profile, "prepared");
            using var directory = ReadOnlyFileAccess.OpenDirectoryForRead(profile, FileShare.ReadWrite | FileShare.Delete);
            Assert.False(directory.IsInvalid);
            if (ReadOnlyFileAccess.HandlePathMatches(directory, profile))
            {
                testOutput.WriteLine("AppData fixture was not virtualized. Verifying normal preparation.");
                await UnifiedProfilePreparation.PrepareAsync(v6, v7, output, CancellationToken.None);
                UnifiedProfilePreparation.VerifyPrepared(output);
                return;
            }

            testOutput.WriteLine("AppData fixture was virtualized. Verifying rejection of the output and the source.");
            await Assert.ThrowsAsync<IOException>(() => UnifiedProfilePreparation.PrepareAsync(
                v6, v7, output, CancellationToken.None));
            Assert.Empty(Directory.EnumerateFileSystemEntries(profile));
            var shadow = Path.Combine(profile, "shadow.db");
            File.Copy(v6, shadow);
            var elsewhere = Path.Combine(_root, "prepared");
            await Assert.ThrowsAsync<IOException>(() => UnifiedProfilePreparation.PrepareAsync(
                shadow, v7, elsewhere, CancellationToken.None));
            Assert.False(Directory.Exists(elsewhere));
        }
        finally { TestDirectories.DeleteWithRetry(profile); }
    }

    [Fact]
    public async Task PreparesTwoAccountsFromConsistentSnapshotsAndKeepsOriginals()
    {
        var official = Path.Combine(_root, "official.db");
        var incoming = Path.Combine(_root, "incoming.db");
        var destination = Path.Combine(_root, "prepared");
        var starRail = new LocalDatabase(official);
        var genshin = new LocalDatabase(incoming);
        await starRail.InitializeAsync();
        await genshin.InitializeAsync();
        using var keepWal = Open(official);
        Execute(keepWal, "PRAGMA wal_autocheckpoint=0; DROP TABLE account_configuration_audit; PRAGMA user_version=6;");
        var starAccount = await starRail.AddAccountAsync(GameId.StarRail, "星铁正式档案", "", null);
        var starPlan = await starRail.LoadAccountTaskPlanAsync(starAccount.Id);
        starPlan = await starRail.SaveAccountTaskPlanAsync(starPlan with { DailyEnabled = false });
        Assert.True(new FileInfo(official + "-wal").Length > 0);
        var a = await AddGenshinAsync(genshin, "档案甲", "130****00");
        var b = await AddGenshinAsync(genshin, "档案乙", "131****01");

        await UnifiedProfilePreparation.PrepareAsync(official, incoming, destination, CancellationToken.None);
        UnifiedProfilePreparation.VerifyPrepared(destination);
        var prepared = new LocalDatabase(Path.Combine(destination, "vivian.db"));
        var accounts = await prepared.LoadAccountsAsync(GameId.Genshin);
        Assert.Contains(accounts, account => account.Id == a.Id);
        Assert.Contains(accounts, account => account.Id == b.Id);
        Assert.Equal(2, (await prepared.LoadLoginProfilesAsync(GameId.Genshin)).Count);
        Assert.Equal("true", (await prepared.LoadAccountSettingsAsync(a.Id)).Values["dailyRewards"]);
        Assert.Equal("true", (await prepared.LoadAccountSettingsAsync(b.Id)).Values["dailyRewards"]);
        Assert.Equal(starPlan, await prepared.LoadAccountTaskPlanAsync(starAccount.Id));
        var acceptance = Path.Combine(_root, "reward-acceptance");
        await UnifiedProfilePreparation.CopyPreparedProfileAsync(destination, acceptance, CancellationToken.None);
        var acceptanceDb = new LocalDatabase(Path.Combine(acceptance, "vivian.db"), requirePreparedProfile: true);
        var accepted = (await acceptanceDb.LoadAccountsAsync(GameId.Genshin)).Single(x => x.Id == a.Id);
        Assert.False(accepted.AutoRun);
        await acceptanceDb.SaveAccountAsync(accepted with { AutoRun = true });
        Assert.False((await prepared.LoadAccountsAsync(GameId.Genshin)).Single(x => x.Id == a.Id).AutoRun);
        await Assert.ThrowsAsync<IOException>(() => UnifiedProfilePreparation.CopyPreparedProfileAsync(
            destination, acceptance, CancellationToken.None));
        Assert.Equal(6L, Scalar(keepWal, "PRAGMA user_version;"));
        Assert.Single(await starRail.LoadAccountsAsync(GameId.Genshin));
        Assert.Throws<IOException>(() => UnifiedProfilePreparation.VerifyPrepared(Path.Combine(_root, "absent")));
        await Assert.ThrowsAsync<IOException>(() => UnifiedProfilePreparation.PrepareAsync(
            official, incoming, destination, CancellationToken.None));
    }

    [Fact]
    public async Task ConflictingSavedIdentityLeavesNoPublishedProfileAndCanBeRetried()
    {
        var official = Path.Combine(_root, "official.db");
        var incoming = Path.Combine(_root, "incoming.db");
        var destination = Path.Combine(_root, "prepared");
        var target = new LocalDatabase(official);
        var source = new LocalDatabase(incoming);
        await target.InitializeAsync();
        await source.InitializeAsync();
        var owner = await AddGenshinAsync(target, "已存在档案", "131****01");
        using (var connection = Open(official))
            Execute(connection, "DROP TABLE account_configuration_audit; PRAGMA user_version=6;");
        await AddGenshinAsync(source, "无冲突档案", "130****00");
        await AddGenshinAsync(source, "冲突档案", "131****01");

        await Assert.ThrowsAsync<InvalidDataException>(() => UnifiedProfilePreparation.PrepareAsync(
            official, incoming, destination, CancellationToken.None));
        Assert.False(Directory.Exists(destination));
        Assert.Equal(owner.Id, Assert.Single(await target.LoadLoginProfilesAsync(GameId.Genshin)).AccountId);

        var replacement = Path.Combine(_root, "replacement.db");
        var corrected = new LocalDatabase(replacement);
        await corrected.InitializeAsync();
        await AddGenshinAsync(corrected, "无冲突档案", "130****00");
        await UnifiedProfilePreparation.PrepareAsync(official, replacement, destination, CancellationToken.None);
        UnifiedProfilePreparation.VerifyPrepared(destination);
    }

    [Fact]
    public async Task FinishedRunWithPendingCleanupStillBlocksPublishing()
    {
        var official = Path.Combine(_root, "official.db");
        var incoming = Path.Combine(_root, "incoming.db");
        var destination = Path.Combine(_root, "prepared");
        await new LocalDatabase(official).InitializeAsync();
        using (var old = Open(official))
            Execute(old, "DROP TABLE account_configuration_audit; PRAGMA user_version=6;");
        await new LocalDatabase(incoming).InitializeAsync();
        using (var preview = Open(incoming))
            Execute(preview, """
                INSERT INTO runs(run_id,queue_id,game,rule_version,is_simulation,phase,outcome,cleanup,created_at,updated_at)
                VALUES('run','queue',0,'test',0,'Finished','Success','Pending','2026-11-09','2026-11-09');
                """);

        await Assert.ThrowsAsync<InvalidOperationException>(() => UnifiedProfilePreparation.PrepareAsync(
            official, incoming, destination, CancellationToken.None));
        Assert.False(Directory.Exists(destination));
        Assert.Empty(Directory.EnumerateDirectories(_root));
    }

    [Fact]
    public async Task ASettingsConflictRollsBackEarlierAccountInserts()
    {
        var official = Path.Combine(_root, "official.db");
        var incoming = Path.Combine(_root, "incoming.db");
        var destination = Path.Combine(_root, "prepared");
        var target = new LocalDatabase(official);
        await target.InitializeAsync();
        var existing = await AddGenshinAsync(target, "正式原神档案", "130****00");
        using (var old = Open(official))
        using (var preview = Open(incoming))
        {
            old.BackupDatabase(preview);
            Execute(old, "DROP TABLE account_configuration_audit; PRAGMA user_version=6;");
            Execute(preview, $"UPDATE account_settings SET settings_json='{{}}', revision=revision+1 WHERE account_id='{existing.Id:D}';");
        }
        await AddGenshinAsync(new LocalDatabase(incoming), "新档案", "131****01");

        var conflict = await Assert.ThrowsAsync<InvalidDataException>(() => UnifiedProfilePreparation.PrepareAsync(
            official, incoming, destination, CancellationToken.None));
        Assert.Contains("account_settings", conflict.Message);
        Assert.False(Directory.Exists(destination));
        Assert.Empty(Directory.EnumerateDirectories(_root));
        Assert.Equal("true", (await target.LoadAccountSettingsAsync(existing.Id)).Values["dailyRewards"]);
        Assert.Equal(existing.Id, Assert.Single(await target.LoadLoginProfilesAsync(GameId.Genshin)).AccountId);
    }

    [Fact]
    public async Task SwappingThePreparedDirectoryAfterPreflightDoesNotMigrateTheReplacement()
    {
        var official = Path.Combine(_root, "official.db");
        var incoming = Path.Combine(_root, "incoming.db");
        var destination = Path.Combine(_root, "prepared");
        await new LocalDatabase(official).InitializeAsync();
        using (var old = Open(official))
            Execute(old, "DROP TABLE account_configuration_audit; PRAGMA user_version=6;");
        await new LocalDatabase(incoming).InitializeAsync();
        await UnifiedProfilePreparation.PrepareAsync(official, incoming, destination, CancellationToken.None);
        UnifiedProfilePreparation.VerifyPrepared(destination);
        Directory.Move(destination, Path.Combine(_root, "verified"));
        Directory.CreateDirectory(destination);
        var replacement = Path.Combine(destination, "vivian.db");
        using (var old = Open(official))
        using (var displaced = Open(replacement))
            old.BackupDatabase(displaced);

        var strict = new LocalDatabase(replacement, requirePreparedProfile: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => strict.InitializeAsync());
        using var untouched = Open(replacement);
        Assert.Equal(6L, Scalar(untouched, "PRAGMA user_version;"));
        Assert.False(File.Exists(replacement + ".pre-v7.bak"));
    }

    private static async Task<GameAccount> AddGenshinAsync(LocalDatabase database, string name, string maskedPhone)
    {
        var account = await database.AddAccountAsync(GameId.Genshin, name, "", null);
        await database.SaveLoginProfileAsync(account.Id, maskedPhone, maskedPhone, 0, .9, null, "测试");
        var provider = new GenshinAccountConfigurationProvider(database);
        var target = new AccountConfigurationTarget(GameId.Genshin, account.Id);
        var view = await provider.ReadAsync(target, CancellationToken.None);
        var proposal = await provider.ProposeAsync(target, view.Revision,
            new Dictionary<string, string> { ["dailyRewards"] = "true" }, CancellationToken.None);
        await provider.ApplyAsync(proposal, () => true, CancellationToken.None);
        return account;
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string path, string target, IntPtr securityAttributes);

    public void Dispose() => TestDirectories.DeleteWithRetry(_root);
}
