using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Engines;
using VIvian.Infrastructure.Engines.Cli;
using Xunit;

namespace VIvian.Tests.Engines;

/// <summary>Daily readiness: no pause/exit precondition, reward_enable handled as a recoverable temporary change.</summary>
public sealed class M5DailyReadinessTests : IDisposable
{
    private const string Id = "20261106-120000-abcd";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vivian-m5d-" + Guid.NewGuid().ToString("N"));
    private readonly VersionedConfigurationControl _control = new();
    private string Config => Path.Combine(_root, "config.yaml");
    private string Data => Path.Combine(_root, "data");
    private TemporaryConfigurationManager NewManager() =>
        new(_control, Path.Combine(Data, "recovery"), Path.Combine(Data, "audit"));

    public M5DailyReadinessTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "assets", "config"));
        File.WriteAllText(Path.Combine(_root, "assets", "config", "version.txt"), "v2026.10.31\n");
        File.WriteAllText(Config,
            "# user file\r\ndaily_enable: true\r\npower_enable: true\r\nreward_enable: false # user keeps this off\r\n" +
            "pause_after_success: true\r\nexit_after_failure: false\r\nlast_run_timestamp: 0\r\n" +
            "notify_template:\r\n  Title: x\r\n  DailyPracticeCompleted: \"实训搞定啦\"\r\n  DailyPracticeNotCompleted: \"实训没搞定\"\r\n");
    }
    public void Dispose() => TestDirectories.DeleteWithRetry(_root);

    private static string Final(string task = "daily") => "{\"schema\":1,\"run_id\":\"" + Id + "\",\"task\":\"" + task +
        "\",\"status\":\"success\",\"lock\":\"acquired\",\"exit_code\":0,\"ended_at\":\"2026-11-06T12:01:00Z\"}";

    [Fact]
    public void StartArgumentsAlwaysRunNonInteractivelySoPauseSettingsDoNotMatter()
    {
        foreach (var task in new[] { "daily", "screen_test" })
            Assert.Contains("--non-interactive", March7thTaskContract.StartArguments(task));
        Assert.DoesNotContain("pause_after_success", March7thTaskRequirements.For("daily").Keys);
        Assert.DoesNotContain("exit_after_failure", March7thTaskRequirements.For("daily").Keys);
        Assert.Equal("true", March7thTaskRequirements.For("daily")["reward_enable"]);
        Assert.Empty(March7thTaskRequirements.For("screen_test"));
    }

    [Fact]
    public void PlanOnlyListsSettingsThatDiffer()
    {
        var snapshot = _control.ReadConfiguration(GameId.StarRail, _root);
        var plan = TemporaryConfigurationManager.Plan(snapshot, March7thTaskRequirements.For("daily"));
        var change = Assert.Single(plan);
        Assert.Equal(new TemporaryChange("reward_enable", "false", "true"), change);
        Assert.Contains("启用领取奖励", TemporaryConfigurationManager.Describe(plan, snapshot));
        Assert.Throws<InvalidOperationException>(() => TemporaryConfigurationManager.Plan(snapshot,
            new Dictionary<string, string> { ["daily_tasks"] = "[]" }));
    }

    [Fact]
    public void ApplyThenRestoreKeepsUpstreamRunWritesAndPutsBackOnlyOurField()
    {
        var manager = NewManager();
        var snapshot = _control.ReadConfiguration(GameId.StarRail, _root);
        var plan = TemporaryConfigurationManager.Plan(snapshot, March7thTaskRequirements.For("daily"));
        manager.Apply(snapshot, plan, _root, "daily");
        Assert.True(manager.HasPending);
        Assert.Contains("reward_enable: true # user keeps this off\r\n", File.ReadAllText(Config));
        // The upstream run legitimately rewrites other fields (timestamps) while the change is in place.
        File.WriteAllText(Config, File.ReadAllText(Config).Replace("last_run_timestamp: 0", "last_run_timestamp: 1762430000"));
        var result = manager.Restore();
        Assert.Equal(RestoreState.Restored, result.State);
        var text = File.ReadAllText(Config);
        Assert.Contains("reward_enable: false # user keeps this off\r\n", text);
        Assert.Contains("last_run_timestamp: 1762430000", text);
        Assert.False(manager.HasPending);
    }

    [Fact]
    public void InterruptedRunIsRecoveredByANewInstanceAfterRestart()
    {
        var snapshot = _control.ReadConfiguration(GameId.StarRail, _root);
        NewManager().Apply(snapshot, TemporaryConfigurationManager.Plan(snapshot, March7thTaskRequirements.For("daily")), _root, "daily");
        var afterRestart = NewManager();
        Assert.True(afterRestart.HasPending);
        Assert.Equal("daily", afterRestart.ReadPending()!.Task);
        Assert.Equal(RestoreState.Restored, afterRestart.Restore().State);
        Assert.Contains("reward_enable: false", File.ReadAllText(Config));
    }

    [Fact]
    public void UserRevertDuringRunIsNotOverwrittenAndUnreadableFileKeepsRecord()
    {
        var manager = NewManager();
        var snapshot = _control.ReadConfiguration(GameId.StarRail, _root);
        manager.Apply(snapshot, TemporaryConfigurationManager.Plan(snapshot, March7thTaskRequirements.For("daily")), _root, "daily");
        var original = File.ReadAllText(Config);
        // Field made unreadable (e.g. upstream rewrote it in a new layout): nothing is written, record kept.
        File.WriteAllText(Config, original.Replace("reward_enable: true", "reward_enable: [true]"));
        Assert.Equal(RestoreState.Blocked, manager.Restore().State);
        Assert.True(manager.HasPending);
        Assert.Contains("reward_enable: [true]", File.ReadAllText(Config));
        // The user switched it back themselves: nothing to write, record resolved.
        File.WriteAllText(Config, original.Replace("reward_enable: true", "reward_enable: false"));
        Assert.Equal(RestoreState.NothingToRestore, manager.Restore().State);
        Assert.False(manager.HasPending);
    }

    [Fact]
    public void SecondTemporaryChangeIsRefusedWhileOneIsPending()
    {
        var manager = NewManager();
        var snapshot = _control.ReadConfiguration(GameId.StarRail, _root);
        var plan = TemporaryConfigurationManager.Plan(snapshot, March7thTaskRequirements.For("daily"));
        manager.Apply(snapshot, plan, _root, "daily");
        Assert.Throws<InvalidOperationException>(() => manager.Apply(snapshot, plan, _root, "daily"));
    }

    [Fact]
    public void FailedApplyLeavesNoTemporaryValueAndNoRecord()
    {
        var manager = NewManager();
        var snapshot = _control.ReadConfiguration(GameId.StarRail, _root);
        var plan = TemporaryConfigurationManager.Plan(snapshot, March7thTaskRequirements.For("daily"));
        File.AppendAllText(Config, "external_edit: true\r\n"); // revision conflict
        Assert.Throws<IOException>(() => manager.Apply(snapshot, plan, _root, "daily"));
        Assert.Contains("reward_enable: false", File.ReadAllText(Config));
        Assert.False(manager.HasPending);
    }

    [Fact]
    public void UserCustomisedNotificationTemplatesAreUsedForEvidence()
    {
        var templates = March7thDailyTemplates.Read(Config);
        Assert.Equal(new March7thDailyTemplates("实训搞定啦", "实训没搞定"), templates);
        var done = "开始日常任务\n2026-11-06 12:00:00,000 | INFO | 实训搞定啦\n" + Final();
        Assert.Equal(RunOutcome.Success, March7thTaskContract.ParseFinalRecord(done, "daily", 0, Id, templates).Outcome);
        Assert.Equal(RunOutcome.Indeterminate, March7thTaskContract.ParseFinalRecord(done, "daily", 0, Id).Outcome);
        var notDone = "开始日常任务\n2026-11-06 12:00:00,000 | INFO | 实训没搞定\n" + Final();
        Assert.Equal(RunOutcome.Failed, March7thTaskContract.ParseFinalRecord(notDone, "daily", 0, Id, templates).Outcome);
        Assert.Equal(March7thDailyTemplates.Default, March7thDailyTemplates.Read(Path.Combine(_root, "missing.yaml")));
    }

    [Fact]
    public void SkippedSubTasksAreReportedButDoNotBlockVerifiedCompletion()
    {
        var output = "开始日常任务\n任务无法完成: 累计消耗120点开拓力\n任务无法完成: 拍照1次\n实训分数已达标\n每日实训已完成\n" + Final();
        var result = March7thTaskContract.ParseFinalRecord(output, "daily", 0, Id);
        Assert.Equal(RunOutcome.Success, result.Outcome);
        Assert.Contains("2 项子任务", result.Detail);
        var silent = March7thTaskContract.ParseFinalRecord("开始日常任务\n领取奖励未开启\n" + Final(), "daily", 0, Id);
        Assert.Equal(RunOutcome.Indeterminate, silent.Outcome);
    }
}

/// <summary>Regression from the 2026-11-06 real run: "daily" alone never spends stamina and stops at 300/500.</summary>
public sealed class M5RoutineTaskTests
{
    private const string Id = "20261106-080829-b43d";
    private static string Final(string task) => "{\"schema\":1,\"run_id\":\"" + Id + "\",\"task\":\"" + task +
        "\",\"status\":\"success\",\"lock\":\"acquired\",\"exit_code\":0,\"ended_at\":\"2026-11-06T08:12:42-05:00\"}";

    // Trimmed from the user's real run log (logs/runs/20261106-080829-b43d.log).
    private const string RealDailyRun = """
        开始日常任务
        2026-11-06 08:09:30,000 | INFO | 当前累计分数：100/500
        2026-11-06 08:10:00,000 | INFO | 完成任务: 派遣委托或收取1次委托奖励 (+100分)，当前分数: 200/500
        2026-11-06 08:10:01,000 | INFO | 任务无法完成: 累计消耗120点开拓力
        2026-11-06 08:10:02,000 | INFO | 任务无法完成: 使用支援角色并获得战斗胜利1次
        2026-11-06 08:10:30,000 | INFO | 完成任务: 使用1次「万能合成机」 (+100分)，当前分数: 300/500
        2026-11-06 08:11:30,000 | INFO | 每日实训未完成
        """;

    [Fact]
    public void M5BuildRunsRoutineWhichSpendsStaminaBeforeDailyPractice()
    {
        Assert.Equal("routine", VIvian.Core.Engines.RealExecutionPolicy.M5RealTask);
        Assert.Equal(["routine", "--after-finish", "None", "--non-interactive", "--json"], March7thTaskContract.StartArguments("routine"));
        var required = March7thTaskRequirements.For("routine");
        Assert.Equal("true", required["power_enable"]);
        Assert.Equal("true", required["daily_enable"]);
        Assert.Equal("true", required["reward_enable"]);
        var routine = VIvian.Core.Engines.EngineCatalog.March7th.Tasks.Single(t => t.Id == "routine");
        Assert.Equal(VIvian.Core.Engines.TaskFrequency.Daily, routine.Frequency);
        Assert.Contains("routine", routine.Invocation);
    }

    [Fact]
    public void RealThreeHundredPointRunIsFailedNotSuccess()
    {
        foreach (var task in new[] { "daily", "routine" })
            Assert.Equal(VIvian.Core.Execution.RunOutcome.Failed,
                March7thTaskContract.ParseFinalRecord(RealDailyRun + "\n" + Final(task), task, 0, Id).Outcome);
    }

    [Fact]
    public void RoutineReachingFiveHundredIsSuccess()
    {
        var output = """
            开始清体力
            开始日常任务
            2026-11-06 08:20:00,000 | INFO | 任务无法完成: 累计消灭20个敌人
            2026-11-06 08:20:10,000 | INFO | 实训分数已达标
            2026-11-06 08:20:20,000 | INFO | 每日实训已完成
            """ + "\n" + Final("routine");
        Assert.Equal(VIvian.Core.Execution.RunOutcome.Success, March7thTaskContract.ParseFinalRecord(output, "routine", 0, Id).Outcome);
    }
}
