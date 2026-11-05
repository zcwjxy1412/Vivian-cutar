using VIvian.Core.Execution;
using VIvian.Infrastructure.Engines.Cli;
using Xunit;

namespace VIvian.Tests.Engines;

public class March7thScreenTestEvidenceTests
{
    private const string Id = "20261031-123456-ab12";
    private const string Start = "共 2 个待测界面";
    private const string Steps = "[1/2] 切换到 主菜单 成功！\n[2/2] 切换到 每日实训 成功！";
    private const string End = "测试完成！成功 2 个，失败 0 个，共 2 个界面";
    private const string Record = """
        {"schema":1,"ended_at":"2026-10-31T12:40:00","run_id":"20261031-123456-ab12","task":"screen_test","status":"success","lock":"acquired","exit_code":0}
        """;

    [Fact]
    public void RequiresEveryActualScreenSwitchAndOwnedFinalRecord()
    {
        var output = string.Join('\n', Start, Steps, End, Record);
        Assert.True(March7thScreenTestEvidence.ProvesComplete(output));
        Assert.Equal(RunOutcome.Success, March7thTaskContract.ParseFinalRecord(output, "screen_test", 0, Id).Outcome);
        Assert.Equal(RunOutcome.Indeterminate, March7thTaskContract.ParseFinalRecord(output, "screen_test", 0, "20261031-123456-ffff").Outcome);
        Assert.Equal(RunOutcome.Indeterminate, March7thTaskContract.ParseFinalRecord(output, "screen_test", 1, Id).Outcome);
        Assert.Equal(RunOutcome.Indeterminate, March7thTaskContract.ParseFinalRecord(output.Replace("\"acquired\"", "\"unavailable\""), "screen_test", 0, Id).Outcome);
    }

    [Theory]
    [InlineData("共 0 个待测界面\n" + End)]
    [InlineData(Start + "\n[1/2] 切换到 主菜单 成功！\n" + End)]
    [InlineData(Start + "\n[1/2] 切换到 主菜单 成功！\n[1/2] 切换到 每日实训 成功！\n" + End)]
    [InlineData(Start + "\n" + Steps + "\n测试完成！成功 1 个，失败 1 个，共 2 个界面")]
    public void MissingConflictingOrFailedStepsNeverProveCompletion(string output)
        => Assert.False(March7thScreenTestEvidence.ProvesComplete(output));
}
