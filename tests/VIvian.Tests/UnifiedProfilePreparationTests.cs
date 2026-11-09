using Microsoft.Data.Sqlite;
using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Engines.BetterGi;
using Xunit;

namespace VIvian.Tests;

public sealed class UnifiedProfilePreparationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vivian-unified-tests-").FullName;

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

    public void Dispose() => TestDirectories.DeleteWithRetry(_root);
}
