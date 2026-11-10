using System.Text.Json.Nodes;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Engines.BetterGi;
using Xunit;

namespace VIvian.Tests.Engines.BetterGi;

public sealed class BetterGiDailyAcceptanceTests
{
    private static readonly Guid Account = new("1810a252-65f3-4ee9-afb0-75c80757c33d");
    private const string Client = @"C:\Games\YuanShen.exe";

    [Fact]
    public async Task ReadsTheExactRenderedAccountAndPreservesTheLiteralPartyName()
    {
        await using var fixture = new BetterGiFixture();
        var plan = Render(party: "当前队伍");
        Write(fixture.Root, plan);

        Assert.Equal(Client, BetterGiDailyAcceptance.ValidateDailyFiles(fixture.Root, Account, plan));
        Assert.Throws<InvalidDataException>(() => BetterGiDailyAcceptance.ValidateDailyFiles(fixture.Root, Guid.NewGuid(), plan));
    }

    [Theory]
    [InlineData("launch")]
    [InlineData("update")]
    [InlineData("craft")]
    [InlineData("close_game")]
    public async Task RejectsUnsafeSettingsEvenWhenOnDiskAndExpectedFilesMatch(string unsafeChange)
    {
        await using var fixture = new BetterGiFixture();
        var plan = Render();
        var settings = JsonNode.Parse(plan.SettingsJson)!;
        var profile = JsonNode.Parse(plan.ProfileJson)!;
        switch (unsafeChange)
        {
            case "launch": settings["genshinStartConfig"]!["linkedStartEnabled"] = true; break;
            case "update": settings["scriptConfig"]!["autoUpdateBeforeCommandLineRun"] = true; break;
            case "craft": profile["TaskEnabledList"]!["genshin.craft_condensed_resin"] = true; break;
            case "close_game": profile["CompletionAction"] = "关闭游戏和软件"; break;
        }
        plan = plan with { SettingsJson = settings.ToJsonString(), ProfileJson = profile.ToJsonString() };
        Write(fixture.Root, plan);

        Assert.Throws<InvalidDataException>(() => BetterGiDailyAcceptance.ValidateDailyFiles(fixture.Root, Account, plan));
    }

    [Fact]
    public async Task RejectsChangingTheFrozenPlanBeforeStartup()
    {
        await using var fixture = new BetterGiFixture();
        Write(fixture.Root, Render(party: "另一队"));
        Assert.Throws<InvalidDataException>(() => BetterGiDailyAcceptance.ValidateDailyFiles(fixture.Root, Account, Render()));
    }

    [Fact]
    public async Task StockCraftProfileRequiresTheInventoryObservationPath()
    {
        await using var fixture = new BetterGiFixture();
        var plan = BetterGiAccountPlans.RenderSavedRun(Account,
            new(BetterGiAccountPlan.SourceProfileName(Account), ["合成树脂", "领取每日奖励"], "关闭软件", "枫丹", 0, "", "", false, "枫丹", ""), Client);
        Write(fixture.Root, plan);

        Assert.Throws<InvalidDataException>(() => BetterGiDailyAcceptance.ValidateDailyFiles(fixture.Root, Account, plan));
        Assert.Equal(Client, BetterGiDailyAcceptance.ValidateDailyFiles(fixture.Root, Account, plan, allowStockCraft: true));
    }

    [Fact]
    public async Task TreatsAnIncompleteStageAsInvalidData()
    {
        await using var fixture = new BetterGiFixture();
        var plan = Render() with { SettingsJson = "{}" };
        Write(fixture.Root, plan);

        Assert.Throws<InvalidDataException>(() => BetterGiDailyAcceptance.ValidateDailyFiles(fixture.Root, Account, plan));
    }

    [Fact]
    public void ObservedDiagnosticSuccessDoesNotPromoteTheFormalVerdict()
    {
        var result = new BetterGiDailyAcceptanceResult(Guid.NewGuid(), new(RunOutcome.Indeterminate, ReasonCodes.RealAdapterUnverified, []),
            CleanupState.Confirmed, Restored: true, EvidenceObserved: true, Cancelled: false, TimedOut: false, "report", null);

        Assert.True(result.DiagnosticPassed);
        Assert.Equal(RunOutcome.Indeterminate, result.Outcome);
        Assert.True(result.ScopedCompletionConfirmed);
        Assert.False((result with { Restored = false }).DiagnosticPassed);
        Assert.False((result with { Cleanup = CleanupState.NotRequired }).ScopedCompletionConfirmed);
        Assert.False((result with { Verdict = new(RunOutcome.Failed, ReasonCodes.ReportedFailure, []) }).ScopedCompletionConfirmed);
    }

    [Fact]
    public void WorldProofCannotBeReusedAcrossAccountsOrAReopenedClient()
    {
        var start = DateTimeOffset.UnixEpoch.AddYears(50);
        var window = new IntPtr(101);
        var proof = new GenshinVerifiedWorld(Account, window, 1001, start, null);

        Assert.True(BetterGiDailyAcceptance.MatchesVerifiedWorld(proof, Account, window, 1001, start));
        Assert.False(BetterGiDailyAcceptance.MatchesVerifiedWorld(proof, Guid.NewGuid(), window, 1001, start));
        Assert.False(BetterGiDailyAcceptance.MatchesVerifiedWorld(proof, Account, new IntPtr(102), 1001, start));
        Assert.False(BetterGiDailyAcceptance.MatchesVerifiedWorld(proof, Account, window, 1002, start));
        Assert.False(BetterGiDailyAcceptance.MatchesVerifiedWorld(proof, Account, window, 1001, start.AddSeconds(1)));
    }

    private static BetterGiPlanFiles Render(string party = "") => BetterGiAccountPlans.RenderDailyAcceptance(Account,
        new(BetterGiAccountPlan.SourceProfileName(Account), ["领取每日奖励"], "关闭软件", "枫丹", 0, "", "", false, "枫丹", party), Client);

    private static void Write(string root, BetterGiPlanFiles plan)
    {
        File.WriteAllText(Path.Combine(root, "User", "OneDragon", plan.ProfileName + ".json"), plan.ProfileJson);
        File.WriteAllText(Path.Combine(root, "User", "config.json"), plan.SettingsJson);
    }
}
