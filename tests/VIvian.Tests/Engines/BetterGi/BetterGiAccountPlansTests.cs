using System.Text.Json;
using VIvian.Infrastructure.Engines.BetterGi;
using Xunit;

namespace VIvian.Tests.Engines.BetterGi;

public sealed class BetterGiAccountPlansTests
{
    private const string Client = @"E:\Genshin Impact Game\YuanShen.exe";

    [Fact]
    public void NewAccountHasNoAutomaticTasks()
    {
        var plan = BetterGiAccountPlans.Compile(new Dictionary<string, string>());

        Assert.Empty(plan.Tasks);
        Assert.Throws<InvalidOperationException>(() => plan.Render(Guid.NewGuid(), Client));
    }

    [Fact]
    public void AccountsProduceSeparateNamedProfilesAndFrozenSettings()
    {
        var settings = new Dictionary<string, string>
        {
            ["dailyRewards"] = "true",
            ["AdventurersGuildCountry"] = "璃月",
            ["DailyRewardPartyName"] = "每日队伍",
        };
        var a = BetterGiAccountPlans.Compile(settings);
        settings["AdventurersGuildCountry"] = "蒙德";
        var b = BetterGiAccountPlans.Compile(settings);
        var first = a.Render(Guid.NewGuid(), Client);
        var second = b.Render(Guid.NewGuid(), Client);

        Assert.NotEqual(first.ProfileName, second.ProfileName);
        using var firstProfile = JsonDocument.Parse(first.ProfileJson);
        using var secondProfile = JsonDocument.Parse(second.ProfileJson);
        Assert.Equal("璃月", firstProfile.RootElement.GetProperty("AdventurersGuildCountry").GetString());
        Assert.Equal("蒙德", secondProfile.RootElement.GetProperty("AdventurersGuildCountry").GetString());
        Assert.Equal("每日队伍", firstProfile.RootElement.GetProperty("DailyRewardPartyName").GetString());
        Assert.Equal(new[] { "genshin.daily_rewards" }, a.Tasks.Select(t => t.Id));
    }

    [Fact]
    public async Task RenderedPlanRoundTripsThroughTheAdapterConfigurationReader()
    {
        await using var fixture = new BetterGiFixture();
        var plan = BetterGiAccountPlans.Compile(new Dictionary<string, string>
        {
            ["dailyRewards"] = "true",
            ["craftCondensedResin"] = "true",
            ["autoDomain"] = "true",
            ["CraftingBenchCountry"] = "蒙德",
            ["MinResinToKeep"] = "40",
            ["DomainName"] = "虹灵的净土",
            ["PartyName"] = "秘境队",
            ["autoDomainConfig.originalResin20UseCount"] = "2",
            ["autoDomainConfig.condensedResinUseCount"] = "1",
        });
        var files = plan.Render(Guid.NewGuid(), Client);
        var user = Path.Combine(fixture.Root, "User");
        File.WriteAllText(Path.Combine(user, "OneDragon", files.ProfileName + ".json"), files.ProfileJson);
        File.WriteAllText(Path.Combine(user, "config.json"), files.SettingsJson);

        var oneDragon = await BetterGiConfiguration.InspectOneDragonAsync(fixture.Root);
        var domain = await BetterGiConfiguration.InspectDomainResinAsync(fixture.Root);
        Assert.Equal(files.ProfileName, oneDragon.Name);
        Assert.Equal(new[] { "合成树脂", "自动秘境", "领取每日奖励" }, oneDragon.EnabledTasks);
        Assert.Equal("关闭软件", oneDragon.CompletionAction);
        Assert.Equal("蒙德", oneDragon.CraftingBenchCountry);
        Assert.Equal(40, oneDragon.MinResinToKeep);
        Assert.Equal("秘境队", oneDragon.PartyName);
        Assert.Equal((2, 1, 0, 0), (domain.Original20, domain.Condensed, domain.Transient, domain.Fragile));
        Assert.True(domain.SpecifyResinUse);
        Assert.False(domain.AutoArtifactSalvage);

        using var settings = JsonDocument.Parse(files.SettingsJson);
        var launch = settings.RootElement.GetProperty("genshinStartConfig");
        foreach (var key in new[] { "linkedStartEnabled", "startGameWithCmd", "autoEnterGameEnabled", "autoDisableGenshinHdrEnabled" })
            Assert.False(launch.GetProperty(key).GetBoolean());
    }

    [Theory]
    [InlineData("领奖队")]
    [InlineData("当前队伍")]
    [InlineData("")]
    public async Task DailyAcceptanceCopiesOnlyTheBoundSourceRewardTask(string party)
    {
        await using var fixture = new BetterGiFixture();
        var id = Guid.NewGuid();
        var name = BetterGiAccountPlan.SourceProfileName(id);
        var user = Path.Combine(fixture.Root, "User");
        var profile = Path.Combine(user, "OneDragon", name + ".json");
        var original = JsonSerializer.Serialize(new
        {
            Name = name,
            TaskEnabledList = new Dictionary<string, bool> { ["合成树脂"] = true, ["自动秘境"] = true, ["领取每日奖励"] = true },
            CompletionAction = "无",
            AdventurersGuildCountry = "璃月",
            DailyRewardPartyName = party,
            DomainName = "虹灵的净土",
        });
        File.WriteAllText(profile, original);
        var source = await BetterGiConfiguration.InspectOneDragonAsync(fixture.Root, name);

        var isolated = BetterGiAccountPlans.RenderDailyAcceptance(id, source, Client);
        Assert.Equal(original, File.ReadAllText(profile));
        await using var isolatedFixture = new BetterGiFixture();
        var isolatedUser = Path.Combine(isolatedFixture.Root, "User");
        File.WriteAllText(Path.Combine(isolatedUser, "OneDragon", isolated.ProfileName + ".json"), isolated.ProfileJson);
        File.WriteAllText(Path.Combine(isolatedUser, "config.json"), isolated.SettingsJson);

        Assert.Equal(name, isolated.ProfileName);
        var effective = await BetterGiConfiguration.InspectOneDragonAsync(isolatedFixture.Root);
        Assert.Equal(["领取每日奖励"], effective.EnabledTasks);
        Assert.Equal("璃月", effective.AdventurersGuildCountry);
        Assert.Equal(party, effective.DailyRewardPartyName);
        Assert.Equal("关闭软件", effective.CompletionAction);
        var domain = await BetterGiConfiguration.InspectDomainResinAsync(isolatedFixture.Root);
        Assert.False(domain.SpecifyResinUse);
        Assert.Equal((0, 0, 0, 0, 0, 0), (domain.Original, domain.Original20, domain.Original40, domain.Condensed,
            domain.Transient, domain.Fragile));
        using var settings = JsonDocument.Parse(isolated.SettingsJson);
        var launch = settings.RootElement.GetProperty("genshinStartConfig");
        Assert.False(launch.GetProperty("linkedStartEnabled").GetBoolean());
        Assert.False(launch.GetProperty("startGameWithCmd").GetBoolean());
        Assert.False(launch.GetProperty("autoEnterGameEnabled").GetBoolean());
        var updates = settings.RootElement.GetProperty("scriptConfig");
        Assert.False(updates.GetProperty("autoUpdateSubscribedScripts").GetBoolean());
        Assert.False(updates.GetProperty("autoUpdateBeforeCommandLineRun").GetBoolean());
    }

    [Fact]
    public void DailyAcceptanceRejectsUnboundProfilesAndProfilesWithoutDailyRewards()
    {
        var id = Guid.NewGuid();
        var source = new BetterGiOneDragonPlan(BetterGiAccountPlan.SourceProfileName(id), ["合成树脂"], "无",
            "枫丹", 0, "", "", false, "璃月", "");

        var disabled = Assert.Throws<InvalidOperationException>(() =>
            BetterGiAccountPlans.RenderDailyAcceptance(id, source, Client));
        Assert.Contains("领取每日奖励", disabled.Message);
        Assert.Throws<InvalidOperationException>(() =>
            BetterGiAccountPlans.RenderDailyAcceptance(Guid.NewGuid(), source with { EnabledTasks = ["领取每日奖励"] }, Client));
    }

    [Theory]
    [InlineData("autoDomainConfig.autoArtifactSalvage", "true")]
    [InlineData("autoDomainConfig.fragileResinUseCount", "3")]
    [InlineData("autoDomainConfig.originalResin20UseCount", "-1")]
    [InlineData("AdventurersGuildCountry", "未知地区")]
    [InlineData("dailyRewards", "on")]
    public void UnknownOrUnsafeParametersAreRejected(string key, string value)
        => Assert.Throws<ArgumentException>(() => BetterGiAccountPlans.Compile(new Dictionary<string, string> { [key] = value }));

    [Fact]
    public void DomainRequiresAnExactNameAndAtLeastOneResinCount()
    {
        var settings = new Dictionary<string, string> { ["autoDomain"] = "true" };
        Assert.Throws<ArgumentException>(() => BetterGiAccountPlans.Compile(settings));
        settings["DomainName"] = "虹灵的净土";
        Assert.Throws<ArgumentException>(() => BetterGiAccountPlans.Compile(settings));
        settings["autoDomainConfig.originalResin40UseCount"] = "1";
        Assert.Single(BetterGiAccountPlans.Compile(settings).Tasks);
        settings["DomainName"] = "根据提升指南选择秘境";
        Assert.Throws<ArgumentException>(() => BetterGiAccountPlans.Compile(settings));
    }
}
