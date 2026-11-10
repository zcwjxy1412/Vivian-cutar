using VIvian.App;
using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Core.Execution;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Accounts;
using VIvian.Infrastructure.Simulation;
using Xunit;

namespace VIvian.Tests.App;

public sealed class GenshinHomeRewardTests
{
    [Fact]
    public async Task FormalPlayCardRunsFirstCheckedAccountInSavedOrderAndKeepsTheRegularQueue()
    {
        var folder = Directory.CreateTempSubdirectory("vivian-home-card-");
        try
        {
            var database = new LocalDatabase(Path.Combine(folder.FullName, "vivian.db"));
            await database.InitializeAsync();
            var first = await database.AddAccountAsync(GameId.Genshin, "先建档", "", null);
            var second = await database.AddAccountAsync(GameId.Genshin, "排在最前", "", null);
            await database.SaveLoginProfileAsync(first.Id, "130****01", "130****01", 0, .95, null, "合成");
            await database.SaveLoginProfileAsync(second.Id, "130****02", "130****02", 1, .95, null, "合成");
            await database.RecordAccountSwitchAsync(null, second.Id, "logged_in", "已核对入场");
            var paths = new AppPaths(folder.FullName);
            var supervisor = new RefusingProcessSupervisor();
            var queue = new QueueViewModel(new QueueScheduler(database, supervisor), database, paths);
            var accounts = new AccountViewModel(database, new AccountRecognitionService(database, new LoginScreenReader()),
                NoScanWindowShield.Instance);
            var vm = new MainViewModel(database, database, database, database, paths, queue, supervisor,
                accounts, new MultiAccountOrchestrator(database, null!), new GamePathProvider());
            vm.ConfigureHomeGenshinReward(new GenshinHomeReward(paths.DataDirectory,
                database,
                (_, _, _, _, _, _, _, _) => throw new InvalidOperationException("此用例不启动引擎")));
            await vm.GenshinConfiguration.LoadAsync();
            var card = vm.Engines.Single(x => x.Id == GameId.Genshin);
            card.InstallPath = card.SavedInstallPath = @"C:\PinnedBetterGI";
            card.Show(new EngineReadinessReport(GameId.Genshin, "BetterGI", "0.66.0", CompatibilityClass.PendingValidation,
                "0.66.0", null, null, [new("fixture", "模拟", CheckState.Passed, EngineErrorCategory.None, "模拟")], DateTimeOffset.UtcNow));

            Assert.True(card.CanPlayFromHome);
            Assert.False(card.CanRunForReal);
            Assert.Contains("保存", card.RunHint);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopScopes = 0;
            vm.RunWithHomeRewardStopKey = async _ =>
            {
                stopScopes++;
                Assert.Contains(second.Name, vm.Status);
                await release.Task;
                return null;
            };
            await vm.PlayGameCardAsync(GameId.Genshin);
            Assert.Contains("账号管理", vm.Status);
            Assert.Equal(0, stopScopes);

            GameAccount Row(Guid id) => vm.GenshinConfiguration.Accounts.Single(a => a.Id == id);
            await vm.GenshinConfiguration.ToggleAutoRunAsync(Row(first.Id));
            await vm.GenshinConfiguration.ToggleAutoRunAsync(Row(second.Id));
            await vm.GenshinConfiguration.MoveAccountAsync(-1, Row(second.Id));
            var running = vm.PlayGameCardAsync(GameId.Genshin);
            try
            {
                Assert.Equal(1, stopScopes);
                var duplicate = vm.PlayGameCardAsync(GameId.Genshin);
                Assert.True(duplicate.IsCompleted);
                await duplicate;
                Assert.Equal(1, stopScopes);
            }
            finally { release.SetResult(); }
            await running;

            if (RealExecutionPolicy.IsM5DailyTest)
            {
                await vm.OneClickAsync();
                Assert.Equal(2, stopScopes);

                var star = await database.AddAccountAsync(GameId.StarRail, "星铁日常", "", null);
                await database.SaveLoginProfileAsync(star.Id, "130****03", "130****03", 0, .95, null, "合成");
                await accounts.ReloadAsync();
                accounts.Rows.Single(row => row.Id == star.Id).IsSelected = true;
                await vm.PlayGameCardAsync(GameId.StarRail);
                var starRailStatus = vm.BatchStatus;
                Assert.DoesNotContain("原神卡片", starRailStatus);
                await vm.OneClickAsync();
                Assert.StartsWith(starRailStatus, vm.BatchStatus);
                Assert.DoesNotContain("原神卡片", vm.BatchStatus);
                Assert.Equal(3, stopScopes);
            }
            vm.GenshinConfiguration.Close();
        }
        finally { folder.Delete(recursive: true); }
    }

    [Fact]
    public async Task HomeRunnerUsesInstalledDatabaseAndAllSavedAccountsWithoutRewardOnlyOverride()
    {
        var temp = Directory.CreateTempSubdirectory("vivian-home-daily-");
        try
        {
            var live = new LocalDatabase(Path.Combine(temp.FullName, "vivian.db"));
            await live.InitializeAsync();
            var first = await live.AddAccountAsync(GameId.Genshin, "甲", "", null);
            var second = await live.AddAccountAsync(GameId.Genshin, "乙", "", null);
            foreach (var account in new[] { first, second })
            {
                await live.SaveAccountAsync(account with { AutoRun = true });
                await live.SaveLoginProfileAsync(account.Id, account.Id == first.Id ? "130****01" : "130****02",
                    account.Id == first.Id ? "130****01" : "130****02", 0, .95, null, "合成");
            }
            await live.RecordAccountSwitchAsync(null, first.Id, "logged_in", "已核对入场");
            var selected = (await live.LoadAccountsAsync(GameId.Genshin))
                .Where(a => a.Id == first.Id || a.Id == second.Id).OrderBy(a => a.Order).ThenBy(a => a.Id).ToArray();
            var run = Guid.NewGuid();
            var called = 0;
            var home = new GenshinHomeReward(temp.FullName, live,
                async (database, accounts, _, _, owner, rewardOnly, _, ct) =>
                {
                    called++;
                    Assert.Same(live, database);
                    Assert.Equal(temp.FullName, owner);
                    Assert.False(rewardOnly);
                    Assert.Equal(selected.Select(a => a.Id), accounts.Select(a => a.Id));
                    await database.PublishAsync(new(run, run, GameId.Genshin, "fixture/acceptance", false,
                        RunPhase.Finished, RunOutcome.Indeterminate, CleanupState.Unconfirmed,
                        ReasonCodes.CleanupUnconfirmed, DateTimeOffset.UtcNow), ct);
                    return new MultiAccountOutcome(run, false, 0, 1, "模拟");
                });
            var stale = home.CreateRequest(selected[0]);
            await live.RecordAccountSwitchAsync(null, selected[0].Id, "identity_uncertain", "上一轮未读到 UID");
            await live.SaveAccountAsync(selected[0] with { Name = "已修改" });
            await home.RunAsync(stale, "root", "archive", null, default);
            Assert.Equal(1, called);
            Assert.Equal(run, Assert.Single(await live.ReadRecentRunsAsync(5)).RunId);
            Assert.Empty(await home.ReadRecentRunsAsync(5)); // No new isolated history.
            await Assert.ThrowsAsync<InvalidOperationException>(() => home.RunAsync(stale, "root", "archive", null, default));
            Assert.Equal(1, called);
        }
        finally { temp.Delete(recursive: true); }
    }
}
