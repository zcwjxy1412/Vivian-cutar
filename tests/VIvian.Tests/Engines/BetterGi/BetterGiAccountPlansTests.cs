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
    }
}
