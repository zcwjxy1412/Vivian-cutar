using Microsoft.Data.Sqlite;
using VIvian.Core;
using VIvian.Infrastructure;
using Xunit;

namespace VIvian.Tests;

public sealed class AccountConfigurationMigrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vivian-schema-").FullName;

    [Fact]
    public async Task SchemaSixAccountSettingsSurviveUpgradeAndBackupCanRestoreThem()
    {
        var path = Path.Combine(_root, "accounts.db");
        var db = new LocalDatabase(path);
        await db.InitializeAsync();
        var account = await db.AddAccountAsync(GameId.StarRail, "测试账号", "测试区", null);
        var before = await db.LoadAccountSettingsAsync(account.Id);
        before = await db.SaveAccountSettingsAsync(before with { Values = new Dictionary<string, string> { ["borrow_enable"] = "false" } });
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE account_configuration_audit; PRAGMA user_version=6;";
            await command.ExecuteNonQueryAsync();
        }

        await db.InitializeAsync();
        Assert.Equal(7, await VersionAsync(path));
        var backup = path + ".pre-v7.bak";
        Assert.Equal(6, await VersionAsync(backup));

        var restoredPath = Path.Combine(_root, "restored.db");
        File.Copy(backup, restoredPath);
        var restored = new LocalDatabase(restoredPath);
        foreach (var store in new[] { db, restored })
        {
            var settings = await store.LoadAccountSettingsAsync(account.Id);
            Assert.Equal(before.Revision, settings.Revision);
            Assert.Equal("false", settings.Values["borrow_enable"]);
        }
    }

    private static async Task<long> VersionAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public void Dispose() => TestDirectories.DeleteWithRetry(_root);
}
