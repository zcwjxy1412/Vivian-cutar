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
    public async Task FormalPlayCardOpensAnExplicitSelectionWithoutAdmittingFullDaily()
    {
        var folder = Directory.CreateTempSubdirectory("vivian-home-card-");
        try
        {
            var database = new LocalDatabase(Path.Combine(folder.FullName, "vivian.db"));
            await database.InitializeAsync();
            var account = await database.AddAccountAsync(GameId.Genshin, "领奖账号", "", null);
            await database.SaveLoginProfileAsync(account.Id, "130****01", "130****01", 0, .95, null, "合成");
            var paths = new AppPaths(folder.FullName);
            var supervisor = new RefusingProcessSupervisor();
            var queue = new QueueViewModel(new QueueScheduler(database, supervisor), database, paths);
            var accounts = new AccountViewModel(database, new AccountRecognitionService(database, new LoginScreenReader()),
                NoScanWindowShield.Instance);
            var vm = new MainViewModel(database, database, database, database, paths, queue, supervisor,
                accounts, new MultiAccountOrchestrator(database, null!), new GamePathProvider());
            vm.ConfigureHomeGenshinReward(new GenshinHomeReward(paths.DataDirectory,
                (_, _) => throw new InvalidOperationException("打开选择区不应准备快照"),
                (_, _, _, _, _, _, _, _) => throw new InvalidOperationException("打开选择区不应启动引擎")));
            await vm.GenshinConfiguration.LoadAsync();
            var card = vm.Engines.Single(x => x.Id == GameId.Genshin);
            card.InstallPath = card.SavedInstallPath = @"C:\PinnedBetterGI";
            card.Show(new EngineReadinessReport(GameId.Genshin, "BetterGI", "0.66.0", CompatibilityClass.PendingValidation,
                "0.66.0", null, null, [new("fixture", "模拟", CheckState.Passed, EngineErrorCategory.None, "模拟")], DateTimeOffset.UtcNow));

            Assert.True(card.CanPlayFromHome);
            Assert.False(card.CanRunForReal);
            Assert.Contains("0 树脂", card.RunHint);
            vm.PlayGameCardCommand.Execute(GameId.Genshin);
            Assert.True(vm.ShowHomeGenshinReward);
            Assert.Equal(account.Id, Assert.Single(vm.HomeGenshinRewardAccounts).Id);
            Assert.Null(vm.HomeGenshinRewardAccount);
            Assert.False(vm.CanConfirmHomeGenshinReward);
            vm.HomeGenshinRewardAccount = Assert.Single(vm.HomeGenshinRewardAccounts);
            Assert.True(vm.CanConfirmHomeGenshinReward);
            vm.CancelHomeGenshinRewardCommand.Execute(null);
            Assert.False(vm.CanConfirmHomeGenshinReward);
            vm.GenshinConfiguration.Close();
        }
        finally { folder.Delete(recursive: true); }
    }

    [Fact]
    public async Task HomeRunnerUsesOnlyTheConfirmedAccountAndRestoresTheExactIsolatedRun()
    {
        var temp = Directory.CreateTempSubdirectory("vivian-home-reward-");
        try
        {
            var official = Directory.CreateDirectory(Path.Combine(temp.FullName, "official")).FullName;
            var live = new LocalDatabase(Path.Combine(official, "vivian.db"));
            await live.InitializeAsync();
            var selected = await live.AddAccountAsync(GameId.Genshin, "甲", "", null);
            await live.SaveLoginProfileAsync(selected.Id, "130****01", "130****01", 0, .95, null, "合成");
            selected = (await live.LoadAccountsAsync(GameId.Genshin)).Single(a => a.Id == selected.Id);
            var run = Guid.NewGuid();
            var called = 0;
            var snapshot = "";
            var changeRevision = true;
            async Task Prepare(string destination, CancellationToken ct)
            {
                snapshot = destination;
                Directory.CreateDirectory(destination);
                var copiedFile = Path.Combine(destination, "vivian.db");
                File.Copy(Path.Combine(official, "vivian.db"), copiedFile);
                if (changeRevision)
                    await new LocalDatabase(copiedFile).SaveAccountAsync(selected with { AutoRun = true }, ct);
            }
            var home = new GenshinHomeReward(official, Prepare,
                async (database, accounts, _, _, owner, rewardOnly, _, ct) =>
                {
                    called++;
                    Assert.Equal(official, owner);
                    Assert.True(rewardOnly);
                    Assert.Equal(selected.Id, Assert.Single(accounts).Id);
                    await database.PublishAsync(new(run, run, GameId.Genshin, "fixture/acceptance", false,
                        RunPhase.Finished, RunOutcome.Indeterminate, CleanupState.Unconfirmed,
                        ReasonCodes.CleanupUnconfirmed, DateTimeOffset.UtcNow), ct);
                    return new MultiAccountOutcome(run, false, 0, 1, "模拟");
                }, path => new LocalDatabase(Path.Combine(path, "vivian.db")));

            var request = home.CreateRequest(selected);
            await Assert.ThrowsAsync<InvalidOperationException>(() => home.RunAsync(request, "root", "archive", null, default));
            Assert.Equal(0, called);
            changeRevision = false;
            await home.RunAsync(home.CreateRequest(selected), "root", "archive", null, default);
            Assert.Equal(1, called);
            Assert.Equal(run, Assert.Single(await home.ReadRecentRunsAsync(5)).RunId);
            Assert.Empty(await live.ReadRecentRunsAsync(5));
            await Assert.ThrowsAsync<InvalidOperationException>(() => home.RunAsync(home.CreateRequest(selected), "root", "archive", null, default));
            Assert.Equal(1, called);
            Assert.True(await home.RecordRecoveryAsync(run, default));
            Assert.True(await home.RecordRecoveryAsync(run, default));
            Assert.False(await home.RecordRecoveryAsync(Guid.NewGuid(), default));
            Assert.Empty(await new LocalDatabase(Path.Combine(snapshot, "vivian.db")).ReadUnresolvedAsync());
        }
        finally { temp.Delete(recursive: true); }
    }
}
