using VIvian.Infrastructure.Engines.BetterGi;
using Xunit;

namespace VIvian.Tests.Engines.BetterGi;

public sealed class BetterGiAcceptanceStageTests
{
    private const string Game = @"E:\Genshin Impact Game\YuanShen.exe";

    [Fact]
    public async Task WritesTheBoundPlanAndReadsItBackBeforePublication()
    {
        await using var fixture = new BetterGiFixture(createInstall: false);
        Directory.CreateDirectory(Path.Combine(fixture.Root, "BetterGI", "User"));
        var id = Guid.NewGuid();
        var plan = DailyPlan(id);

        await BetterGiAcceptanceStage.WritePlanAsync(fixture.Root, id, Game, plan, CancellationToken.None);

        Assert.Equal(plan.ProfileJson, File.ReadAllText(ProfilePath(fixture.Root, plan)));
        Assert.Equal(plan.SettingsJson, File.ReadAllText(SettingsPath(fixture.Root)));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "verified-archive.txt")));
    }

    [Fact]
    public async Task ExistingPlanCannotBeOverwrittenOrPartiallyReplaced()
    {
        await using var fixture = new BetterGiFixture(createInstall: false);
        Directory.CreateDirectory(Path.Combine(fixture.Root, "BetterGI", "User"));
        await File.WriteAllTextAsync(SettingsPath(fixture.Root), """{"untouched":true}""");
        var id = Guid.NewGuid();
        var plan = DailyPlan(id);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BetterGiAcceptanceStage.WritePlanAsync(fixture.Root, id, Game, plan, CancellationToken.None));

        Assert.Equal("""{"untouched":true}""", File.ReadAllText(SettingsPath(fixture.Root)));
        Assert.False(File.Exists(ProfilePath(fixture.Root, plan)));
    }

    [Fact]
    public async Task WrongAccountOrUnsafeStartupDoesNotWritePlanFiles()
    {
        await using var fixture = new BetterGiFixture(createInstall: false);
        Directory.CreateDirectory(Path.Combine(fixture.Root, "BetterGI", "User"));
        var id = Guid.NewGuid();
        var plan = DailyPlan(id);
        var unsafeStartup = plan with
        {
            SettingsJson = plan.SettingsJson.Replace("\"autoEnterGameEnabled\": false", "\"autoEnterGameEnabled\": true",
                StringComparison.Ordinal),
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BetterGiAcceptanceStage.WritePlanAsync(fixture.Root, Guid.NewGuid(), Game, plan, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BetterGiAcceptanceStage.WritePlanAsync(fixture.Root, id, Game, unsafeStartup, CancellationToken.None));

        Assert.False(File.Exists(SettingsPath(fixture.Root)));
        Assert.False(File.Exists(ProfilePath(fixture.Root, plan)));
    }

    private static BetterGiPlanFiles DailyPlan(Guid id) =>
        BetterGiAccountPlans.Compile(new Dictionary<string, string> { ["dailyRewards"] = "true" }).Render(id, Game);

    private static string SettingsPath(string root) => Path.Combine(root, "BetterGI", "User", "config.json");

    private static string ProfilePath(string root, BetterGiPlanFiles plan) =>
        Path.Combine(root, "BetterGI", "User", "OneDragon", plan.ProfileName + ".json");
}
