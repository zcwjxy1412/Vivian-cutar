using System.Text.Json.Nodes;
using VIvian.Infrastructure.Engines;
using VIvian.Infrastructure.Engines.BetterGi;
using Xunit;
namespace VIvian.Tests.Engines.BetterGi;

public sealed class BetterGiDomainOverrideTests
{
    [Fact]
    public async Task SparseAccountBudgetCannotInheritOtherSupportedCountsFromSharedSettings()
    {
        await using var f = new BetterGiFixture();
        var path = Path.Combine(f.Root, "User", "config.json");
        File.WriteAllText(path, """{"autoDomainConfig":{"originalResin20UseCount":8,"originalResin40UseCount":6}}""");
        var overlay = new BetterGiDomainOverride(f.Root, f.Probe);
        overlay.Apply(new Dictionary<string,string> { [BetterGiAccountPlans.Condensed] = "3" });
        var native = JsonNode.Parse(File.ReadAllText(path))!["autoDomainConfig"]!;
        Assert.Equal(0, native["originalResin20UseCount"]!.GetValue<int>());
        Assert.Equal(0, native["originalResin40UseCount"]!.GetValue<int>());
        Assert.Equal(3, native["condensedResinUseCount"]!.GetValue<int>());
        Assert.Equal(RestoreState.Restored, overlay.Restore().State);
    }

    [Fact]
    public async Task ConflictingUserEditAndRunningEngineBlockRestoration()
    {
        await using var f = new BetterGiFixture();
        var overlay = new BetterGiDomainOverride(f.Root, f.Probe);
        overlay.Apply(new Dictionary<string,string> { [BetterGiAccountPlans.Condensed] = "3" });
        f.Probe.Running = 1;
        Assert.Equal(RestoreState.Blocked, overlay.Restore().State);
        f.Probe.Running = 0;
        var path = Path.Combine(f.Root, "User", "config.json");
        var current = JsonNode.Parse(File.ReadAllText(path))!;
        current["autoDomainConfig"]!["condensedResinUseCount"] = 7;
        File.WriteAllText(path, current.ToJsonString());
        Assert.Equal(RestoreState.Blocked, overlay.Restore().State);
        Assert.Equal(7, JsonNode.Parse(File.ReadAllText(path))!["autoDomainConfig"]!["condensedResinUseCount"]!.GetValue<int>());
        Assert.True(overlay.HasPending);
    }

    [Fact]
    public async Task RecoveryIsIdempotentIfProcessExitsBeforeJournalDeletion()
    {
        await using var f = new BetterGiFixture();
        var overlay = new BetterGiDomainOverride(f.Root, f.Probe);
        overlay.Apply(new Dictionary<string,string> { [BetterGiAccountPlans.Condensed] = "3" });
        var journal = Path.Combine(f.Root, "User", "config.json.vivian-domain-recovery");
        var bytes = File.ReadAllBytes(journal);
        Assert.Equal(RestoreState.Restored, overlay.Restore().State);
        File.WriteAllBytes(journal, bytes);
        Assert.Equal(RestoreState.Restored, overlay.Restore().State);
        Assert.False(overlay.HasPending);
    }

    [Fact]
    public async Task AccountCountsReachNativeConfigAndRestoreWithoutLosingNativeUpdates()
    {
        await using var f = new BetterGiFixture();
        var path = Path.Combine(f.Root, "User", "config.json");
        File.WriteAllText(path, """{"autoDomainConfig":{"originalResin20UseCount":8},"other":"before"}""");
        var overlay = new BetterGiDomainOverride(f.Root, f.Probe);
        overlay.Apply(new Dictionary<string,string> { [BetterGiAccountPlans.Original20] = "2", [BetterGiAccountPlans.Condensed] = "3" });
        var native = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(2, native["autoDomainConfig"]!["originalResin20UseCount"]!.GetValue<int>());
        Assert.True(native["autoDomainConfig"]!["specifyResinUse"]!.GetValue<bool>());
        native["other"] = "native update";
        File.WriteAllText(path, native.ToJsonString());
        Assert.Equal(RestoreState.Restored, overlay.Restore().State);
        var restored = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(8, restored["autoDomainConfig"]!["originalResin20UseCount"]!.GetValue<int>());
        Assert.Null(restored["autoDomainConfig"]!["condensedResinUseCount"]);
        Assert.Equal("native update", restored["other"]!.GetValue<string>());
    }
}
