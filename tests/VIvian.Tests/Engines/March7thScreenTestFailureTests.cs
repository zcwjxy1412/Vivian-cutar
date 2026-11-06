using VIvian.Core.Execution;
using VIvian.Infrastructure.Engines.Cli;
using Xunit;

namespace VIvian.Tests.Engines;

/// <summary>M4-F1 defect A: the R3 real test exited 0 with upstream status success, yet switched no screens.</summary>
public class March7thScreenTestFailureTests
{
    private const string Id = "20261105-234701-c953";

    // Sanitized from the R3 evidence (upstream run 20261105-234701-c953): paths replaced, long lists shortened,
    // repeated screenshot errors trimmed. Line order and the upstream message texts are unchanged.
    private const string R3Log = """
        [m7a] 已启动 screen_test，运行编号 20261105-234701-c953
        2026-11-05 23:47:04,926 | INFO | 游戏启动：C:\Game\StarRail.exe
        2026-11-05 23:47:14,927 | INFO | 窗口已切换到前台
        2026-11-05 23:47:29,708 | INFO | DML 预热完成，耗时 1.40 秒
        2026-11-05 23:53:14,173 | ERROR | 尝试启动游戏时发生错误：账号登录过期
        2026-11-05 23:53:14,551 | INFO | 游戏终止：StarRail
        2026-11-05 23:53:14,717 | INFO | 游戏启动：C:\Game\StarRail.exe
        2026-11-05 23:53:24,717 | INFO | 窗口已切换到前台
        ------------------------------------------------------- 完成 --------------------------------------------------------
        2026-11-05 23:55:09,441 | INFO | 共 39 个待测界面
        2026-11-05 23:55:09,442 | INFO | 测试顺序：手机菜单 -> 星际和平指南-每日实训 -> 地图
        2026-11-05 23:55:20,841 | ERROR | 截图失败：没有找到游戏窗口
        2026-11-05 23:56:21,559 | WARNING | 未识别出任何界面，请确保游戏画面干净，按ESC后重试
        2026-11-05 23:57:23,562 | ERROR | [1/39] 切换到 手机菜单 失败：截图超时
        2026-11-05 23:58:23,566 | ERROR | 无法回到主界面，测试终止
        2026-11-05 23:58:23,567 | INFO | 测试完成！成功 0 个，失败 1 个，共 39 个界面
        [m7a] 运行结束：成功（退出码 0）。
        {"schema": 1, "run_id": "20261105-234701-c953", "status": "success", "task": "screen_test", "workflow": null, "invoker": "entry", "version": "v2026.10.31", "pid": 152960, "pid_create_time": 1793940423.4278266, "started_at": "2026-11-05T23:47:03-05:00", "ended_at": "2026-11-05T23:58:23-05:00", "exit_code": 0, "reason": null, "error": null, "screenshot": null, "lock": "acquired", "lock_error": null}
        """;

    [Fact]
    public void R3LoginExpiredRunIsFailedEvenThoughUpstreamSaysSuccess()
    {
        var result = March7thTaskContract.ParseFinalRecord(R3Log, "screen_test", 0, Id);
        Assert.Equal(RunOutcome.Failed, result.Outcome);
        Assert.Equal(Id, result.UpstreamRunId);
        Assert.Contains("成功 0 个，失败 1 个，共 39 个界面", result.Detail);
        Assert.Contains("测试终止", result.Detail);
        Assert.Contains("账号登录过期", result.Detail);
        Assert.False(March7thScreenTestEvidence.ProvesComplete(R3Log));
    }

    [Fact]
    public void FailureSummaryNeverBecomesSuccessAndForeignRunStaysIndeterminate()
    {
        Assert.Equal(RunOutcome.Indeterminate,
            March7thTaskContract.ParseFinalRecord(R3Log, "screen_test", 0, "20261105-234701-ffff").Outcome);
        Assert.Equal(RunOutcome.Indeterminate, March7thTaskContract.ParseFinalRecord(R3Log, "screen_test", 1, Id).Outcome);
        Assert.Equal(RunOutcome.Indeterminate, March7thTaskContract.ParseFinalRecord(
            R3Log.Replace("\"acquired\"", "\"unavailable\""), "screen_test", 0, Id).Outcome);
    }

    [Theory]
    // No summary at all: the run's result is unknown, not failed.
    [InlineData("共 3 个待测界面\n[1/3] 切换到 地图 失败：截图超时")]
    // Two summaries (e.g. output from two runs mixed): contradictory.
    [InlineData("共 3 个待测界面\n测试完成！成功 0 个，失败 1 个，共 3 个界面\n测试完成！成功 0 个，失败 1 个，共 3 个界面")]
    // Summary total differs from the announced count.
    [InlineData("共 3 个待测界面\n测试完成！成功 0 个，失败 1 个，共 4 个界面")]
    // Counted successes disagree with the per-step lines.
    [InlineData("共 3 个待测界面\n测试完成！成功 2 个，失败 1 个，共 3 个界面")]
    // Counted failures disagree with the per-step failure lines.
    [InlineData("共 3 个待测界面\n[1/3] 切换到 地图 失败：超时\n测试完成！成功 0 个，失败 2 个，共 3 个界面")]
    // Summary before the start line.
    [InlineData("测试完成！成功 0 个，失败 1 个，共 3 个界面\n共 3 个待测界面")]
    // Zero failures and no termination: not a proven failure (a partial success stays unknown).
    [InlineData("共 3 个待测界面\n[1/3] 切换到 地图 成功！\n测试完成！成功 1 个，失败 0 个，共 3 个界面")]
    public void IncompleteOrContradictoryEvidenceIsNotAProvenFailure(string output)
        => Assert.Null(March7thScreenTestEvidence.ProvesFailure(output));

    [Fact]
    public void PartialFailuresWithMatchingStepsAreFailed()
    {
        var output = "共 3 个待测界面\n[1/3] 切换到 地图 成功！\n[2/3] 切换到 邮箱 失败：超时\n[3/3] 切换到 队伍 成功！\n"
            + "测试完成！成功 2 个，失败 1 个，共 3 个界面";
        var failure = March7thScreenTestEvidence.ProvesFailure(output);
        Assert.NotNull(failure);
        Assert.Equal((2, 1, 3, false, false), (failure.Succeeded, failure.Failed, failure.Total, failure.Terminated, failure.LoginExpired));
    }
}
