using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Engines.BetterGi;
using Xunit;

namespace VIvian.Tests.Engines.BetterGi;

public sealed class GenshinAccountConfigurationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vivian-genshin-plan-").FullName;
    private string DbPath => Path.Combine(_root, "profiles.db");
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task SaveIsAccountScopedAndRecordsTheVerifiedBeforeAndAfter()
    {
        var (db, provider, a, b) = await SetupAsync();
        var first = await provider.ReadAsync(a, Ct);
        var second = await provider.ReadAsync(b, Ct);
        Assert.NotEqual(first.Revision, second.Revision);
        Assert.Equal("false", Value(first, "dailyRewards"));
        var proposal = await provider.ProposeAsync(a, first.Revision, new Dictionary<string, string> { ["dailyRewards"] = "true" }, Ct);
        Assert.True(proposal.Validation.Valid);
        Assert.Empty((await db.LoadAccountSettingsAsync(a.AccountId)).Values);

        var applied = await provider.ApplyAsync(proposal, () => true, Ct);
        var reread = await provider.ReadAsync(a, Ct);
        Assert.Equal(applied.Revision, reread.Revision);
        Assert.Equal("true", Value(reread, "dailyRewards"));
        Assert.Equal("false", Value(await provider.ReadAsync(b, Ct), "dailyRewards"));
        Assert.Equal(("{}", "true"), await AuditAsync(applied.AuditId));
        await Assert.ThrowsAsync<SettingsConflictException>(() => provider.ApplyAsync(proposal, () => true, Ct));
    }

    [Fact]
    public async Task StaleAccountsCrossGameValuesAndInvalidPlansCannotWrite()
    {
        var (db, provider, a, b) = await SetupAsync();
        var first = await provider.ReadAsync(a, Ct);
        await Assert.ThrowsAsync<SettingsConflictException>(() => provider.ProposeAsync(b, first.Revision, new Dictionary<string, string>(), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ReadAsync(a with { Game = GameId.StarRail }, Ct));
        var starRail = Assert.Single(await db.LoadAccountsAsync(GameId.StarRail));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ReadAsync(new(GameId.Genshin, starRail.Id), Ct));

        var invalid = await provider.ProposeAsync(a, first.Revision, new Dictionary<string, string> { ["autoDomain"] = "true" }, Ct);
        Assert.False(invalid.Validation.Valid);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ApplyAsync(invalid, () => true, Ct));
        var stale = await provider.ProposeAsync(a, first.Revision, new Dictionary<string, string> { ["dailyRewards"] = "true" }, Ct);
        var account = (await db.LoadAccountsAsync(GameId.Genshin)).Single(x => x.Id == a.AccountId);
        await db.SaveAccountAsync(account with { Name = "账号已更名" });
        await Assert.ThrowsAsync<SettingsConflictException>(() => provider.ApplyAsync(stale, () => true, Ct));

        var settings = await db.LoadAccountSettingsAsync(a.AccountId);
        await Assert.ThrowsAsync<SettingsConflictException>(() => db.SaveAccountSettingsAsync(settings with
        {
            Values = new Dictionary<string, string> { ["borrow_enable"] = "true" },
        }));
        Assert.Empty((await db.LoadAccountSettingsAsync(a.AccountId)).Values);
    }

    [Fact]
    public async Task SelectionChangeDuringCommitRollsBackBothPlanAndAudit()
    {
        var (db, provider, a, _) = await SetupAsync();
        var current = await provider.ReadAsync(a, Ct);
        var proposal = await provider.ProposeAsync(a, current.Revision, new Dictionary<string, string> { ["dailyRewards"] = "true" }, Ct);
        var checks = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ApplyAsync(proposal, () => ++checks < 3, Ct));
        Assert.Empty((await db.LoadAccountSettingsAsync(a.AccountId)).Values);
        Assert.Null(await AuditAsync(proposal.Id));
    }

    [Fact]
    public async Task CorruptGenshinPlanIsReportedAtItsOwnBoundary()
    {
        var (db, provider, a, b) = await SetupAsync();
        await using (var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE account_settings SET settings_json='{' WHERE account_id=$id";
            command.Parameters.AddWithValue("$id", a.AccountId.ToString("D"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ReadAsync(a, Ct));
        Assert.Contains("无法读取", error.Message);
        Assert.Equal("false", Value(await provider.ReadAsync(b, Ct), "dailyRewards"));
        var starRail = Assert.Single(await db.LoadAccountsAsync(GameId.StarRail));
        Assert.NotNull(await db.LoadAccountTaskPlanAsync(starRail.Id));
    }

    private async Task<(LocalDatabase Db, GenshinAccountConfigurationProvider Provider, AccountConfigurationTarget A, AccountConfigurationTarget B)> SetupAsync()
    {
        var db = new LocalDatabase(DbPath);
        await db.InitializeAsync();
        var a = await db.AddAccountAsync(GameId.Genshin, "原神 A", "", null);
        var b = await db.AddAccountAsync(GameId.Genshin, "原神 B", "", null);
        var provider = new GenshinAccountConfigurationProvider(db);
        return (db, provider, new(GameId.Genshin, a.Id), new(GameId.Genshin, b.Id));
    }

    private static string? Value(AccountConfigurationView view, string key) => view.Values.Single(v => v.Schema.Path == key).Value;

    private async Task<(string Before, string After)?> AuditAsync(Guid id)
    {
        await using var connection = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT before_json, after_json FROM account_configuration_audit WHERE proposal_id=$id AND game=$game;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$game", (int)GameId.Genshin);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return (reader.GetString(0), JsonNode.Parse(reader.GetString(1))!["dailyRewards"]!.GetValue<string>());
    }

    public void Dispose() => TestDirectories.DeleteWithRetry(_root);
}
