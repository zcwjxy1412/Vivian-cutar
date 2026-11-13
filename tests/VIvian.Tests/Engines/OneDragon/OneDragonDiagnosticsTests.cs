using VIvian.Core.Engines;
using VIvian.Infrastructure.Engines.OneDragon;
using Xunit;

namespace VIvian.Tests.Engines.OneDragon;

public sealed class OneDragonDiagnosticsTests
{
    [Theory]
    [InlineData(@"E:\VIvianEngines\OneDragon-v2.6.0", true)]
    [InlineData(@"E:\VIvianEngines\OneDragon-v2.6.0.", false)]
    [InlineData(@"E:\auto agent game\OneDragon-v2.6.0", false)]
    [InlineData(@"E:\工具\OneDragon-v2.6.0", false)]
    [InlineData(@"\\host\share\OneDragon", false)]
    public void VersionedInstallPathKeepsOtherRestrictions(string path, bool allowed) =>
        Assert.Equal(allowed, OneDragonInstallationInspector.IsSupportedInstallPath(path));

    [Fact]
    public async Task BundledLayoutDoesNotImplyFirstCheckoutOrExplicitUpdateSetting()
    {
        await using var fixture = new OneDragonFixture();
        var result = await OneDragonInstallationInspector.InspectAsync(fixture.Root);
        Assert.Contains(result.Checks, c => c.Id == "od:checkout" && c.State == CheckState.Failed);
        Assert.Contains(result.Checks, c => c.Id == "od:update" && c.State == CheckState.Failed);
        Assert.False(result.RealExecutionAllowed);
    }

    [Fact]
    public async Task ReadsActualInstanceIdsInFileOrderWithoutExposingNamesOrInventingDefaults()
    {
        await using var fixture = new OneDragonFixture();
        WriteConfig(fixture.Root, """
            instance_list:
            - idx: 7
              name: 私有名称
              active: true
              active_in_od: false
            - idx: 3
              name: '另一个名称'
              active: false
              active_in_od: true
              force_login_before_run: true
            instance_run: 全部实例
            """);

        var result = await OneDragonInstanceCatalog.ReadAsync(fixture.Root);
        Assert.Equal(OneDragonInstanceStatus.Available, result.Status);
        Assert.Equal([7, 3], result.Instances.Select(instance => instance.Index));
        Assert.False(result.Instances[0].IncludedByDefault);
        Assert.True(result.Instances[1].ForceLogin);
        Assert.True(result.ContainsExactlyOne([7]));
        Assert.False(result.ContainsExactlyOne([1]));
        Assert.False(result.ContainsExactlyOne([7, 3]));
        Assert.False(result.ContainsExactlyOne([]));
        Assert.DoesNotContain("私有名称", result.ToString());
    }

    [Theory]
    [InlineData("instance_list: []", OneDragonInstanceStatus.Empty)]
    [InlineData("instance_list: [{idx: 1}]", OneDragonInstanceStatus.Invalid)]
    [InlineData("instance_list:\n- idx: 1\n  idx: 2\n  name: x\n  active: true\n  active_in_od: true", OneDragonInstanceStatus.Invalid)]
    [InlineData("instance_list:\n- idx: 01\n  name: x\n  active: true\n  active_in_od: true", OneDragonInstanceStatus.Invalid)]
    [InlineData("instance_list:\n- idx: 1\n  name: x\n  active: 'true'\n  active_in_od: true", OneDragonInstanceStatus.Invalid)]
    [InlineData("instance_list: []\ninstance_list: []", OneDragonInstanceStatus.Invalid)]
    [InlineData("instance_list: []\n---\ninstance_list: []", OneDragonInstanceStatus.Invalid)]
    public async Task AmbiguousOrEmptyCatalogDoesNotSelectAnInstance(string yaml, OneDragonInstanceStatus status)
    {
        await using var fixture = new OneDragonFixture();
        WriteConfig(fixture.Root, yaml);

        var result = await OneDragonInstanceCatalog.ReadAsync(fixture.Root);

        Assert.Equal(status, result.Status);
        Assert.Empty(result.Instances);
    }

    [Fact]
    public async Task MissingCatalogDoesNotCreateDirectoriesOrFallbackToSamples()
    {
        await using var fixture = new OneDragonFixture();
        Assert.Equal(OneDragonInstanceStatus.Missing, (await OneDragonInstanceCatalog.ReadAsync(fixture.Root)).Status);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "config")));
    }

    private static void WriteConfig(string root, string yaml)
    {
        var config = Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(Path.Combine(config.FullName, "one_dragon.yml"), yaml);
    }
}
