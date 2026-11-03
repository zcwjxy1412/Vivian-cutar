using System.Reflection;
using VIvian.Core;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Simulation;
using Xunit;

namespace VIvian.Tests.Execution;

[Collection(TimingCollection.Name)]
public sealed class SimulationModeTests
{
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(10);
    private readonly InMemoryJournal _journal = new();

    private async Task<QueueResult> RunAsync(params (GameId Game, DemoScenario Scenario)[] plan)
    {
        var limits = new RunLimits(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));
        var options = new SchedulerOptions(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), limits);
        var scheduler = new QueueScheduler(_journal, new RefusingProcessSupervisor(), options);
        return await scheduler.RunAsync(plan.Select(p => new QueueItem(p.Game, new DemoEngineAdapter(p.Game, p.Scenario, Step), limits)).ToList())
            .WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData(DemoScenario.Success, RunOutcome.Success, CleanupState.Confirmed, ReasonCodes.Completed)]
    [InlineData(DemoScenario.NoEvidence, RunOutcome.Indeterminate, CleanupState.Confirmed, ReasonCodes.NoCompletionEvidence)]
    [InlineData(DemoScenario.Failure, RunOutcome.Failed, CleanupState.Confirmed, ReasonCodes.ReportedFailure)]
    [InlineData(DemoScenario.Stall, RunOutcome.Indeterminate, CleanupState.Confirmed, ReasonCodes.Stalled)]
    [InlineData(DemoScenario.CleanupUnconfirmed, RunOutcome.Success, CleanupState.Unconfirmed, ReasonCodes.Completed)]
    public async Task EachDemoScenarioProducesItsDocumentedResult(DemoScenario scenario, RunOutcome outcome, CleanupState cleanup, string reason)
    {
        var run = (await RunAsync((GameId.Genshin, scenario))).Runs.Single();
        Assert.Equal(outcome, run.Outcome);
        Assert.Equal(cleanup, run.Cleanup);
        Assert.Equal(reason, run.ReasonCode);
        Assert.True(run.IsSimulation);
    }

    [Fact]
    public async Task DemoNeverStartsAProcess()
    {
        var supervisor = new RefusingProcessSupervisor();
        var ex = await Assert.ThrowsAsync<EngineStartException>(() =>
            supervisor.StartAsync(new ProcessLaunchSpec("C:\\x.exe", [], "C:\\"), Guid.NewGuid(), default));
        Assert.True(ex.NothingStarted);
    }

    [Fact]
    public async Task EveryDemoEventIsLabelledAsSimulation()
    {
        await RunAsync((GameId.StarRail, DemoScenario.Success), (GameId.ZenlessZoneZero, DemoScenario.Failure));
        Assert.NotEmpty(_journal.EventsSnapshot());
        Assert.All(_journal.EventsSnapshot(), e => Assert.StartsWith(RunPresentation.SimulationLabel, e.Message));
    }

    public static IEnumerable<object?[]> AllOutcomes() =>
        Enum.GetValues<RunOutcome>().Select(o => new object?[] { o }).Append([null]);

    [Theory]
    [MemberData(nameof(AllOutcomes))]
    public void SimulatedResultsAreAlwaysLabelled(RunOutcome? outcome)
    {
        Assert.StartsWith(RunPresentation.SimulationLabel, RunPresentation.Outcome(outcome, isSimulation: true));
        foreach (var phase in Enum.GetValues<RunPhase>())
        foreach (var cleanup in Enum.GetValues<CleanupState>())
            Assert.StartsWith(RunPresentation.SimulationLabel,
                RunPresentation.Summary(GameId.Genshin, phase, outcome, cleanup, ReasonCodes.Completed, isSimulation: true));
    }

    [Fact]
    public void RealResultsAreNotLabelledAsSimulation() =>
        Assert.DoesNotContain(RunPresentation.SimulationLabel,
            RunPresentation.Summary(GameId.Genshin, RunPhase.Finished, RunOutcome.Success, CleanupState.Confirmed, ReasonCodes.Completed, isSimulation: false));

    [Fact]
    public void EveryReasonCodeHasUserFacingWording()
    {
        var codes = typeof(ReasonCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.True(codes.Count >= 15);
        Assert.All(codes, code =>
        {
            var text = RunPresentation.Reason(code);
            Assert.NotEqual("其他原因", text);
            Assert.DoesNotContain(code, text);
        });
    }

    [Fact]
    public void BlockedQueueExplainsWhy() =>
        Assert.Equal("队列已阻断：有待确认的未清理运行", RunPresentation.QueueStatusText(
            new QueueResult(Guid.NewGuid(), QueueStatus.Blocked, [], [GameId.Genshin], false, ReasonCodes.UnresolvedRuns)));
}
