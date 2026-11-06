using VIvian.Core.Execution;
using VIvian.Infrastructure.Engines.Cli;
using Xunit;

namespace VIvian.Tests.Engines;

public class March7thTaskContractTests
{
    private const string Id = "20261031-123456-ab12";

    [Fact]
    public void ExactUacCancellationExplainsFailureButNeverProvesCleanup()
    {
        var text = $$"""{"run_id":"{{Id}}","status":"not_started","exit_code":5,"error":"[WinError 1223] 操作已被用户取消。"}""";
        var cancelled = March7thTaskContract.ParseFinalRecord(text, "screen_test", 5, Id);
        Assert.Equal(RunOutcome.Indeterminate, cancelled.Outcome);
        Assert.Contains("UAC 已取消", cancelled.Detail);
        Assert.DoesNotContain("已确认清理", cancelled.Detail);
        Assert.DoesNotContain("UAC 已取消", March7thTaskContract.ParseFinalRecord(text, "screen_test", 5,
            "20261031-123456-ffff").Detail);
        Assert.DoesNotContain("UAC 已取消", March7thTaskContract.ParseFinalRecord(text, "screen_test", 0, Id).Detail);
    }

    [Fact]
    public void CommandsUseWhitelistedTasksAndExactRunId()
    {
        Assert.Equal(["daily", "--after-finish", "None", "--json"], March7thTaskContract.StartArguments("daily"));
        Assert.Equal(["--control", Id, "stop", "--json"], March7thTaskContract.StopArguments(Id));
        Assert.Throws<ArgumentException>(() => March7thTaskContract.StartArguments("--force"));
        Assert.Throws<ArgumentException>(() => March7thTaskContract.StopArguments("current"));
        Assert.Equal(Id, March7thTaskContract.CaptureRunId(["[m7a] 运行任务需要管理员权限，请在 UAC 弹窗中选择「是」。运行编号 " + Id]));
        Assert.Null(March7thTaskContract.CaptureRunId(["[m7a] 运行编号 " + Id, "[m7a] 运行编号 20261031-123456-ffff"]));
        Assert.Null(March7thTaskContract.CaptureRunId(["运行编号 " + Id]));
    }

    [Theory]
    [InlineData("success", 0, RunOutcome.Indeterminate)]
    [InlineData("failed", 1, RunOutcome.Failed)]
    [InlineData("blocked", 4, RunOutcome.Failed)]
    [InlineData("stopped", 6, RunOutcome.Cancelled)]
    [InlineData("aborted", 1, RunOutcome.Indeterminate)]
    public void RecordOutcomeRequiresMatchingRunTaskAndExit(string status, int exit, RunOutcome expected)
    {
        var text = "启动中\n" + $$"""{"schema":1,"ended_at":"2026-10-31T12:40:00","run_id":"{{Id}}","task":"daily","status":"{{status}}","lock":"acquired","exit_code":{{exit}}}""";
        Assert.Equal(expected, March7thTaskContract.ParseFinalRecord(text, "daily", exit, Id).Outcome);
        Assert.Equal(RunOutcome.Indeterminate, March7thTaskContract.ParseFinalRecord(text, "power", exit, Id).Outcome);
        Assert.Equal(RunOutcome.Indeterminate, March7thTaskContract.ParseFinalRecord(text, "daily", exit, "20261031-123456-ffff").Outcome);
    }

    [Fact]
    public void ExitZeroWithoutOwnedEvidenceRemainsUnknown()
    {
        var text = $$"""{"schema":1,"ended_at":"2026-10-31T12:40:00","run_id":"{{Id}}","task":"daily","status":"success","lock":"acquired","exit_code":0}""";
        Assert.Equal(RunOutcome.Indeterminate, March7thTaskContract.ParseFinalRecord("仅退出码 0", "daily", 0, Id).Outcome);
        Assert.Equal(RunOutcome.Indeterminate, March7thTaskContract.ParseFinalRecord(text, "daily", 1, Id).Outcome);
        Assert.Equal(RunOutcome.Indeterminate, March7thTaskContract.ParseFinalRecord(text.Replace("acquired", "unavailable"), "daily", 0, Id).Outcome);
        Assert.Equal(RunOutcome.Indeterminate, March7thTaskContract.ParseFinalRecord(text.Replace("\"exit_code\":0", "\"exit_code\":0,\"error\":null").Replace("success", "running"), "daily", 0, Id).Outcome);
    }
}
