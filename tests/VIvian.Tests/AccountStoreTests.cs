using Microsoft.Data.Sqlite;
using VIvian.Core;
using VIvian.Infrastructure;
using Xunit;

namespace VIvian.Tests;

public sealed class AccountStoreTests
{
    [Fact]
    public async Task MigratesLegacyPlansAndKeepsAccountsIndependent()
    {
        var path = Path.Combine(Path.GetTempPath(), "vivian-accounts-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var db = new LocalDatabase(path);
            await db.InitializeAsync();
            var legacy = (await db.LoadAccountsAsync(GameId.StarRail)).Single();
            Assert.Equal("原有账号", legacy.Name);
            var separateInstall = Path.Combine(Path.GetDirectoryName(path)!, "m7a-second");
            var other = await db.AddAccountAsync(GameId.StarRail, "第二个账号", "国服", "123456789", separateInstall);
            Assert.NotEqual(legacy.Id, other.Id);
            Assert.Equal(1, other.Order);
            Assert.Equal(separateInstall, other.InstallPath);
            var settings = await db.LoadAccountSettingsAsync(other.Id);
            await db.SaveAccountSettingsAsync(settings with
            { Values = new Dictionary<string, string> { ["borrow_enable"] = "false" } });
            Assert.Empty((await db.LoadAccountSettingsAsync(legacy.Id)).Values);
            Assert.Equal("false", (await db.LoadAccountSettingsAsync(other.Id)).Values["borrow_enable"]);
            var legacyPlan = await db.LoadAccountTaskPlanAsync(legacy.Id);
            var independentPlan = await db.LoadAccountTaskPlanAsync(other.Id);
            Assert.True(independentPlan.DailyEnabled);
            await db.SaveAccountTaskPlanAsync(independentPlan with { DailyEnabled = false });
            Assert.False((await db.LoadAccountTaskPlanAsync(other.Id)).DailyEnabled);
            Assert.Equal(legacyPlan.DailyEnabled, (await db.LoadAccountTaskPlanAsync(legacy.Id)).DailyEnabled);
            other = await db.SaveAccountAsync(other with { Name = "修改后", Enabled = false });
            Assert.False(other.Enabled);
            Assert.Equal("原有账号", (await db.LoadAccountsAsync(GameId.StarRail)).Single(a => a.Id == legacy.Id).Name);
            await Assert.ThrowsAsync<SettingsConflictException>(() => db.SaveAccountAsync(other with { Revision = 1 }));
            await db.DeleteAccountAsync(other.Id);
            Assert.Single(await db.LoadAccountsAsync(GameId.StarRail));
            await using var c = new SqliteConnection("Data Source=" + path + ";Pooling=False");
            await c.OpenAsync();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA foreign_key_check;";
            Assert.Null(await cmd.ExecuteScalarAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")) File.Delete(file);
        }
    }

    [Fact]
    public async Task VersionFourUpgradeAddsIsolatedInstallPathWithBackup()
    {
        var path = Path.Combine(Path.GetTempPath(), "vivian-v4-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var db = new LocalDatabase(path);
            await db.InitializeAsync();
            await using (var c = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
            {
                await c.OpenAsync();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "DROP TABLE account_configuration_audit; DROP TABLE account_scan_sessions; DROP TABLE account_login_profiles; ALTER TABLE batch_execution_records DROP COLUMN mode; ALTER TABLE game_accounts DROP COLUMN install_path; PRAGMA user_version=4;";
                await cmd.ExecuteNonQueryAsync();
            }
            await db.InitializeAsync();
            Assert.True(File.Exists(path + ".pre-v5.bak"));
            Assert.Equal("", (await db.LoadAccountsAsync(GameId.StarRail)).Single().InstallPath);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")) File.Delete(file);
        }
    }

    [Fact]
    public async Task InterruptedBatchRemainsRecordedAndRequiresNewIdentityCheck()
    {
        var path = Path.Combine(Path.GetTempPath(), "vivian-batch-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var db = new LocalDatabase(path);
            await db.InitializeAsync();
            var account = await db.AddAccountAsync(GameId.StarRail, "测试账号", "国服", "123456789");
            var batch = await db.BeginBatchAsync(GameId.StarRail);
            await db.RecordAccountSwitchAsync(null, account.Id, "manual_verified", "测试人工核对");
            await db.RecordAccountResultAsync(batch.Id, account.Id, null, "failed", "模拟引擎退出");
            var results = await db.LoadBatchResultsAsync(batch.Id);
            Assert.Single(results);
            Assert.Equal("测试账号", results[0].AccountName);
            Assert.Equal("failed", results[0].Result);
            await db.ReconcileInterruptedBatchesAsync();
            var recovered = (await db.LoadRecentBatchesAsync()).Single();
            Assert.Equal("interrupted", recovered.State);
            Assert.Equal("unverified", (await db.LoadAccountsAsync(GameId.StarRail)).Single(a => a.Id == account.Id).VerificationState);
            Assert.Equal("failed", (await db.LoadAccountsAsync(GameId.StarRail)).Single(a => a.Id == account.Id).LastResult);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")) File.Delete(file);
        }
    }
}
