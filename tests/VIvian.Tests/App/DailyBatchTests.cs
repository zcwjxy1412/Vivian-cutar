using VIvian.App;
using VIvian.Core;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Accounts;
using VIvian.Infrastructure.Engines.BetterGi;
using Xunit;

namespace VIvian.Tests.App;

public sealed class DailyBatchTests
{
    [Theory]
    [InlineData(null, true, true, true, true)]
    [InlineData(GameId.StarRail, true, true, true, false)]
    [InlineData(GameId.Genshin, true, true, true, true)]
    [InlineData(null, false, true, true, false)]
    [InlineData(null, true, false, true, false)]
    [InlineData(null, true, true, false, false)]
    public async Task StagesFollowCardScopeAndRequireConfirmedHandoff(GameId? scope, bool completed,
        bool gameClean, bool hostClean, bool runsGenshin)
    {
        var ran = new List<GameId>();
        var handoff = new TaskCompletionSource<MultiAccountOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = DailyBatchSequence.RunAsync(scope, _ => handoff.Task,
            _ => Task.FromResult(Outcome()), (game, _) => ran.Add(game), CancellationToken.None);
        if (scope != GameId.Genshin) Assert.Empty(ran);
        handoff.SetResult(Outcome(completed, gameClean, hostClean));
        await pending;

        var expected = new List<GameId>();
        if (scope != GameId.Genshin) expected.Add(GameId.StarRail);
        if (runsGenshin) expected.Add(GameId.Genshin);
        Assert.Equal(expected, ran);
    }

    [Fact]
    public async Task StopAfterFirstStageCannotStartTheSecondStage()
    {
        using var stop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DailyBatchSequence.RunAsync(null, token =>
            {
                Assert.Equal(stop.Token, token);
                stop.Cancel();
                return Task.FromResult(Outcome());
            }, _ => throw new InvalidOperationException("下一阶段不应运行"), (_, _) => { }, stop.Token));
    }

    [Fact]
    public async Task GenshinSelectionsAndOrderPersistIndependently()
    {
        var root = Directory.CreateTempSubdirectory("vivian-app-batch-");
        try
        {
            var db = new LocalDatabase(Path.Combine(root.FullName, "test.db"));
            await db.InitializeAsync();
            var starRail = await db.LoadAccountsAsync(GameId.StarRail);
            var first = await db.AddAccountAsync(GameId.Genshin, "原神甲", "", null);
            var second = await db.AddAccountAsync(GameId.Genshin, "原神乙", "", null);
            await db.SaveLoginProfileAsync(first.Id, "130****01", "130****01", 0, .95, null, "合成");
            await db.SaveLoginProfileAsync(second.Id, "130****02", "130****02", 1, .95, null, "合成");
            var view = new GenshinConfigurationViewModel(db, new GenshinAccountConfigurationProvider(db),
                () => true, root.FullName, () => true, () => "", action => action());
            await view.LoadAsync();
            await view.ToggleAutoRunAsync(view.Accounts.Single(a => a.Id == first.Id));
            await view.ToggleAutoRunAsync(view.Accounts.Single(a => a.Id == second.Id));
            await view.MoveAccountAsync(-1, view.Accounts.Single(a => a.Id == second.Id));
            await view.ToggleAutoRunAsync(view.Accounts.First(a => a.LoginState != "recognized"));

            Assert.Equal(new[] { second.Id, first.Id }, view.SelectedForDaily.Select(a => a.Id));
            Assert.Equal(new[] { second.Id, first.Id }, (await db.LoadAccountsAsync(GameId.Genshin))
                .Where(a => a.AutoRun).Select(a => a.Id));
            Assert.Equal(starRail, await db.LoadAccountsAsync(GameId.StarRail));
            view.Close();
        }
        finally { root.Delete(recursive: true); }
    }

    private static MultiAccountOutcome Outcome(bool completed = true, bool gameClean = true, bool hostClean = true) =>
        new(Guid.NewGuid(), completed, 1, 0, "模拟", HostCleanupConfirmed: hostClean, GameCleanupConfirmed: gameClean);
}
