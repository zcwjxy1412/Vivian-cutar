using VIvian.Infrastructure.Engines;
using VIvian.Infrastructure.Engines.BetterGi;
using Xunit;

namespace VIvian.Tests.Engines.BetterGi;

public sealed class BetterGiProfileOverrideTests
{
    [Fact]
    public async Task ApplyReadsBackAndRestoreRecoversExactOriginalBytes()
    {
        await using var f = new BetterGiFixture();
        var original = File.ReadAllBytes(ProfilePath(f));
        var overlay = new BetterGiProfileOverride(f.Root, "默认配置", f.Probe);
        var snapshot = overlay.Read();
        Assert.Equal("关闭软件", snapshot.CompletionAction);

        var applied = overlay.Apply(snapshot.Revision, "关闭游戏和软件");

        Assert.Equal("关闭游戏和软件", applied.CompletionAction);
        Assert.Equal(applied, overlay.Read());
        Assert.True(overlay.HasPending);
        Assert.Equal(RestoreState.Restored, overlay.Restore().State);
        Assert.Equal(original, File.ReadAllBytes(ProfilePath(f)));
        Assert.False(overlay.HasPending);
    }

    [Fact]
    public async Task RecoveryCanResumeAfterThePreviousProcessMovedTheProfile()
    {
        await using var f = new BetterGiFixture();
        var path = ProfilePath(f);
        var original = File.ReadAllBytes(path);
        var overlay = new BetterGiProfileOverride(f.Root, "默认配置", f.Probe);
        overlay.Apply(overlay.Read().Revision, "关闭游戏");
        File.Move(path, path + ".vivian-displaced", overwrite: false);

        Assert.Equal(RestoreState.Restored, overlay.Restore().State);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(overlay.HasPending);
    }

    [Fact]
    public async Task ANewUserEditBlocksRecoveryAndIsPreserved()
    {
        await using var f = new BetterGiFixture();
        var overlay = new BetterGiProfileOverride(f.Root, "默认配置", f.Probe);
        overlay.Apply(overlay.Read().Revision, "关闭游戏和软件");
        const string edited = """{ "Name": "默认配置", "CompletionAction": "关闭游戏", "PartyName": "其他队伍" }""";
        File.WriteAllText(ProfilePath(f), edited);

        Assert.Equal(RestoreState.Blocked, overlay.Restore().State);
        Assert.Equal(edited, File.ReadAllText(ProfilePath(f)));
        Assert.True(overlay.HasPending);
        Assert.Throws<InvalidOperationException>(() => overlay.Apply(overlay.Read().Revision, "关闭软件"));
    }

    [Fact]
    public async Task StaleProposalAndRunningEngineDoNotWriteAnything()
    {
        await using var f = new BetterGiFixture();
        var overlay = new BetterGiProfileOverride(f.Root, "默认配置", f.Probe);
        var snapshot = overlay.Read();
        const string edited = """{ "Name": "默认配置", "CompletionAction": "关闭软件", "PartyName": "新的" }""";
        File.WriteAllText(ProfilePath(f), edited);

        Assert.Throws<ConfigurationConflictException>(() => overlay.Apply(snapshot.Revision, "关闭游戏"));
        f.Probe.Running = 1;
        Assert.Throws<InvalidOperationException>(() => overlay.Apply(overlay.Read().Revision, "关闭游戏"));
        Assert.Equal(edited, File.ReadAllText(ProfilePath(f)));
        Assert.False(overlay.HasPending);
    }

    private static string ProfilePath(BetterGiFixture f) => Path.Combine(f.Root, "User", "OneDragon", "默认配置.json");
}
