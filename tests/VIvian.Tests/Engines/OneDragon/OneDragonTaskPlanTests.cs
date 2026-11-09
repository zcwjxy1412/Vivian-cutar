using VIvian.Infrastructure.Engines.OneDragon;
using Xunit;

namespace VIvian.Tests.Engines.OneDragon;

public sealed class OneDragonTaskPlanTests
{
    [Fact]
    public async Task InCurrentOnlyModeAnExistingButInactiveIdCannotBeSelected()
    {
        await using var fixture = new OneDragonFixture();
        Write(fixture.Root, "one_dragon.yml", """
            instance_list:
            - idx: 1
              name: A
              active: true
              active_in_od: true
            - idx: 7
              name: B
              active: false
              active_in_od: true
            instance_run: 仅运行当前
            """);

        var list = await OneDragonInstanceCatalog.ReadAsync(fixture.Root);

        Assert.True(list.ContainsExactlyOne([1]));
        Assert.False(list.ContainsExactlyOne([7]));
    }

    [Fact]
    public async Task ReadsOnlyTheRequestedInstanceAndPreservesEnabledTaskOrder()
    {
        await using var fixture = new OneDragonFixture();
        SaveInstances(fixture.Root);
        Write(fixture.Root, "01/one_dragon/_group.yml", """
            app_list:
            - app_id: engagement_reward
              enabled: true
            """);
        Write(fixture.Root, "07/one_dragon/_group.yml", """
            app_list:
            - app_id: coffee
              enabled: true
            - app_id: retired_app
              enabled: false
            - app_id: engagement_reward
              enabled: true
            """);

        var planA = await OneDragonTaskPlans.ReadAsync(fixture.Root, 1);
        var planB = await OneDragonTaskPlans.ReadAsync(fixture.Root, 7);

        Assert.Equal(OneDragonTaskPlanStatus.Available, planA.Status);
        Assert.Equal(["engagement_reward"], planA.Tasks.Select(task => task.Id));
        Assert.Equal(OneDragonTaskPlanStatus.Available, planB.Status);
        Assert.Equal(["coffee", "engagement_reward"], planB.Tasks.Select(task => task.Id));
    }

    [Theory]
    [InlineData("app_list: []", OneDragonTaskPlanStatus.Empty)]
    [InlineData("app_list:\n- app_id: scratch_card\n  enabled: true", OneDragonTaskPlanStatus.UnsupportedTask)]
    [InlineData("app_list:\n- app_id: coffee\n  enabled: 'false'", OneDragonTaskPlanStatus.Invalid)]
    [InlineData("app_list:\n- app_id: coffee\n  enabled: false\n- app_id: coffee\n  enabled: true", OneDragonTaskPlanStatus.Invalid)]
    public async Task RefusesAmbiguousOrUnsupportedPlans(string yaml, OneDragonTaskPlanStatus status)
    {
        await using var fixture = new OneDragonFixture();
        SaveInstances(fixture.Root);
        Write(fixture.Root, "01/one_dragon/_group.yml", yaml);

        var plan = await OneDragonTaskPlans.ReadAsync(fixture.Root, 1);

        Assert.Equal(status, plan.Status);
        Assert.Empty(plan.Tasks);
    }

    [Fact]
    public async Task MissingCurrentGroupDoesNotReadLegacyOrAnotherInstance()
    {
        await using var fixture = new OneDragonFixture();
        SaveInstances(fixture.Root);
        Write(fixture.Root, "01/one_dragon/_group.yml", "app_list: []");
        Write(fixture.Root, "07/one_dragon_app.yml", "app_run_list: [engagement_reward]");

        Assert.Equal(OneDragonTaskPlanStatus.Missing, (await OneDragonTaskPlans.ReadAsync(fixture.Root, 7)).Status);
        Assert.Equal(OneDragonTaskPlanStatus.InstanceUnavailable, (await OneDragonTaskPlans.ReadAsync(fixture.Root, 2)).Status);
    }

    private static void SaveInstances(string root) => Write(root, "one_dragon.yml", """
        instance_list:
        - idx: 1
          name: A
          active: true
          active_in_od: true
        - idx: 7
          name: B
          active: false
          active_in_od: true
        """);

    private static void Write(string root, string file, string contents)
    {
        var path = Path.Combine(root, "config", file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }
}
