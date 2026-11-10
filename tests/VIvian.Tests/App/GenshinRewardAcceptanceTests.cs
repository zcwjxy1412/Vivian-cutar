using VIvian.App;
using VIvian.Core;
using VIvian.Core.Execution;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Accounts;
using VIvian.Infrastructure.Simulation;
using Xunit;

namespace VIvian.Tests.App;

public sealed class GenshinRewardAcceptanceTests
{
    private static readonly Guid Attempt = Guid.NewGuid();
    private static readonly Guid Account = Guid.NewGuid();

    [Fact]
    public async Task AcceptanceWorkspaceKeepsLogReviewAndBlocksOrdinaryOperations()
    {
        var folder = Directory.CreateTempSubdirectory("vivian-acceptance-shell-");
        try
        {
            var database = new LocalDatabase(Path.Combine(folder.FullName, "vivian.db"));
            await database.InitializeAsync();
            var paths = new AppPaths(folder.FullName, "隔离验收 · 原神单账号领奖 · 0 树脂");
            var supervisor = new RefusingProcessSupervisor();
            var queue = new QueueViewModel(new QueueScheduler(database, supervisor), database, paths);
            var accounts = new AccountViewModel(database, new AccountRecognitionService(database, new LoginScreenReader()),
                NoScanWindowShield.Instance);
            var vm = new MainViewModel(database, database, database, database, paths, queue, supervisor,
                accounts, new MultiAccountOrchestrator(database, null!), new GamePathProvider());

            Assert.Contains("隔离验收", vm.ExecutionBanner);
            Assert.False(vm.CanOperateWorkspace);
            Assert.False(vm.CanEditWorkspace);
            Assert.False(vm.CanStartMarch7th);
            Assert.False(queue.CanOperateWorkspace);
            Assert.False(queue.CanAcknowledge);
            Assert.False(vm.GenshinConfiguration.CanEditWorkspace);
            Assert.All(vm.Engines, card => Assert.False(card.CanRunForReal));
            var first = await database.AddAccountAsync(GameId.Genshin, "先入档", "", null);
            await database.SaveLoginProfileAsync(first.Id, "130****01", "130****01", 0, .95, null, "模拟");
            var target = await database.AddAccountAsync(GameId.Genshin, "实际验收账号", "", null);
            await vm.GenshinConfiguration.LoadAsync(target.Id);
            Assert.Equal(target.Id, vm.GenshinConfiguration.SelectedAccount?.Id);
            await vm.GenshinConfiguration.LoadAsync(Guid.NewGuid());
            Assert.Null(vm.GenshinConfiguration.SelectedAccount);
            Assert.Null(await queue.RunDemoAsync([(GameId.StarRail, DemoScenario.Success)]));
            var runId = Guid.NewGuid();
            await database.PublishAsync(new RunUpdate(runId, runId, GameId.Genshin, "fixture/acceptance", false,
                RunPhase.Finished, RunOutcome.Indeterminate, CleanupState.Unconfirmed, ReasonCodes.CleanupUnconfirmed,
                DateTimeOffset.UtcNow), CancellationToken.None);
            await queue.RefreshAsync();
            Assert.Equal(runId, Assert.Single(queue.Runs).RunId);
            await queue.AcknowledgeAllAsync();
            Assert.Equal(runId, Assert.Single(await database.ReadUnresolvedAsync()).RunId);
            vm.Accounts.ScanCommand.Execute(null);
            Assert.Contains("隔离验收", vm.Status);
            await Assert.ThrowsAsync<InvalidOperationException>(() => vm.SerializeGenshinOperationAsync(
                () => throw new Exception("不应修改源档案")));
            vm.Configuration.OpenUpstreamCommand.Execute(null);
            Assert.Contains("任务运行中", vm.Configuration.Status);
            vm.GenshinConfiguration.Close();
        }
        finally { folder.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(true, true, true, false, true, 0)]
    [InlineData(false, true, true, false, true, 1)]
    [InlineData(true, false, true, false, true, null)]
    [InlineData(true, true, false, false, true, null)]
    [InlineData(true, true, true, true, true, null)]
    [InlineData(true, true, true, false, false, null)]
    public void CommandLineExitRequiresProcessCleanupAndNoPendingRecovery(bool passed, bool gameClean,
        bool hostClean, bool pendingRecovery, bool desktopClean, int? expectedExitCode)
    {
        var outcome = new MultiAccountOutcome(Guid.NewGuid(), passed, passed ? 1 : 0, passed ? 0 : 1,
            "模拟", GameCleanupConfirmed: gameClean, HostCleanupConfirmed: hostClean);
        var checkedDesktop = false;
        Assert.Equal(expectedExitCode, GenshinRewardAcceptance.AutoExitCode(outcome, passed, pendingRecovery, () =>
        {
            checkedDesktop = true;
            return desktopClean;
        }));
        Assert.Equal(gameClean && hostClean && !pendingRecovery, checkedDesktop);
    }

    [Fact]
    public void LaunchRequiresCandidateAndMarkerAndRejectsMixedArguments()
    {
        var args = new[] { "--genshin-reward-acceptance", Attempt.ToString("D"), Account.ToString("D"), "2" };
        Assert.Throws<InvalidOperationException>(() => GenshinRewardAcceptance.Parse(args, false, true, @"C:\Data"));
        Assert.Throws<InvalidOperationException>(() => GenshinRewardAcceptance.Parse(args, true, false, @"C:\Data"));
        Assert.Throws<ArgumentException>(() => GenshinRewardAcceptance.Parse([.. args, "--smoke-test"], true, true, @"C:\Data"));
        Assert.Throws<ArgumentException>(() => GenshinRewardAcceptance.Parse([.. args, "--craft"], true, true, @"C:\Data"));
        var request = Assert.IsType<GenshinRewardAcceptance>(GenshinRewardAcceptance.Parse(args, true, true, @"C:\Data"));
        Assert.Equal(Path.Combine(@"C:\Data", "VIvian-M6-GenshinAcceptance", Attempt.ToString("N")), request.ProfileDirectory);
        Assert.False(request.ReviewOnly);
        Assert.Contains("0 树脂", request.WorkspaceScope);
        Assert.True(GenshinRewardAcceptance.Parse(["--genshin-reward-review", Attempt.ToString("D")],
            true, true, @"C:\Data")!.ReviewOnly);
    }

    [Fact]
    public async Task CallsOnlyTheNamedGenshinAccountWithRewardsAndPassesTheStopToken()
    {
        var folder = Directory.CreateTempSubdirectory("vivian-reward-app-");
        try
        {
            var a = Guid.NewGuid();
            var profile = Path.Combine(folder.FullName, "VIvian-M6-GenshinAcceptance", a.ToString("N"));
            Directory.CreateDirectory(profile);
            var db = new LocalDatabase(Path.Combine(profile, "vivian.db"));
            await db.InitializeAsync();
            var account = await db.AddAccountAsync(GameId.Genshin, "A", "", null);
            await db.SaveLoginProfileAsync(account.Id, "130****01", "130****01", 0, .95, null, "模拟");
            var other = await db.AddAccountAsync(GameId.Genshin, "B", "", null);
            await db.SaveAccountAsync(other with { AutoRun = true });
            var star = await db.AddAccountAsync(GameId.StarRail, "崩铁", "", null);
            await db.SaveAccountAsync(star with { AutoRun = true });
            account = (await db.LoadAccountsAsync(GameId.Genshin)).Single(x => x.Id == account.Id);

            GenshinRewardAcceptance Request(Guid id, long revision) => GenshinRewardAcceptance.Parse(
                ["--genshin-reward-acceptance", a.ToString("D"), id.ToString("D"), revision.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                true, true, folder.FullName)!;

            var calls = 0;
            using var stop = new CancellationTokenSource();
            GenshinDailyVerification fake = (database, selected, root, archive, directory, rewardOnly, _, ct) =>
            {
                Assert.Same(db, database);
                Assert.Equal(account, Assert.Single(selected));
                Assert.Equal(("BetterGI", "archive", profile), (root, archive, directory));
                Assert.True(rewardOnly);
                Assert.Equal(stop.Token, ct);
                calls++;
                stop.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(new MultiAccountOutcome(Guid.NewGuid(), true, 1, 0, "模拟"));
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() => Request(star.Id, star.Revision + 1)
                .RunAsync(db, "BetterGI", "archive", profile, fake, null, stop.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Request(account.Id, account.Revision + 1)
                .RunAsync(db, "BetterGI", "archive", profile, fake, null, stop.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Request(account.Id, account.Revision)
                .RunAsync(db, "BetterGI", "archive", folder.FullName, fake, null, stop.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Request(account.Id, account.Revision)
                .RunAsync(db, "BetterGI", "archive", profile, fake, null, stop.Token));
            Assert.Equal(1, calls);
            Assert.Equal(account, (await db.LoadAccountsAsync(GameId.Genshin)).Single(x => x.Id == account.Id));
        }
        finally { folder.Delete(recursive: true); }
    }
}
