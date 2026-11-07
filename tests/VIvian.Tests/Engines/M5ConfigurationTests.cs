using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Infrastructure.Engines;
using VIvian.Infrastructure.Engines.Cli;
using Xunit;

namespace VIvian.Tests.Engines;

public sealed class M5ConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vivian-m5-" + Guid.NewGuid().ToString("N"));
    private readonly VersionedConfigurationControl _service = new();
    private string Config => Path.Combine(_root, "config.yaml");

    public M5ConfigurationTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "assets", "config"));
        File.WriteAllText(Path.Combine(_root, "assets", "config", "version.txt"), "v2026.10.31\n");
        File.WriteAllText(Config,
            "# keep comment\r\ndaily_enable: true # daily\r\npower_enable: true\r\nreward_enable: true\r\npause_after_success: false\r\nexit_after_failure: true\r\nunknown_future: 42 # untouched\r\n");
    }
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void RegistryMapsPinnedDailyAndAllThreeEngines()
    {
        var daily = _service.GetTaskSchema(GameId.StarRail, "daily");
        Assert.Equal(TaskFrequency.Daily, daily.Frequency);
        Assert.Contains("daily", daily.Invocation);
        Assert.Contains("RunId", daily.CompletionSignal);
        Assert.Equal("v2026.10.31", EngineCatalog.March7th.PinnedVersion);
        Assert.NotEmpty(_service.ListTasks(GameId.Genshin));
        Assert.NotEmpty(_service.ListTasks(GameId.ZenlessZoneZero));
        Assert.Equal(ParameterAccess.UpstreamManaged,
            EngineCatalog.OneDragon.Parameters.Single().Access);
    }

    [Fact]
    public void EditPreservesUnknownFieldsCommentsAndCrLfAndCanRestoreBackup()
    {
        var before = File.ReadAllBytes(Config);
        var snapshot = _service.ReadConfiguration(GameId.StarRail, _root);
        var proposal = _service.ProposeChanges(snapshot, new Dictionary<string, string> { ["daily_enable"] = "false" });
        Assert.True(_service.ValidateChanges(proposal, false).Valid);
        var result = _service.ApplyChanges(proposal, Path.Combine(_root, "audit"), false);
        Assert.Equal("false", result.Snapshot.Values.Single(x => x.Schema.Path == "daily_enable").Value);
        var text = File.ReadAllText(Config);
        Assert.Contains("unknown_future: 42 # untouched\r\n", text);
        Assert.Contains("daily_enable: false # daily\r\n", text);
        Assert.Equal(before, File.ReadAllBytes(result.BackupPath));
        Assert.True(File.Exists(result.AuditPath));
        File.Copy(result.BackupPath, Config, overwrite: true);
        Assert.Equal(before, File.ReadAllBytes(Config));
    }

    [Fact]
    public void ExternalChangeConflictAndRunningWriteAreBlocked()
    {
        var snapshot = _service.ReadConfiguration(GameId.StarRail, _root);
        var proposal = _service.ProposeChanges(snapshot, new Dictionary<string, string> { ["power_enable"] = "false" });
        Assert.False(_service.ValidateChanges(proposal, true).Valid);
        // An unrelated change made elsewhere (upstream timestamps, other settings) no longer blocks the save...
        File.AppendAllText(Config, "external: true\r\n");
        var saved = _service.ApplyChanges(proposal, Path.Combine(_root, "audit"), false);
        Assert.Equal("false", saved.Snapshot.Values.Single(v => v.Schema.Path == "power_enable").Value);
        Assert.Contains("external: true", File.ReadAllText(Config));
        // ...but a change to the same field is a conflict and nothing is written.
        var stale = _service.ProposeChanges(snapshot, new Dictionary<string, string> { ["daily_enable"] = "false" });
        File.WriteAllText(Config, File.ReadAllText(Config).Replace("daily_enable: true", "daily_enable: false"));
        var before = File.ReadAllText(Config);
        var conflict = Assert.Throws<ConfigurationConflictException>(() => _service.ApplyChanges(stale, Path.Combine(_root, "audit"), false));
        Assert.Equal("daily_enable", conflict.Path);
        Assert.Equal(before, File.ReadAllText(Config));
    }

    [Fact]
    public void OpenGuiDoesNotBlockSavingButARunningTaskDoes()
    {
        var snapshot = _service.ReadConfiguration(GameId.StarRail, _root);
        var proposal = _service.ProposeChanges(snapshot, new Dictionary<string, string> { ["power_enable"] = "false" });
        Assert.True(_service.ValidateChanges(proposal, false).Valid);
        var runs = Directory.CreateDirectory(Path.Combine(_root, "logs", "runs")).FullName;
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var created = new DateTimeOffset(self.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds() / 1000.0;
        File.WriteAllText(Path.Combine(runs, "active.json"),
            $"{{\"run_id\": \"20261107-010203-abcd\", \"pid\": {self.Id}, \"pid_create_time\": {created.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}");
        var blocked = _service.ValidateChanges(proposal, false);
        Assert.False(blocked.Valid);
        Assert.True(blocked.Deferred); // queued and written after the task, not rejected as invalid
        Assert.True(_service.ValidateChanges(proposal, true).Deferred);
        Assert.Contains("20261107-010203-abcd", blocked.Reason);
        // A stale lock from a process that started at another time (PID reuse) does not block.
        File.WriteAllText(Path.Combine(runs, "active.json"),
            $"{{\"run_id\": \"old\", \"pid\": {self.Id}, \"pid_create_time\": 1000}}");
        Assert.True(_service.ValidateChanges(proposal, false).Valid);
    }

    [Fact]
    public void InvalidValueAndVersionAreBlocked()
    {
        var snapshot = _service.ReadConfiguration(GameId.StarRail, _root);
        var proposal = _service.ProposeChanges(snapshot, new Dictionary<string, string> { ["reward_enable"] = "maybe" });
        Assert.False(_service.ValidateChanges(proposal, false).Valid);
        Assert.Throws<ArgumentException>(() => _service.ProposeChanges(snapshot,
            new Dictionary<string, string> { ["unknown_future"] = "false" }));
        File.WriteAllText(Path.Combine(_root, "assets", "config", "version.txt"), "v2026.11.01");
        Assert.Throws<InvalidOperationException>(() => _service.ReadConfiguration(GameId.StarRail, _root));
    }

    [Theory]
    [InlineData("开始日常任务\n当前累计分数：500/500\n实训分数已达标\n每日实训已完成", true)]
    [InlineData("开始日常任务\n当前累计分数：400/500", false)]
    // Upstream logs "任务无法完成" for sub-tasks it does not implement and keeps going; a later verified 500 counts.
    [InlineData("开始日常任务\n任务无法完成: 累计消耗120点开拓力\n实训分数已达标\n2026-11-06 12:00:00,000 | INFO | 每日实训已完成", true)]
    [InlineData("开始日常任务\n每日实训已完成\n每日实训未完成", false)]
    [InlineData("每日实训已完成\n开始日常任务", false)]
    [InlineData("开始日常任务\n每日实训已完成\n发生错误 xyz", false)]
    [InlineData("开始日常任务\n日志提到每日实训已完成之类的说明文字", false)]
    [InlineData("实训分数已达标", false)]
    public void DailyCompletionRequiresTaskEvidenceWithoutConflict(string output, bool expected)
        => Assert.Equal(expected, March7thDailyEvidence.ProvesComplete(output));

    [Fact]
    public void ExitZeroWithoutDailyEvidenceRemainsIndeterminate()
    {
        const string id = "20261106-120000-abcd";
        var final = "{\"schema\":1,\"run_id\":\"" + id +
            "\",\"task\":\"daily\",\"status\":\"success\",\"lock\":\"acquired\",\"exit_code\":0,\"ended_at\":\"2026-11-06T12:01:00Z\"}";
        var unknown = March7thTaskContract.ParseFinalRecord("开始日常任务\n当前累计分数：400/500\n" + final,
            "daily", 0, id);
        Assert.Equal(VIvian.Core.Execution.RunOutcome.Indeterminate, unknown.Outcome);
        var complete = March7thTaskContract.ParseFinalRecord("开始日常任务\n当前累计分数：500/500\n每日实训已完成\n" + final,
            "daily", 0, id);
        Assert.True(March7thDailyEvidence.ProvesComplete("开始日常任务\n当前累计分数：500/500\n每日实训已完成\n" + final), complete.Detail);
        Assert.Equal(VIvian.Core.Execution.RunOutcome.Success, complete.Outcome);
    }

    [Fact]
    public void InvalidValuesAreNotDeferredAndResourceSpendingNeedsConfirmation()
    {
        var snapshot = _service.ReadConfiguration(GameId.StarRail, _root);
        var bad = _service.ProposeChanges(snapshot, new Dictionary<string, string> { ["reward_enable"] = "maybe" });
        Assert.False(_service.ValidateChanges(bad, false).Deferred);
        Assert.True(ParameterRules.RequiresConfirmation(new ConfigurationChange("use_fuel", "false", "true")));
        Assert.False(ParameterRules.RequiresConfirmation(new ConfigurationChange("use_fuel", "true", "false")));
        Assert.False(ParameterRules.RequiresConfirmation(new ConfigurationChange("instance_type", "拟造花萼（金）", "饰品提取")));
    }
}
