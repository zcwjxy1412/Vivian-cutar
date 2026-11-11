using VIvian.Core;
using VIvian.Core.Execution;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Engines.OneDragon;
using Xunit;

namespace VIvian.Tests.Engines.OneDragon;

public sealed class OneDragonIntegrationTests
{
    [Fact]
    public async Task StoresOnlyTheBindingAndRejectsSharedInstanceOrIdentity()
    {
        await using var fixture = new OneDragonFixture();
        var db = new LocalDatabase(Path.Combine(fixture.Root, "accounts.db"));
        await db.InitializeAsync();
        var a = await db.AddAccountAsync(GameId.ZenlessZoneZero, "A", "国服", null);
        var b = await db.AddAccountAsync(GameId.ZenlessZoneZero, "B", "国服", null);
        var first = new OneDragonProfileBinding(fixture.Root, 1);
        await db.SaveOneDragonBindingAsync(a with { AutoRun = true }, first, "138****1111");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.SaveOneDragonBindingAsync(b, first, "139****2222"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.SaveOneDragonBindingAsync(b, first with { Instance = 2 }, "138****1111"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.SaveOneDragonBindingAsync(b, first with { Instance = 2 }, "13922222222"));
        Assert.Empty((await db.LoadAccountSettingsAsync(b.Id)).Values);
        Assert.Equal(first, OneDragonProfileBinding.FromSettings((await db.LoadAccountSettingsAsync(a.Id)).Values));
        Assert.True((await db.LoadAccountsAsync(GameId.ZenlessZoneZero)).Single(account => account.Id == a.Id).AutoRun);
    }

    [Fact]
    public async Task WritesDirectlyToOneSourceProfileAndRejectsAStaleRevision()
    {
        await using var fixture = new OneDragonFixture();
        var first = new OneDragonProfileBinding(fixture.Root, 1);
        var second = first with { Instance = 2 };
        FileAt("one_dragon.yml", """
            instance_list:
            - idx: 1
              name: A
              active: true
              active_in_od: true
            - idx: 2
              name: B
              active: false
              active_in_od: true
            """);
        const string group = """
            app_list:
            - app_id: coffee
              enabled: true
            - app_id: another_upstream_app
              enabled: true
            """;
        foreach (var instance in new[] { "01", "02" })
        {
            FileAt($"{instance}/game_account.yml", @"game_path: C:\Games\ZenlessZoneZero.exe");
            FileAt($"{instance}/one_dragon/_group.yml", group);
        }

        var snapshot = await OneDragonSourceProfile.ReadAsync(first);
        Assert.Equal(["coffee", "another_upstream_app"], snapshot.Tasks.Select(task => task.Id));
        await OneDragonSourceProfile.SaveTaskAsync(first, snapshot.Revision, "coffee", false);
        Assert.False((await OneDragonSourceProfile.ReadAsync(first)).Tasks[0].Enabled);
        Assert.True((await OneDragonSourceProfile.ReadAsync(second)).Tasks[0].Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OneDragonSourceProfile.SaveTaskAsync(first, snapshot.Revision, "coffee", true));

        void FileAt(string file, string content)
        {
            var path = Path.Combine(fixture.Root, "config", file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
    }

    [Theory]
    [InlineData("start|123|2", "success", true, RunOutcome.Success)]
    [InlineData("start|999|2", "success", true, RunOutcome.Indeterminate)]
    [InlineData("start|123|1", "success", true, RunOutcome.Indeterminate)]
    [InlineData("start|123|2", "success", false, RunOutcome.Indeterminate)]
    [InlineData("start|123|2", "failed", true, RunOutcome.Failed)]
    [InlineData("start|123|2", "incomplete", true, RunOutcome.Indeterminate)]
    public void CurrentRunRequiresOwnedWorkerInstanceAndEachConfiguredTask(string start, string end,
        bool reportTasks, RunOutcome expected)
    {
        var id = Guid.NewGuid();
        var evidence = new OneDragonHandoffEvidence(id, 123, new(456, 2, ["coffee", "email"], "138****1111"));
        evidence.Read($"VIVIAN-ZZZ|{id:D}|{start}");
        evidence.Read($"VIVIAN-ZZZ|{id:D}|entered");
        evidence.Read($"VIVIAN-ZZZ|{Guid.NewGuid():D}|task|coffee|failed");
        if (reportTasks)
        {
            evidence.Read($"VIVIAN-ZZZ|{id:D}|task|coffee|success");
            evidence.Read($"VIVIAN-ZZZ|{id:D}|task|email|success");
        }
        evidence.Read($"VIVIAN-ZZZ|{id:D}|end|{end}");

        Assert.Equal(expected, evidence.Verdict(0, true, DateTimeOffset.UtcNow).Outcome);
        Assert.Equal(RunOutcome.Indeterminate, evidence.Verdict(0, false, DateTimeOffset.UtcNow).Outcome);
    }
}
