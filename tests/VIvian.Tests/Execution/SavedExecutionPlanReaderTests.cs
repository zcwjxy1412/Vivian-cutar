using VIvian.Core;
using VIvian.Core.Execution;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Engines;
using VIvian.Infrastructure.Engines.BetterGi;
using VIvian.Infrastructure.Engines.OneDragon;
using Xunit;

namespace VIvian.Tests.Execution;

public sealed class SavedExecutionPlanReaderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("furina-plan-reader-").FullName;

    [Fact]
    public async Task ReadsRealSavedTargetAndInvalidatesOnConfigurationLoginAndPathChange()
    {
        var db = new LocalDatabase(Path.Combine(_root, "data.db")); await db.InitializeAsync();
        var account = await db.AddAccountAsync(GameId.Genshin, "测试目标", "", null);
        account = await db.SaveAccountAsync(account with { AutoRun = true });
        await db.SaveLoginProfileAsync(account.Id, "130****00", "130****00", 0, .9, null, "fixture");
        var provider = new GenshinAccountConfigurationProvider(db);
        var target = new VIvian.Core.Engines.AccountConfigurationTarget(GameId.Genshin, account.Id);
        var view = await provider.ReadAsync(target, CancellationToken.None);
        var proposal = await provider.ProposeAsync(target, view.Revision,
            new Dictionary<string, string> { ["dailyRewards"] = "true" }, CancellationToken.None);
        await provider.ApplyAsync(proposal, () => true, CancellationToken.None);
        var reader = new SavedExecutionPlanReader(db, new VersionedConfigurationControl());
        var request = new ExecutionPlanRequestItem(GameId.Genshin, account.Id);
        var before = await reader.ReadAsync(request, 0, _root, CancellationToken.None);
        Assert.Equal(account.Name, before.AccountName);
        Assert.Contains("领取每日奖励", before.ResourceSummary);
        Assert.DoesNotContain("130", before.ResourceSummary);
        Assert.Equal(before, await reader.ReadAsync(request, 0, _root, CancellationToken.None));
        Assert.NotEqual(before.ConfigurationRevision, (await reader.ReadAsync(request, 0, Path.Combine(_root, "other"), CancellationToken.None)).ConfigurationRevision);
        view = await provider.ReadAsync(target, CancellationToken.None);
        proposal = await provider.ProposeAsync(target, view.Revision,
            new Dictionary<string, string> { ["MinResinToKeep"] = "20" }, CancellationToken.None);
        await provider.ApplyAsync(proposal, () => true, CancellationToken.None);
        Assert.NotEqual(before.ConfigurationRevision, (await reader.ReadAsync(request, 0, _root, CancellationToken.None)).ConfigurationRevision);
        var changed = await reader.ReadAsync(request, 0, _root, CancellationToken.None);
        await db.SaveLoginProfileAsync(account.Id, "131****01", "131****01", 0, .9, null, "changed");
        Assert.NotEqual(changed.ConfigurationRevision, (await reader.ReadAsync(request, 0, _root, CancellationToken.None)).ConfigurationRevision);
        account = (await db.LoadAccountsAsync(GameId.Genshin)).Single(x => x.Id == account.Id);
        await db.SaveAccountAsync(account with { AutoRun = false });
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(request, 0, _root, CancellationToken.None));
    }

    public void Dispose() => TestDirectories.DeleteWithRetry(_root);

    [Fact]
    public async Task ZenlessFreezesResourceFilesAndLegacyFallbackWithoutLeakingContents()
    {
        var db = new LocalDatabase(Path.Combine(_root, "zzz.db")); await db.InitializeAsync();
        var account = await db.AddAccountAsync(GameId.ZenlessZoneZero, "测试绝区零", "", null);
        var binding = new OneDragonProfileBinding(_root, 1);
        await db.SaveOneDragonBindingAsync(account with { AutoRun = true }, binding, "138****1234");
        var instance = Path.Combine(_root, "config", "01"); Directory.CreateDirectory(instance);
        await File.WriteAllTextAsync(Path.Combine(_root, "config", "one_dragon.yml"),
            "instance_list:\n- idx: 1\n  name: '01'\n  active: true\n  active_in_od: true\n");
        await OneDragonSourceProfile.PrepareMissingAsync(binding, @"C:\Games\ZenlessZoneZero.exe", CancellationToken.None);
        var legacy = Path.Combine(instance, "charge_plan.yml");
        await File.WriteAllTextAsync(legacy, "plan_list: []\n# legacy-resource-sentinel");
        var reader = new SavedExecutionPlanReader(db, new VersionedConfigurationControl());
        var request = new ExecutionPlanRequestItem(GameId.ZenlessZoneZero, account.Id);
        var first = await reader.ReadAsync(request, 0, _root, CancellationToken.None);
        Assert.DoesNotContain("sentinel", first.ResourceSummary);
        await File.AppendAllTextAsync(legacy, "\nloop: true\n");
        var second = await reader.ReadAsync(request, 0, _root, CancellationToken.None);
        Assert.NotEqual(first.ConfigurationRevision, second.ConfigurationRevision);
        var grouped = Path.Combine(instance, "one_dragon", "charge_plan.yml");
        await File.WriteAllTextAsync(grouped, "plan_list: []\nloop: false\n");
        var third = await reader.ReadAsync(request, 0, _root, CancellationToken.None);
        Assert.NotEqual(second.ConfigurationRevision, third.ConfigurationRevision);
        await File.AppendAllTextAsync(legacy, "\n# ignored once grouped file exists\n");
        Assert.Equal(third, await reader.ReadAsync(request, 0, _root, CancellationToken.None));
    }
}
