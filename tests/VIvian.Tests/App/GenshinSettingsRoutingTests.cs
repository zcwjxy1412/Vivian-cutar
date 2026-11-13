using VIvian.App;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Engines.BetterGi;
using Xunit;

namespace VIvian.Tests.App;

public sealed class GenshinSettingsRoutingTests
{
    [Fact]
    public void DetailedSettingsKeepSavedValuesAndReadBackTheBoundSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "vivian-routing-" + Guid.NewGuid().ToString("N"));
        var database = new LocalDatabase(Path.Combine(root, "test.db"));
        var vm = new GenshinConfigurationViewModel(database, new GenshinAccountConfigurationProvider(database),
            () => true, root, () => true, () => "", action => action());
        foreach (var field in BetterGiAccountPlans.Parameters)
            vm.Parameters.Add(new(new(field, field.DefaultValue, "已保存")));

        var party = vm.Parameters.Single(row => row.Path == "PartyName");
        var original = party.Current;
        var sourceParty = new ParameterRow(new(BetterGiAccountPlans.Parameters.Single(p => p.Path == "PartyName"),
            "源项目队伍", "已读回"));
        vm.Source.ProfileRows.Add(sourceParty);

        Assert.DoesNotContain(vm.VisiblePlanParameters, row => row.Path is "PartyName" or "DailyRewardPartyName");
        Assert.Equal(vm.Parameters.Count - 2, vm.VisiblePlanParameters.Count());
        Assert.Equal(2, vm.DetailedPlanParameters.Count());
        Assert.Same(sourceParty, vm.DetailedPlanParameters.Single(row => row.Path == "PartyName"));
        Assert.Equal(original, party.Current);
        Assert.False(party.IsChanged);
        vm.VisiblePlanParameters.Single(row => row.Path == "dailyRewards").BoolValue = true;
        Assert.Equal("源项目队伍", sourceParty.Current);
        Assert.False(sourceParty.IsChanged);

        vm.Source.ProfileRows.Clear();
        Assert.Same(party, vm.DetailedPlanParameters.Single(row => row.Path == "PartyName"));
    }
}
