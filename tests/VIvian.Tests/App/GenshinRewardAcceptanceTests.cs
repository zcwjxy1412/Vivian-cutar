using VIvian.App;
using VIvian.Core;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Accounts;
using Xunit;

namespace VIvian.Tests.App;

public sealed class GenshinRewardAcceptanceTests
{
    private static readonly Guid Attempt = Guid.NewGuid();
    private static readonly Guid Account = Guid.NewGuid();

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
