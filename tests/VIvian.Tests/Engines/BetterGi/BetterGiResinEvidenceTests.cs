using VIvian.Core.Execution;
using VIvian.Infrastructure.Engines.BetterGi;
using Xunit;

namespace VIvian.Tests.Engines.BetterGi;

public sealed class BetterGiResinEvidenceTests
{
    private const string Profile = "验收配置";
    private const string Domain = "虹灵的净土";
    private const string TaskSource = "BetterGenshinImpact.GameTask.Common.TaskControl";
    private const int Pid = 4242;
    private static readonly DateTimeOffset Start = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("合成\"浓缩树脂\"")]
    [InlineData("无需合成浓缩树脂")]
    public void CraftingAttemptOrSkipDoesNotProveCreation(string action)
    {
        var verdict = Replay(Request("合成树脂"),
            Flow("一条龙任务执行: 1/1"), Task("→ \"前往合成台\" 开始"), Task(action), Task("→ \"前往合成台\" 结束"));

        Assert.Equal(RunOutcome.Indeterminate, verdict.Outcome);
        Assert.Contains(verdict.Evidence, evidence => evidence.Kind == EvidenceKind.Note && evidence.Source.Contains("入包核查", StringComparison.Ordinal));
    }

    [Fact]
    public void DomainEndingAfterResinShortageDoesNotProveCompletion()
    {
        var verdict = Replay(DomainRequest(),
            [.. DomainStart(), Task("自动秘境：\"5. 领取奖励\""), Task("自动秘境：原粹树脂已用尽，退出秘境"),
                Task("体力耗尽或者设置轮次已达标，结束自动秘境")]);

        Assert.Equal(RunOutcome.Indeterminate, verdict.Outcome);
    }

    [Fact]
    public void DomainRequiresEveryPlannedResinUseToHaveItsOwnRecognizedReward()
    {
        var verdict = Replay(DomainRequest(),
            [.. DomainStart(), .. Round("浓缩树脂", "1/2"), .. Round("原粹树脂20", "1/1"), .. Round("浓缩树脂", "2/2"),
                Task("体力耗尽或者设置轮次已达标，结束自动秘境")]);

        Assert.Equal(RunOutcome.Success, verdict.Outcome);
    }

    [Theory]
    [InlineData(2, "自动秘境：传送到秘境\"其他秘境\"")]
    [InlineData(4, "自动秘境：\"浓缩树脂\" 刷取 1/1")]
    [InlineData(4, "自动秘境：\"浓缩树脂\" 刷取 2/2")]
    [InlineData(5, "自动秘境：本轮奖励识别结果 \"空\"")]
    [InlineData(5, "自动秘境：本轮奖励识别结果 摩拉 x100")]
    [InlineData(5, "自动秘境：本轮奖励识别结果为空")]
    [InlineData(12, "自动秘境结束")]
    public void MissingOrContradictoryDomainEvidenceDoesNotBecomeSuccess(int index, string replacement)
    {
        IEnumerable<string>[] records = [.. DomainStart(), .. Round("浓缩树脂", "1/2"), .. Round("浓缩树脂", "2/2"),
            .. Round("原粹树脂20", "1/1"), Task("体力耗尽或者设置轮次已达标，结束自动秘境")];
        records[index] = Task(replacement);

        var verdict = Replay(DomainRequest(), records);

        Assert.Equal(RunOutcome.Indeterminate, verdict.Outcome);
    }

    [Theory]
    [InlineData(1234, TaskSource)]
    [InlineData(Pid, "OtherSource")]
    public void RewardEvidenceFromAnotherProcessOrSourceDoesNotCount(int pid, string source)
    {
        var first = Round("浓缩树脂", "1/2");
        first[2] = BgiLog.Record(pid, Start, "自动秘境：本轮奖励识别结果 \"摩拉 x100\"", source: source);
        var verdict = Replay(DomainRequest(),
            [.. DomainStart(), .. first, .. Round("浓缩树脂", "2/2"), .. Round("原粹树脂20", "1/1"),
                Task("体力耗尽或者设置轮次已达标，结束自动秘境")]);

        Assert.Equal(RunOutcome.Indeterminate, verdict.Outcome);
    }

    [Fact]
    public void DomainEvidenceInAGroupSlotDoesNotProveThePlannedBuiltInTask()
    {
        var start = DomainStart();
        start[0] = Flow("配置组任务执行: 1/1");
        var verdict = Replay(DomainRequest(),
            [.. start, .. Round("浓缩树脂", "1/2"), .. Round("浓缩树脂", "2/2"), .. Round("原粹树脂20", "1/1"),
                Task("体力耗尽或者设置轮次已达标，结束自动秘境")]);

        Assert.Equal(RunOutcome.Indeterminate, verdict.Outcome);
    }

    private static BetterGiTaskRequest DomainRequest() => Request("自动秘境") with
    {
        DomainResinPlan = new(true, ["浓缩树脂", "原粹树脂20"], 0, 1, 0, 2, 0, 0, false),
    };

    private static IEnumerable<string>[] DomainStart() =>
    [
        Flow("一条龙任务执行: 1/1"),
        Task("自动秘境任务：执行"),
        Task("自动秘境：传送到秘境\"虹灵的净土\""),
    ];

    private static IEnumerable<string>[] Round(string resin, string count) =>
    [
        Task("自动秘境：\"5. 领取奖励\""),
        Task($"自动秘境：\"{resin}\" 刷取 {count}"),
        Task("自动秘境：本轮奖励识别结果 \"摩拉 x100, 冒险阅历 x200\""),
    ];

    private static BetterGiTaskRequest Request(params string[] tasks) => BetterGiArguments.OneDragon(Profile).Request! with
    {
        OneDragonPlan = new(Profile, tasks, "关闭软件", "枫丹", 0, Domain, "", false, "枫丹", ""),
    };

    private static RunVerdict Replay(BetterGiTaskRequest request, params IEnumerable<string>[] taskLines)
    {
        var parser = new BetterGiLogRecordParser();
        var evidence = new BetterGiRunEvidence(Guid.NewGuid(), request, new(Pid, Start, TimeSpan.Zero));
        var lines = Flow("启用一条龙配置：" + Profile)
            .Concat(taskLines.SelectMany(l => l)).Concat(Flow("一条龙和配置组任务结束"));
        foreach (var line in lines)
            if (parser.Feed(line) is { } record) evidence.Add(record, Start);
        evidence.Add(parser.Flush()!, Start);
        return evidence.Evaluate(0, stoppedByVivian: false, detached: false, contractVerified: true, Start);
    }

    private static IEnumerable<string> Flow(string message) => BgiLog.Record(Pid, Start, message);
    private static IEnumerable<string> Task(string message) => BgiLog.Record(Pid, Start, message, source: TaskSource);
}
