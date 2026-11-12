using VIvian.Core;
using VIvian.Core.Execution;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Engines.OneDragon;
using Xunit;

namespace VIvian.Tests.Engines.OneDragon;

public sealed class OneDragonIntegrationTests
{
    [Theory]
    [InlineData("139", 2)]
    [InlineData("137", 1)]
    public async Task ExpandedScreenshotRereadsOnlyIncompletePhoneRows(string rereadPrefix, int expected)
    {
        var frame = new VIvian.Infrastructure.Accounts.CapturedFrame(new byte[1920 * 1080 * 4], 1920, 1080, "");
        VIvian.Core.Accounts.RecognizedText[] lines =
        [
            new("138****11", 739, 494, 154, 22),
            new("138****11", 739, 608, 154, 22),
            new("139", 739, 733, 47, 22),
            new("1天前", 760, 775, 50, 19),
        ];
        var calls = 0;
        var rows = await ZenlessAccountImport.ReadExpandedRowsAsync(frame, lines, default, (crop, _) =>
        {
            calls++;
            Assert.True(crop.Width < frame.Width);
            Assert.True(crop.Height < 100);
            return Task.FromResult(new VIvian.Infrastructure.Accounts.OcrReadResult(true, null,
                [new(rereadPrefix, 18, 26, 94, 44), new("22", 260, 26, 62, 44)]));
        });
        Assert.Equal(1, calls);
        var accounts = ZenlessAccountImport.Recognize(frame.Width, frame.Height, lines, false, true, rows);
        Assert.Equal(expected, accounts.Count);
        Assert.Equal("138****11", accounts[0]);
        if (expected == 2)
        {
            Assert.Equal("139****22", accounts[1]);
            Assert.InRange(rows[1].CenterY, 744, 746);
        }
    }

    [Theory]
    [InlineData("进 入 游 戏", true)]
    [InlineData("入 游 戏", true)]
    [InlineData("点 击 进 入 游 戏", false)]
    [InlineData("登 录 其 他 账 号", false)]
    public async Task ConfirmationReadsOnlyTheLoginButtonCrop(string text, bool expected)
    {
        var frame = new VIvian.Infrastructure.Accounts.CapturedFrame(new byte[1920 * 1080 * 4], 1920, 1080, "");
        var recognized = await ZenlessAccountImport.ConfirmButtonAsync(frame, [], default, (crop, _) =>
        {
            Assert.Equal(180, crop.Width);
            Assert.Equal(87, crop.Height);
            return Task.FromResult(new VIvian.Infrastructure.Accounts.OcrReadResult(true, null, [new(text, 2, 2, 70, 10)]));
        });
        Assert.Equal(expected, recognized);
    }

    [Fact]
    public async Task PrepareRepairsBoundInstanceAndCompletesSecondInterruptedImport()
    {
        await using var fixture = new OneDragonFixture();
        var root = fixture.Root;
        var db = new LocalDatabase(Path.Combine(root, "accounts.db"));
        await db.InitializeAsync();
        Directory.CreateDirectory(Path.Combine(root, "config", "01"));
        await File.WriteAllTextAsync(Path.Combine(root, "config", "one_dragon.yml"),
            "instance_list:\n- idx: 1\n  name: '01'\n  active: true\n  active_in_od: true\n");
        var ids = await db.RegisterZenlessScreenshotAsync(["138****11", "139****22"], default);
        var accounts = await db.LoadAccountsAsync(GameId.ZenlessZoneZero);
        var first = accounts.Single(account => account.Id == ids[0]);
        var second = accounts.Single(account => account.Id == ids[1]);
        var firstBinding = new OneDragonProfileBinding(root, 1);
        await db.SaveOneDragonBindingAsync(first with { AutoRun = true }, firstBinding, "138****11");
        var gamePath = @"C:\Games\ZenlessZoneZero.exe";

        await ZenlessAccountImport.PrepareRegisteredAsync(db, first, root, gamePath, "138****11", default);
        await ZenlessAccountImport.PrepareRegisteredAsync(db, second, root, gamePath, "139****22", default);
        var snapshot = await OneDragonSourceProfile.ReadAsync(firstBinding);
        await OneDragonSourceProfile.SaveTasksAsync(firstBinding, snapshot.Revision,
            new Dictionary<string, bool> { ["engagement_reward"] = false, ["email"] = true });
        await ZenlessAccountImport.PrepareRegisteredAsync(db, first, root, gamePath, "138****11", default);
        var after = await OneDragonSourceProfile.ReadAsync(firstBinding);
        Assert.Equal("email", Assert.Single(after.Tasks, task => task.Enabled).Id);
        Assert.Equal(firstBinding, OneDragonProfileBinding.FromSettings((await db.LoadAccountSettingsAsync(first.Id)).Values));
        var secondBinding = Assert.IsType<OneDragonProfileBinding>(
            OneDragonProfileBinding.FromSettings((await db.LoadAccountSettingsAsync(second.Id)).Values));
        Assert.NotEqual(firstBinding.Instance, secondBinding.Instance);
        Assert.Equal("engagement_reward", Assert.Single((await OneDragonSourceProfile.ReadAsync(secondBinding)).Tasks,
            task => task.Enabled).Id);
        Assert.True((await db.LoadAccountsAsync(GameId.ZenlessZoneZero)).Single(row => row.Id == first.Id).AutoRun);
        await db.DeleteAccountAsync(second.Id);
        Assert.DoesNotContain(await db.LoadAccountsAsync(GameId.ZenlessZoneZero), row => row.Id == second.Id);
        Assert.NotEmpty((await OneDragonSourceProfile.ReadAsync(secondBinding)).Tasks);
    }

    [Fact]
    public void LoginScreenshotRequiresTheZenlessCardAndKeepsDistinctSavedAccountsInOrder()
    {
        VIvian.Core.Accounts.RecognizedText[] lines =
        [
            new("138****1111", 700, 480, 150, 20),
            new("138****1111", 700, 554, 150, 20),
            new("139****2222", 700, 615, 150, 20),
            new("进入游戏", 912, 613, 85, 20),
        ];
        Assert.Empty(ZenlessAccountImport.Recognize(1920, 1080, lines, false, false));
        Assert.Equal("138****1111", Assert.Single(ZenlessAccountImport.Recognize(1920, 1080, lines, true, false)));
        Assert.Equal(["138****1111", "139****2222"], ZenlessAccountImport.Recognize(1920, 1080, lines, false, true));
        Assert.Empty(ZenlessAccountImport.Recognize(1920, 1080,
            [.. lines, new("139****2222", 700, 675, 150, 20)], false, true));
    }

    [Fact]
    public async Task ScreenshotRegistrationReusesAccountAndCreatesAnIndependentNativeProfile()
    {
        await using var fixture = new OneDragonFixture();
        var db = new LocalDatabase(Path.Combine(fixture.Root, "accounts.db"));
        await db.InitializeAsync();
        var root = fixture.Root;
        Directory.CreateDirectory(Path.Combine(root, "config"));
        var source = Path.Combine(root, "config", "one_dragon.yml");
        var original = "instance_list:\n- idx: 1\n  name: '01'\n  active: true\n  active_in_od: true\n";
        await File.WriteAllTextAsync(source, original);
        var first = await db.RegisterZenlessScreenshotAsync(["138****1111", "139****2222"], default);
        Assert.All(first, id => Assert.NotEqual(Guid.Empty, id));
        var a = await OneDragonImportedProfile.EnsureAsync(root, first[0], @"C:\Games\ZenlessZoneZero.exe", default);
        var b = await OneDragonImportedProfile.EnsureAsync(root, first[1], @"C:\Games\ZenlessZoneZero.exe", default);
        Assert.Equal(2, a.Instance);
        Assert.Equal(3, b.Instance);
        Assert.StartsWith(original, await File.ReadAllTextAsync(source));
        Assert.Equal(a, await OneDragonImportedProfile.EnsureAsync(root, first[0], @"C:\Games\ZenlessZoneZero.exe", default));
        var accounts = (await db.LoadAccountsAsync(GameId.ZenlessZoneZero)).Where(account => !account.IsLegacyPlaceholder).ToArray();
        await db.SaveOneDragonBindingAsync(accounts.Single(account => account.Id == first[0]), a, "138****1111");
        await db.SaveOneDragonBindingAsync(accounts.Single(account => account.Id == first[1]), b, "139****2222");
        Assert.Equal(first, await db.RegisterZenlessScreenshotAsync(["138****1111", "139****2222"], default));
        Assert.Equal(a, OneDragonProfileBinding.FromSettings((await db.LoadAccountSettingsAsync(first[0])).Values));
        Assert.Equal(b, OneDragonProfileBinding.FromSettings((await db.LoadAccountSettingsAsync(first[1])).Values));
        Assert.All(accounts, account => Assert.False(account.AutoRun));
        var profile = await OneDragonSourceProfile.ReadAsync(a);
        Assert.Equal("engagement_reward", Assert.Single(profile.Tasks, task => task.Enabled).Id);
        await OneDragonSourceProfile.SaveTaskAsync(a, profile.Revision, "engagement_reward", false);
        Assert.True((await OneDragonSourceProfile.ReadAsync(b)).Tasks.First().Enabled);
    }

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

[Collection(VIvian.Tests.Processes.ProcessCollection.Name)]
public sealed class ZenlessProcessAttributionTests
{
    [VIvian.Tests.Processes.WindowsFact]
    public async Task ProcessAttributionUsesTheExactExecutableAndLeavesUnownedProcessesAlone()
    {
        var fake = VIvian.Tests.Processes.ProcessTestSupport.FakeEngine;
        using var started = VIvian.Infrastructure.Processes.WindowsProcessSupervisor.StartInheritedProcess(
            fake, "--scenario sleep-forever", AppContext.BaseDirectory);
        try
        {
            Assert.NotNull(ZenlessLoginDriver.VerifyStartedGame(started, fake));
            Assert.Null(ZenlessLoginDriver.VerifyStartedGame(started, @"C:\Other\ZenlessZoneZero.exe"));
            using var driver = new ZenlessLoginDriver(@"C:\Other\ZenlessZoneZero.exe", "", () =>
                throw new InvalidOperationException("Cleanup must not mark a new launch."));
            Assert.True((await driver.StopGameAsync(default)).Ok);
            Assert.False(started.HasExited);
        }
        finally
        {
            Assert.True(VIvian.Infrastructure.Processes.WindowsProcessSupervisor.TerminateOwnedProcess(started));
        }
    }
}
