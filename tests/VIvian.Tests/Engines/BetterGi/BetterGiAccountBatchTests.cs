using Microsoft.Data.Sqlite;
using VIvian.Core;
using VIvian.Core.Accounts;
using VIvian.Core.Execution;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Accounts;
using VIvian.Infrastructure.Engines.BetterGi;
using Xunit;

namespace VIvian.Tests.Engines.BetterGi;

public sealed class BetterGiAccountBatchTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EachAccountUsesItsOwnFrozenPlanAndTheNextWaitsForRestoration(bool restored)
    {
        await using var fixture = new BetterGiFixture();
        var path = Path.Combine(fixture.Root, "test.db");
        var database = new LocalDatabase(path);
        await database.InitializeAsync();
        var first = await Add(database, path, "A", "130****01", """{"dailyRewards":"true","AdventurersGuildCountry":"蒙德"}""");
        var second = await Add(database, path, "B", "130****02", """{"craftCondensedResin":"true","CraftingBenchCountry":"璃月"}""");
        var driver = new LoginDriver();
        var runs = new List<(Guid Id, string Task, string Country)>();

        var batch = new BetterGiAccountBatch(database, driver);
        var outcome = await batch.ExecuteAsync([first, second], (account, plan, _) =>
        {
            var task = Assert.Single(plan.Tasks).Id;
            runs.Add((account.Id, task, task == "genshin.daily_rewards"
                ? plan.Values["AdventurersGuildCountry"] : plan.Values["CraftingBenchCountry"]));
            return Task.FromResult(new BatchTaskResult(Guid.NewGuid(), true, true, restored, "模拟"));
        }, null, default);

        Assert.Equal((first.Id, "genshin.daily_rewards", "蒙德"), runs[0]);
        Assert.Equal(restored ? 2 : 1, runs.Count);
        if (restored) Assert.Equal((second.Id, "genshin.craft_condensed_resin", "璃月"), runs[1]);
        Assert.Equal(restored ? 1 : 0, driver.Returns);
        Assert.Equal(restored, outcome.Completed);
    }

    [Fact]
    public async Task EmptyAccountPlanFailsBeforeTouchingLogin()
    {
        await using var fixture = new BetterGiFixture();
        var path = Path.Combine(fixture.Root, "test.db");
        var database = new LocalDatabase(path);
        await database.InitializeAsync();
        var account = await Add(database, path, "A", "130****01", "{}");
        var driver = new LoginDriver();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new BetterGiAccountBatch(database, driver).ExecuteAsync([account],
                (_, _, _) => throw new InvalidOperationException("unexpected task"), null, default));
        Assert.Equal(0, driver.Ensures);
    }

    [Fact]
    public async Task UnconfirmedOutcomeStopsBeforeTheNextAccountEvenAfterCleanup()
    {
        await using var fixture = new BetterGiFixture();
        var path = Path.Combine(fixture.Root, "test.db");
        var database = new LocalDatabase(path);
        await database.InitializeAsync();
        var first = await Add(database, path, "A", "130****01", """{"dailyRewards":"true"}""");
        var second = await Add(database, path, "B", "130****02", """{"dailyRewards":"true"}""");
        var driver = new LoginDriver();
        var accountsRun = new List<Guid>();

        var outcome = await new BetterGiAccountBatch(database, driver).ExecuteAsync([first, second], (account, _, _) =>
        {
            accountsRun.Add(account.Id);
            return Task.FromResult(new BatchTaskResult(null, false, true, true, "缺少完成信号", RunOutcome.Indeterminate));
        }, null, default);

        Assert.Equal([first.Id], accountsRun);
        Assert.Equal(0, driver.Returns);
        Assert.False(outcome.Completed);
        Assert.Contains("结果未确认", outcome.Detail);
    }

    [Fact]
    public async Task DirectDailyRunRejectsOtherEnabledTasksInsteadOfSilentlySkippingThem()
    {
        await using var fixture = new BetterGiFixture();
        var path = Path.Combine(fixture.Root, "test.db");
        var database = new LocalDatabase(path);
        await database.InitializeAsync();
        var account = await Add(database, path, "A", "130****01", """{"dailyRewards":"true"}""");
        var name = BetterGiAccountPlan.SourceProfileName(account.Id);
        var profilePath = Path.Combine(fixture.Root, "User", "OneDragon", name + ".json");
        var game = Path.Combine(fixture.Root, "YuanShen.exe");

        await Assert.ThrowsAsync<InvalidDataException>(() => BetterGiAccountBatch.ReadSavedDailyAsync(fixture.Root, account, game, default));
        File.WriteAllText(profilePath, $$"""{"Name":"{{name}}","TaskEnabledList":{"领取每日奖励":true,"合成树脂":true},"CompletionAction":"关闭软件","AdventurersGuildCountry":"蒙德"}""");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => BetterGiAccountBatch.ReadSavedDailyAsync(fixture.Root, account, game, default));
        Assert.Contains("未通过实机准入", error.Message);

        File.WriteAllText(profilePath, $$"""{"Name":"{{name}}","TaskEnabledList":{"custom-group":true},"TaskDefinitions":{"custom-group":"领取每日奖励"},"CompletionAction":"关闭软件"}""");
        await Assert.ThrowsAsync<InvalidOperationException>(() => BetterGiAccountBatch.ReadSavedDailyAsync(fixture.Root, account, game, default));

        File.WriteAllText(profilePath, $$"""{"Name":"{{name}}","TaskEnabledList":{"领取每日奖励":true},"CompletionAction":"关闭软件","AdventurersGuildCountry":"蒙德"}""");
        Assert.Equal(name, (await BetterGiAccountBatch.ReadSavedDailyAsync(fixture.Root, account, game, default)).ProfileName);
    }

    private static async Task<GameAccount> Add(LocalDatabase db, string path, string label, string identity, string settings)
    {
        var account = await db.AddAccountAsync(GameId.Genshin, label, "", null, "");
        await db.SaveLoginProfileAsync(account.Id, identity, identity, 0, 0.95, null, "合成测试");
        await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE account_settings SET settings_json=$settings WHERE account_id=$account;";
        cmd.Parameters.AddWithValue("$settings", settings);
        cmd.Parameters.AddWithValue("$account", account.Id.ToString("D"));
        Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
        return account;
    }

    private sealed class LoginDriver : IGenshinLoginSwitchDriver
    {
        public int Ensures { get; private set; }
        public int Returns { get; private set; }

        public Task<LoginSwitchOutcome> EnsureLoginAsync(SwitchPolicy policy, CancellationToken cancellationToken)
        {
            Ensures++;
            return Done();
        }

        public Task<LoginSwitchOutcome> SwitchAsync(LoginTarget target, SwitchPolicy policy, CancellationToken cancellationToken) => Done();

        public Task<LoginSwitchOutcome> RestartToLoginAsync(SwitchPolicy policy, CancellationToken cancellationToken)
        {
            Returns++;
            return Done();
        }

        public Task<LoginSwitchOutcome> StopGameAsync(CancellationToken cancellationToken) => Done();

        private static Task<LoginSwitchOutcome> Done() =>
            Task.FromResult(LoginSwitchOutcome.Success(LoginSwitchStage.Done, "合成身份确认"));
    }
}
