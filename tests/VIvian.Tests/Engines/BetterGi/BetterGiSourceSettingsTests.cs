using System.Text.Json.Nodes;
using VIvian.Infrastructure.Engines.BetterGi;
using VIvian.Infrastructure.Processes;
using Xunit;

namespace VIvian.Tests.Engines.BetterGi;

public sealed class BetterGiSourceSettingsTests
{
    [Fact]
    public async Task LegacyProfileRoundTripsExistingFieldsWithoutTouchingOtherFiles()
    {
        await using var fixture = new BetterGiFixture();
        var settings = await Bind(fixture, """
            {
              "Name": "默认配置",
              "TaskEnabledList": { "领取每日奖励": false, "配置组": true },
              "CompletionAction": "关闭软件",
              "Unknown": { "values": [1, "二"] }
            }
            """);
        var shared = File.ReadAllBytes(settings.SharedConfigPath);
        var otherPath = Path.Combine(Path.GetDirectoryName(settings.ProfilePath)!, "日常.json");
        var other = File.ReadAllBytes(otherPath);
        var before = await settings.ReadAsync();
        Assert.Equal("false", before.ProfileValues["dailyRewards"]);
        Assert.False(before.ProfileValues.ContainsKey("autoDomain"));

        var after = await settings.UpdateProfileAsync(before.ProfileRevision,
            new Dictionary<string, string> { ["dailyRewards"] = "true", ["MinResinToKeep"] = "20" });

        Assert.Equal("true", after.ProfileValues["dailyRewards"]);
        Assert.Equal("20", after.ProfileValues["MinResinToKeep"]);
        Assert.NotEqual(before.ProfileRevision, after.ProfileRevision);
        var profile = JsonNode.Parse(File.ReadAllText(settings.ProfilePath))!;
        Assert.True((bool)profile["TaskEnabledList"]!["配置组"]!);
        Assert.Equal("二", (string?)profile["Unknown"]!["values"]![1]);
        Assert.Equal("关闭软件", (string?)profile["CompletionAction"]);
        Assert.Equal(other, File.ReadAllBytes(otherPath));
        Assert.Equal(shared, File.ReadAllBytes(settings.SharedConfigPath));
    }

    [Fact]
    public async Task ModernProfileUsesTaskIdAndKeepsOrderResumeAndUnknownTasks()
    {
        await using var fixture = new BetterGiFixture();
        var settings = await Bind(fixture, """
            {
              "Name": "默认配置",
              "TaskEnabledList": { "first": true, "next": false, "external": true },
              "TaskDefinitions": { "first": "合成树脂", "next": "领取每日奖励", "external": "路线配置组" },
              "TaskOrder": ["external", "next", "first"],
              "NextTaskId": "next"
            }
            """);
        var before = await settings.ReadAsync();

        await settings.UpdateProfileAsync(before.ProfileRevision, new Dictionary<string, string> { ["dailyRewards"] = "true" });

        var profile = JsonNode.Parse(File.ReadAllText(settings.ProfilePath))!;
        Assert.True((bool)profile["TaskEnabledList"]!["next"]!);
        Assert.Equal("next", (string?)profile["NextTaskId"]);
        Assert.Equal("external", (string?)profile["TaskOrder"]![0]);
        Assert.Equal("路线配置组", (string?)profile["TaskDefinitions"]!["external"]);
    }

    [Fact]
    public async Task DuplicateProfileIdentityOrRepeatedTaskCannotBeSilentlySelected()
    {
        await using var fixture = new BetterGiFixture();
        var settings = await Bind(fixture, """
            {
              "Name": "默认配置",
              "TaskEnabledList": { "a": true, "b": false },
              "TaskDefinitions": { "a": "领取每日奖励", "b": "领取每日奖励" },
              "TaskOrder": ["a", "b"]
            }
            """);
        var before = await settings.ReadAsync();
        Assert.False(before.ProfileValues.ContainsKey("dailyRewards"));
        await Assert.ThrowsAsync<ArgumentException>(() => settings.UpdateProfileAsync(before.ProfileRevision,
            new Dictionary<string, string> { ["dailyRewards"] = "true" }));

        File.WriteAllText(Path.Combine(Path.GetDirectoryName(settings.ProfilePath)!, "重复.json"), """{"Name":"默认配置"}""");
        await Assert.ThrowsAsync<InvalidDataException>(() => settings.ReadAsync());
    }

    [Fact]
    public async Task NativeChangesInvalidateStaleSnapshotAndStayIntact()
    {
        await using var fixture = new BetterGiFixture();
        var settings = await Bind(fixture, """{"Name":"默认配置","MinResinToKeep":10}""");
        var before = await settings.ReadAsync();
        const string upstream = """{"Name":"默认配置","MinResinToKeep":35,"future":true}""";
        File.WriteAllText(settings.ProfilePath, upstream);

        await Assert.ThrowsAsync<BetterGiSourceSettingsConflictException>(() => settings.UpdateProfileAsync(before.ProfileRevision,
            new Dictionary<string, string> { ["MinResinToKeep"] = "20" }));

        Assert.Equal(upstream, File.ReadAllText(settings.ProfilePath));
        Assert.Equal("35", (await settings.ReadAsync()).ProfileValues["MinResinToKeep"]);
    }

    [Fact]
    public async Task AtomicExternalEditBeforeReplacementIsPreservedAtSource()
    {
        await using var fixture = new BetterGiFixture();
        const string original = """{"Name":"默认配置","MinResinToKeep":10}""";
        const string external = """{"Name":"默认配置","MinResinToKeep":55}""";
        var path = Path.Combine(fixture.Root, "User", "OneDragon", "默认配置.json");
        var probe = new CallbackProbe(() =>
        {
            File.WriteAllText(path + ".editor-tmp", external);
            File.Replace(path + ".editor-tmp", path, null);
        });
        var settings = await Bind(fixture, original, probe);
        var before = await settings.ReadAsync();

        await Assert.ThrowsAsync<BetterGiSourceSettingsConflictException>(() => settings.UpdateProfileAsync(before.ProfileRevision,
            new Dictionary<string, string> { ["MinResinToKeep"] = "20" }));

        Assert.Equal(external, File.ReadAllText(settings.ProfilePath));
    }

    [Fact]
    public async Task ADeleteShareableExternalHandleStillBlocksSourceWrite()
    {
        await using var fixture = new BetterGiFixture();
        const string original = """{"Name":"默认配置","MinResinToKeep":10}""";
        var settings = await Bind(fixture, original);
        var before = await settings.ReadAsync();

        using (File.Open(settings.ProfilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => settings.UpdateProfileAsync(before.ProfileRevision,
                new Dictionary<string, string> { ["MinResinToKeep"] = "20" }));
        }

        Assert.Equal(original, File.ReadAllText(settings.ProfilePath));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(settings.ProfilePath)!, "*.backup"));
    }

    [Fact]
    public async Task SharedDomainUpdateIsExplicitAndKeepsUnrelatedSettings()
    {
        await using var fixture = new BetterGiFixture();
        var settings = await Bind(fixture, """{"Name":"默认配置"}""");
        File.WriteAllText(settings.SharedConfigPath, """
            {"selectedOneDragonFlowConfigName":"日常","autoDomainConfig":{
                "originalResin20UseCount":2,"autoArtifactSalvage":true,"resinPriorityList":["浓缩树脂","原粹树脂"]
            }}
            """);
        var before = await settings.ReadAsync();
        var changes = new Dictionary<string, string> { ["autoDomainConfig.originalResin20UseCount"] = "4" };
        Assert.Equal("2", before.SharedDomainValues["autoDomainConfig.originalResin20UseCount"]);
        await Assert.ThrowsAsync<ArgumentException>(() => settings.UpdateProfileAsync(before.ProfileRevision, changes));

        var after = await settings.UpdateSharedDomainAsync(before.SharedDomainRevision, changes);

        Assert.Equal("4", after.SharedDomainValues["autoDomainConfig.originalResin20UseCount"]);
        Assert.Equal(before.ProfileRevision, after.ProfileRevision);
        var shared = JsonNode.Parse(File.ReadAllText(settings.SharedConfigPath))!;
        Assert.Equal("日常", (string?)shared["selectedOneDragonFlowConfigName"]);
        Assert.True((bool)shared["autoDomainConfig"]!["autoArtifactSalvage"]!);
        Assert.Equal("浓缩树脂", (string?)shared["autoDomainConfig"]!["resinPriorityList"]![0]);
    }

    [Fact]
    public async Task RunningEngineAndDirtyDesktopLeaseBlockWritesWithoutChangingSource()
    {
        await using var fixture = new BetterGiFixture();
        var settings = await Bind(fixture, """{"Name":"默认配置","MinResinToKeep":10}""");
        var before = await settings.ReadAsync();
        var changes = new Dictionary<string, string> { ["MinResinToKeep"] = "20" };
        fixture.Probe.Running = 1;
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.UpdateProfileAsync(before.ProfileRevision, changes));
        fixture.Probe.Running = 0;
        using (var lease = DesktopOperationLease.AcquireAt(LockPath(fixture), fixture.Root, requireExisting: true))
            lease.MarkDirty();

        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.UpdateProfileAsync(before.ProfileRevision, changes));

        Assert.Equal(before.ProfileRevision, (await settings.ReadAsync()).ProfileRevision);
    }

    private static string LockPath(BetterGiFixture fixture) => Path.Combine(fixture.Root, "test-desktop.lock");

    private static async Task<BetterGiSourceSettings> Bind(BetterGiFixture fixture, string profile, IBetterGiProcessProbe? probe = null)
    {
        File.WriteAllText(Path.Combine(fixture.Root, "User", "OneDragon", "默认配置.json"), profile);
        File.WriteAllBytes(LockPath(fixture), []);
        return await BetterGiSourceSettings.BindAsync(fixture.Root, "默认配置", probe ?? fixture.Probe, LockPath(fixture), _ => true);
    }

    private sealed class CallbackProbe(Action beforeReplace) : IBetterGiProcessProbe
    {
        private int _calls;

        public bool IsBusy(string programPath)
        {
            if (++_calls == 2) beforeReplace();
            return false;
        }
    }
}
