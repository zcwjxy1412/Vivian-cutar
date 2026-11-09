using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Engines.BetterGi;
using VIvian.Tests.Execution;
using Xunit;

namespace VIvian.Tests.Engines.BetterGi;

public sealed class BetterGiArgumentTests
{
    [Fact]
    public void AllowlistedRequestsProduceTheExactPinnedArgumentVectors()
    {
        Assert.Equal(["startOneDragon"], BetterGiArguments.OneDragonSelected().Request!.ToArguments());
        Assert.Equal(["startOneDragon", "默认配置"], BetterGiArguments.OneDragon("默认配置").Request!.ToArguments());
        Assert.Equal(["startOneDragon", "日常 二号"], BetterGiArguments.OneDragon("日常 二号").Request!.ToArguments());
        Assert.Equal(["--startGroups", "采集", "锄地"], BetterGiArguments.StartGroups(["采集", "锄地"]).Request!.ToArguments());
        Assert.Equal(BetterGiTaskKind.OneDragonSelected, BetterGiArguments.OneDragon(null).Request!.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" 默认配置")]
    [InlineData("默认配置 ")]
    [InlineData("--instance")]
    [InlineData("-x")]
    [InlineData("..\\config")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a\"b")]
    [InlineData("{Name}")]
    [InlineData("a%PATH%")]
    [InlineData("line\nbreak")]
    [InlineData("tab\there")]
    [InlineData("zero​width")]
    [InlineData("CON")]
    [InlineData("nul.json")]
    [InlineData("name.")]
    [InlineData("startOneDragon")]
    [InlineData("--startGroups")]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345")]
    public void InvalidNamesAreRejectedBeforeAnythingStarts(string name)
    {
        var result = BetterGiArguments.OneDragon(name);
        Assert.False(result.IsValid);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.False(BetterGiArguments.StartGroups([name]).IsValid);
    }

    [Fact]
    public void GroupListsAreBoundedAndUnique()
    {
        Assert.False(BetterGiArguments.StartGroups([]).IsValid);
        Assert.False(BetterGiArguments.StartGroups(null).IsValid);
        Assert.False(BetterGiArguments.StartGroups(["a", "a"]).IsValid);
        Assert.False(BetterGiArguments.StartGroups(Enumerable.Range(0, 17).Select(i => "g" + i).ToList()).IsValid);
        Assert.True(BetterGiArguments.StartGroups(Enumerable.Range(0, 16).Select(i => "g" + i).ToList()).IsValid);
    }

    [Fact]
    public void OnlyTheOneDragonFlowHasAKnownCompletionContract()
    {
        Assert.True(BetterGiArguments.OneDragonSelected().Request!.HasKnownCompletionContract);
        Assert.True(BetterGiArguments.OneDragon("x").Request!.HasKnownCompletionContract);
        Assert.False(BetterGiArguments.StartGroups(["x"]).Request!.HasKnownCompletionContract);
    }

    [Fact]
    public void PinnedSourceFactsAreRecordedNotVerified()
    {
        Assert.Equal(553, BetterGiContract.ElevationRestartExitCode); // 'r'+'u'+'n'+'a'+'s'; research doc said 559
        Assert.Equal(0xFFFF, BetterGiContract.SingleInstanceOrIntegrationExitCode);
        Assert.StartsWith("0af68c2", BetterGiContract.PinnedCommit);
        Assert.False(BetterGiContract.LaunchContractVerified);
        Assert.False(BetterGiContract.CompletionSignalsVerified);
#if !(M4_SCREEN_TEST || M4_MANUAL_TEST || M5_DAILY_TEST)
        Assert.False(RealExecutionPolicy.BuildPermitsRealExecution);
#endif
        // Closed in every build, including the M4-F1 manual-test build: only March7th screen_test may run.
        Assert.False(BetterGiAdapter.ExecutionGateOpen);
        Assert.Equal("better-genshin-impact20261105.log", BetterGiContract.LogFileName(new DateOnly(2026, 11, 5)));
    }
}

public sealed class BetterGiLogParserTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UtcNow;

    [Fact]
    public void RecordsSpanMultipleLinesAndOnlyTheMessageLineIsClassified()
    {
        var parser = new BetterGiLogRecordParser();
        var lines = BgiLog.Record(7, Start, "UnHandle Exception", "ERR", exception: "System.Exception: 一条龙和配置组任务结束\n   at X")
            .Concat(BgiLog.Record(7, Start, BetterGiMarkers.CompletedLine)).ToList();
        var records = lines.Select(parser.Feed).Where(r => r is not null).Cast<BetterGiLogRecord>().ToList();
        records.Add(parser.Flush()!);
        Assert.Equal(2, records.Count);
        Assert.Equal("UnHandle Exception", records[0].Message);
        Assert.Contains("一条龙和配置组任务结束", records[0].Body);
        Assert.Equal(BetterGiMarkerKind.Failure, BetterGiMarkers.Classify(records[0]).Kind);
        Assert.Equal(BetterGiMarkerKind.Completed, BetterGiMarkers.Classify(records[1]).Kind);
        Assert.Equal(7, records[1].ProcessId);
        Assert.Equal("Primary", records[1].InstanceType);
    }

    [Fact]
    public void LinesBeforeTheFirstHeaderAreDiscarded()
    {
        var parser = new BetterGiLogRecordParser();
        Assert.Null(parser.Feed(BetterGiMarkers.CompletedLine));
        Assert.Null(parser.Feed(""));
        Assert.Null(parser.Flush());
    }

    [Theory]
    [InlineData("启用一条龙配置：默认配置", "INF", BetterGiMarkerKind.Activated)]
    [InlineData("参数指定的一条龙配置：默认配置", "INF", BetterGiMarkerKind.ArgumentConfig)]
    [InlineData("未找到，请检查。", "WRN", BetterGiMarkerKind.ConfigNotFound)]
    [InlineData("一条龙任务执行: 3/5", "INF", BetterGiMarkerKind.Progress)]
    [InlineData("配置组任务执行: 1/2", "INF", BetterGiMarkerKind.Progress)]
    [InlineData("一条龙和配置组任务结束", "INF", BetterGiMarkerKind.Completed)]
    [InlineData("一条龙和配置组任务结束", "WRN", BetterGiMarkerKind.Failure)]
    [InlineData("任务被取消，退出执行", "INF", BetterGiMarkerKind.Cancelled)]
    [InlineData("一条龙在启动阶段被取消", "INF", BetterGiMarkerKind.Cancelled)]
    [InlineData("任务中断:\"任务被取消\"", "INF", BetterGiMarkerKind.Cancelled)]
    [InlineData("没有配置,退出执行!", "INF", BetterGiMarkerKind.NothingDone)]
    [InlineData("没有配置组任务,退出执行!", "INF", BetterGiMarkerKind.NothingDone)]
    [InlineData("执行配置组任务时失败", "DBG", BetterGiMarkerKind.Failure)]
    [InlineData("任务启动失败：当前存在正在运行中的独立任务，请不要重复执行任务！", "ERR", BetterGiMarkerKind.Failure)]
    [InlineData("UnHandle Exception [FATAL]", "ERR", BetterGiMarkerKind.Fatal)]
    [InlineData("anything", "FTL", BetterGiMarkerKind.Fatal)]
    [InlineData("某个子任务抛出的异常消息", "ERR", BetterGiMarkerKind.Failure)]
    [InlineData("前缀 一条龙和配置组任务结束", "INF", BetterGiMarkerKind.None)]
    [InlineData("一条龙和配置组任务结束了", "INF", BetterGiMarkerKind.None)]
    public void MarkersMatchThePinnedStringsExactly(string message, string level, BetterGiMarkerKind expected)
    {
        var parser = new BetterGiLogRecordParser();
        foreach (var line in BgiLog.Record(1, Start, message, level)) parser.Feed(line);
        Assert.Equal(expected, BetterGiMarkers.Classify(parser.Flush()!).Kind);
    }

    [Fact]
    public void AttributionRequiresPrimaryOwnedPidAndARecentInstanceStart()
    {
        var root = DateTimeOffset.UtcNow;
        var attribution = new BetterGiAttribution(100, root, TimeSpan.FromSeconds(2));
        BetterGiLogRecord Rec(int pid, DateTimeOffset t, string type = "Primary") =>
            new("12:00:00.000", "INF", type, 1, pid, t.ToUnixTimeMilliseconds(), "", "m", "m");
        Assert.True(attribution.Owns(Rec(100, root.AddMilliseconds(300))));
        Assert.False(attribution.Owns(Rec(101, root.AddMilliseconds(300))));               // another process
        Assert.False(attribution.Owns(Rec(100, root.AddHours(-3))));                       // same PID, earlier process
        Assert.False(attribution.Owns(Rec(100, root.AddMilliseconds(300), "ChildSession"))); // not the primary instance
    }

    [Theory]
    [InlineData("0.66.0+0af68c28f6b0e714d5304625ce644736d508022e", "0af68c28f6b0e714d5304625ce644736d508022e")]
    [InlineData("0.66.0+0AF68C2", "0af68c2")]
    [InlineData("0.66.0", null)]
    [InlineData("0.66.0+local", null)]
    [InlineData(null, null)]
    public void SourceRevisionIsReadFromTheInformationalVersion(string? product, string? expected) =>
        Assert.Equal(expected, BetterGiInstallationInspector.ParseRevision(product));

    [Fact]
    public void RevisionMatchIsOnlyPendingAndMismatchWarns()
    {
        Assert.Equal(CheckState.Pending, BetterGiInstallationInspector.ClassifyRevision("0af68c2").State);
        Assert.Equal(CheckState.Warning, BetterGiInstallationInspector.ClassifyRevision("1234567").State);
        Assert.Equal(CheckState.NotApplicable, BetterGiInstallationInspector.ClassifyRevision(null).State);
    }
}

public sealed class BetterGiPreflightTests
{
    private static readonly RunContext Ctx = new(Guid.NewGuid(), Guid.NewGuid(), GameId.Genshin, DateTimeOffset.UtcNow);

    [Fact]
    public async Task MissingInstallationBlocksOnlyThisGame()
    {
        await using var f = new BetterGiFixture(createInstall: false);
        var result = await f.Adapter().PreflightAsync(Ctx, default);
        Assert.False(result.CanStart);
        Assert.Equal(ReasonCodes.NotConfigured, result.ReasonCode);
        Assert.False(result.BlocksQueue);
        Assert.Equal(0, f.Supervisor.Calls);
    }

    [Fact]
    public async Task UnconfiguredPathBlocks()
    {
        var adapter = new BetterGiAdapter(() => null, BetterGiArguments.OneDragonSelected().Request!);
        var result = await adapter.PreflightAsync(Ctx, default);
        Assert.Equal(ReasonCodes.NotConfigured, result.ReasonCode);
    }

    [Fact]
    public async Task MissingIntegrationFoldersMakeTheLayoutUnsafe()
    {
        await using var f = new BetterGiFixture();
        Directory.Delete(Path.Combine(f.Root, "GameTask"));
        var report = await BetterGiInstallationInspector.InspectAsync(f.Root);
        // The empty test program has no version resource; the folder check still runs for an unknown version.
        Assert.Contains(report.Checks, c => c.Id == "bgi:GameTask" && c.State == CheckState.Failed);
        Assert.Equal(CompatibilityClass.UnsafeLayout, report.Compatibility);
        Assert.Equal("execution", report.Checks[^1].Id);
        var result = await f.Adapter().PreflightAsync(Ctx, default);
        Assert.False(result.CanStart);
    }

    [Fact]
    public async Task UnknownOrUnsupportedVersionsAreRefused()
    {
        await using var f = new BetterGiFixture();
        var unknown = await f.Adapter().PreflightAsync(Ctx, default); // test program has no version resource
        Assert.False(unknown.CanStart);
        Assert.Equal(ReasonCodes.PreflightError, unknown.ReasonCode);
        var unsupported = await f.Adapter(inspect: BetterGiFixture.Report(CompatibilityClass.UnsupportedVersion)).PreflightAsync(Ctx, default);
        Assert.False(unsupported.CanStart);
        Assert.Equal(ReasonCodes.PreflightError, unsupported.ReasonCode);
        Assert.Equal("synthetic", unsupported.Detail);
    }

    [Fact]
    public async Task AnyRunningBetterGiBlocksTheQueueAndIsNeverTouched()
    {
        await using var f = new BetterGiFixture();
        f.Probe.Running = 1;
        var result = await f.Adapter(inspect: BetterGiFixture.Report(CompatibilityClass.PendingValidation)).PreflightAsync(Ctx, default);
        Assert.Equal(ReasonCodes.ForeignProcess, result.ReasonCode);
        Assert.True(result.BlocksQueue);
    }

    [Fact]
    public async Task AValidInstallationIsStillRefusedWhileTheExecutionGateIsClosed()
    {
        await using var f = new BetterGiFixture();
        var adapter = f.Adapter(inspect: BetterGiFixture.Report(CompatibilityClass.PendingValidation));
        var result = await adapter.PreflightAsync(Ctx, default);
        Assert.False(result.CanStart);
        Assert.Equal(ReasonCodes.RealAdapterUnverified, result.ReasonCode);

        var ex = await Assert.ThrowsAsync<EngineStartException>(() => adapter.StartAsync(Ctx, f.Supervisor, default));
        Assert.True(ex.NothingStarted);
        Assert.Equal(ReasonCodes.RealAdapterUnverified, ex.ReasonCode);
        Assert.Equal(0, f.Supervisor.Calls);
        Assert.Equal(0, f.Logs.Opens);
        Assert.False(adapter.IsSimulation);
    }

    [Fact]
    public async Task TheSchedulerBlocksTheAdapterBeforePreflightOrLaunch()
    {
        await using var f = new BetterGiFixture();
        var result = await new QueueScheduler(new InMemoryJournal(), f.Supervisor)
            .RunAsync([new QueueItem(GameId.Genshin, f.Adapter(inspect: BetterGiFixture.Report(CompatibilityClass.PendingValidation)))]);
        Assert.Equal(QueueStatus.Blocked, result.Status);
        Assert.Equal(ReasonCodes.RealAdapterUnverified, result.BlockReason);
        Assert.Equal(0, f.Supervisor.Calls);
    }
}

public sealed class BetterGiLifecycleTests
{
    private static DateTimeOffset Own(FakeGroup g) => g.RootStartTime.AddMilliseconds(300);

    private static async Task<RunVerdict> FinishAsync(BetterGiFixture f, BetterGiRun run, FakeGroup group, IEnumerable<string> lines, int exitCode)
    {
        f.Logs.Write(lines);
        await Task.Delay(50);
        group.Exit(exitCode);
        return await run.WaitForVerdictAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
    }

    [Fact]
    public async Task LaunchUsesTheSupervisorWithAllowlistedArgumentsAfterTakingTheLogBaseline()
    {
        await using var f = new BetterGiFixture();
        var (run, _) = await f.LaunchAsync(f.Adapter(BetterGiArguments.OneDragon("默认配置").Request));
        await using var _run = run;
        Assert.True(f.LogOpenedBeforeLaunch);
        Assert.Equal(1, f.Supervisor.Calls);
        var spec = f.Supervisor.LastSpec!;
        Assert.Equal(Path.Combine(Path.GetFullPath(f.Root), "BetterGI.exe"), spec.FileName);
        Assert.Equal(["startOneDragon", "默认配置"], spec.Arguments);
        Assert.Equal(Path.GetFullPath(f.Root), spec.WorkingDirectory);
        Assert.Null(spec.Environment);
    }

    [Fact]
    public async Task CompleteAttributedEvidenceIsSuccessOnlyOnceTheContractIsVerified()
    {
        await using var f = new BetterGiFixture();
        var (run, group) = await f.LaunchAsync(f.Adapter(contractVerified: true));
        await using var _run = run;
        var verdict = await FinishAsync(f, run, group, BgiLog.SuccessfulRun(group.RootProcessId, Own(group)), 0);
        Assert.Equal(RunOutcome.Success, verdict.Outcome);
        Assert.Equal(CleanupState.Confirmed, await run.ReconcileAsync(default));
    }

    [Fact]
    public async Task ProductionContractNeverYieldsSuccessEvenWithPerfectLogs()
    {
        await using var f = new BetterGiFixture();
        var (run, group) = await f.LaunchAsync(f.Adapter());
        await using var _run = run;
        var verdict = await FinishAsync(f, run, group, BgiLog.SuccessfulRun(group.RootProcessId, Own(group)), 0);
        Assert.Equal(RunOutcome.Indeterminate, verdict.Outcome);
        Assert.Equal(ReasonCodes.RealAdapterUnverified, verdict.ReasonCode);
        Assert.All(verdict.Evidence, e => Assert.Equal(EvidenceKind.Note, e.Kind));
    }

    [Fact]
    public async Task HistoricalCompletionLinesNeverCount()
    {
        await using var f = new BetterGiFixture();
        var (run, group) = await f.LaunchAsync(f.Adapter(contractVerified: true));
        await using var _run = run;
        // A previous process with the same PID, and a different earlier process: both before our root existed.
        var old = group.RootStartTime.AddHours(-5);
        var lines = BgiLog.SuccessfulRun(group.RootProcessId, old).Concat(BgiLog.SuccessfulRun(9999, old));
        var verdict = await FinishAsync(f, run, group, lines, 0);
        Assert.Equal(RunOutcome.Indeterminate, verdict.Outcome);
        Assert.Equal(ReasonCodes.CleanupUnconfirmed, verdict.ReasonCode); // no attributable startup
        Assert.Equal(10, run.Evidence.IgnoredRecords);
        Assert.False(run.Evidence.ForeignActivitySeen);
        Assert.Equal(CleanupState.Unconfirmed, await run.ReconcileAsync(default));
    }

    [Theory]
    [InlineData(false, RunOutcome.Indeterminate)]
    [InlineData(true, RunOutcome.Success)]
    public async Task DailyRewardsRequireTheirOwnPostTaskVerification(bool observed, RunOutcome expected)
    {
        await using var f = new BetterGiFixture();
        var path = Path.Combine(f.Root, "User", "OneDragon", "日常.json");
        File.WriteAllText(path, """{ "Name": "日常", "TaskEnabledList": { "领取每日奖励": true }, "CompletionAction": "关闭软件" }""");
        var request = BetterGiArguments.OneDragon("日常").Request;
        var (run, group) = await f.LaunchAsync(f.Adapter(request, contractVerified: true));
        await using var owned = run;

        var lines = BgiLog.Record(group.RootProcessId, Own(group), "启用一条龙配置：日常")
            .Concat(BgiLog.Record(group.RootProcessId, Own(group), "一条龙任务执行: 1/1"));
        if (observed)
            lines = lines.Concat(BgiLog.Record(group.RootProcessId, Own(group), "检查每日奖励结果：今日奖励已领取",
                source: "BetterGenshinImpact.GameTask.Common.Job.CheckRewardsTask"));
        lines = lines.Concat(BgiLog.Record(group.RootProcessId, Own(group), "一条龙和配置组任务结束"));
        var verdict = await FinishAsync(f, run, group, lines, 0);
        Assert.Equal(expected, verdict.Outcome);
    }

    [Fact]
    public async Task MissingCompletionMarkerIsIndeterminate()
    {
        await using var f = new BetterGiFixture();
        var (run, group) = await f.LaunchAsync(f.Adapter(contractVerified: true));
        await using var _run = run;
        var lines = BgiLog.Record(group.RootProcessId, Own(group), BetterGiMarkers.ActivatedPrefix + "默认配置")
            .Concat(BgiLog.Record(group.RootProcessId, Own(group), "一条龙任务执行: 1/2"));
        var verdict = await FinishAsync(f, run, group, lines, 0);
        Assert.Equal(RunOutcome.Indeterminate, verdict.Outcome);
    }

    [Fact]
    public async Task ExitCodeZeroWithoutAnyLogIsIndeterminate()
    {
        await using var f = new BetterGiFixture();
        var (run, group) = await f.LaunchAsync(f.Adapter(contractVerified: true));
        await using var _run = run;
        var verdict = await FinishAsync(f, run, group, [], 0);
        Assert.Equal(RunOutcome.Indeterminate, verdict.Outcome);
    }

    [Theory]
    [InlineData("某个子任务抛出的异常消息", "ERR")]
    [InlineData("UnHandle Exception [FATAL]", "ERR")]
    [InlineData("执行配置组任务时失败", "DBG")]
    [InlineData("任务被取消，退出执行", "INF")]
    [InlineData("没有配置组任务,退出执行!", "INF")]
    public async Task ContradictoryMarkersOverrideCompletion(string message, string level)
    {
        await using var f = new BetterGiFixture();
        var (run, group) = await f.LaunchAsync(f.Adapter(contractVerified: true));
        await using var _run = run;
        var lines = BgiLog.Record(group.RootProcessId, Own(group), BetterGiMarkers.ActivatedPrefix + "默认配置")
            .Concat(BgiLog.Record(group.RootProcessId, Own(group), message, level))
            .Concat(BgiLog.Record(group.RootProcessId, Own(group), BetterGiMarkers.CompletedLine));
        var verdict = await FinishAsync(f, run, group, lines, 0);
        Assert.NotEqual(RunOutcome.Success, verdict.Outcome);
        Assert.Equal(RunOutcome.Indeterminate, verdict.Outcome);
    }

    [Fact]
    public async Task FatalWithoutCompletionIsFailed()
    {
        await using var f = new BetterGiFixture();
        var (run, group) = await f.LaunchAsync(f.Adapter(contractVerified: true));
        await using var _run = run;
        var lines = BgiLog.Record(group.RootProcessId, Own(group), BetterGiMarkers.ActivatedPrefix + "默认配置")
            .Concat(BgiLog.Record(group.RootProcessId, Own(group), "UnHandle Exception [FATAL]", "ERR"));
        var verdict = await FinishAsync(f, run, group, lines, 1);
        Assert.Equal(RunOutcome.Failed, verdict.Outcome);
    }

    [Fact]
    public async Task NamedConfigurationFallbackOrMismatchIsNeverSuccess()
    {
        await using var f = new BetterGiFixture();
        var adapter = f.Adapter(BetterGiArguments.OneDragon("日常").Request, contractVerified: true);
        var (run, group) = await f.LaunchAsync(adapter);
        await using var _run = run;
        var lines = BgiLog.Record(group.RootProcessId, Own(group), BetterGiMarkers.ArgumentConfigPrefix + "日常")
            .Concat(BgiLog.Record(group.RootProcessId, Own(group), BetterGiMarkers.ConfigNotFoundLine, "WRN"))
            .Concat(BgiLog.SuccessfulRun(group.RootProcessId, Own(group), "默认配置"));
        var verdict = await FinishAsync(f, run, group, lines, 0);
        Assert.NotEqual(RunOutcome.Success, verdict.Outcome);
        Assert.Contains(verdict.Evidence, e => e.Kind == EvidenceKind.FailureReported);
    }

    [Fact]
    public async Task NamedConfigurationThatMatchesIsAccepted()
    {
        await using var f = new BetterGiFixture();
        var (run, group) = await f.LaunchAsync(f.Adapter(BetterGiArguments.OneDragon("日常").Request, contractVerified: true));
        await using var _run = run;
        var verdict = await FinishAsync(f, run, group, BgiLog.SuccessfulRun(group.RootProcessId, Own(group), "日常"), 0);
        Assert.Equal(RunOutcome.Success, verdict.Outcome);
    }

    [Fact]
    public async Task CompletionBeforeActivationOrRepeatedRunsAreNotSuccess()
    {
        await using var f = new BetterGiFixture();
        var (run, group) = await f.LaunchAsync(f.Adapter(contractVerified: true));
        await using var _run = run;
        var lines = BgiLog.Record(group.RootProcessId, Own(group), BetterGiMarkers.CompletedLine)
            .Concat(BgiLog.SuccessfulRun(group.RootProcessId, Own(group)));
        var verdict = await FinishAsync(f, run, group, lines, 0);
        Assert.Equal(RunOutcome.Indeterminate, verdict.Outcome);
    }

    [Fact]
    public async Task ScriptGroupRunsAreNeverSuccess()
    {
        await using var f = new BetterGiFixture();
        var (run, group) = await f.LaunchAsync(f.Adapter(BetterGiArguments.StartGroups(["采集"]).Request, contractVerified: true));
        await using var _run = run;
        var verdict = await FinishAsync(f, run, group, BgiLog.SuccessfulRun(group.RootProcessId, Own(group)), 0);
        Assert.Equal(RunOutcome.Indeterminate, verdict.Outcome);
    }

    [Fact]
    public async Task ElevatedSelfRestartIsIndeterminateAndCleanupStaysUnconfirmed()
    {
        await using var f = new BetterGiFixture();
        var (run, group) = await f.LaunchAsync(f.Adapter(contractVerified: true));
        // The elevated copy (new PID, outside the job) does the whole run and logs completion.
        var verdict = await FinishAsync(f, run, group, BgiLog.SuccessfulRun(5555, Own(group)), BetterGiContract.ElevationRestartExitCode);
        Assert.Equal(RunOutcome.Indeterminate, verdict.Outcome);
        Assert.True(run.Detached);
        Assert.True(run.Evidence.ForeignActivitySeen);
        Assert.Equal(0, group.ActiveProcessCount); // the job is empty …
        Assert.Equal(CleanupState.Unconfirmed, await run.ReconcileAsync(default)); // … but that proves nothing
        var stop = await run.RequestStopAsync(StopMode.Emergency, default);
        Assert.Equal(CleanupState.Unconfirmed, stop.Cleanup);
        await run.DisposeAsync();
        Assert.True(f.Registry.IsBusy); // the installation stays locked for this session
    }

    [Fact]
    public async Task ForeignBetterGiActivityDuringTheRunKeepsCleanupUnconfirmed()
    {
        await using var f = new BetterGiFixture();
        var (run, group) = await f.LaunchAsync(f.Adapter(contractVerified: true));
        await using var _run = run;
        var lines = BgiLog.SuccessfulRun(group.RootProcessId, Own(group)).Concat(BgiLog.Record(7777, Own(group), "其他实例"));
        var verdict = await FinishAsync(f, run, group, lines, 0);
        Assert.Equal(RunOutcome.Success, verdict.Outcome); // our own process's evidence is complete
        Assert.Equal(CleanupState.Unconfirmed, await run.ReconcileAsync(default)); // but something we do not own is running
    }

    [Fact]
    public async Task UserCancellationTerminatesOnlyTheOwnedGroupAndIsNotReportedAsFailure()
    {
        await using var f = new BetterGiFixture();
        var (run, group) = await f.LaunchAsync(f.Adapter(contractVerified: true));
        f.Logs.Write(BgiLog.Record(group.RootProcessId, Own(group), BetterGiMarkers.ActivatedPrefix + "默认配置"));
        var stop = await run.RequestStopAsync(StopMode.Emergency, default);
        Assert.Equal(CleanupState.Confirmed, stop.Cleanup);
        Assert.Equal(1, group.TerminateCalls);
        var verdict = await run.WaitForVerdictAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        Assert.Equal(RunOutcome.Indeterminate, verdict.Outcome); // killed: exit code is not engine evidence
        Assert.Equal(CleanupState.Confirmed, await run.ReconcileAsync(default));
        await run.DisposeAsync();
        Assert.True(group.Disposed);
        Assert.False(f.Registry.IsBusy);
    }

    [Fact]
    public async Task TimeoutPathSendsNothingGracefullyThenTerminatesOwnedProcesses()
    {
        await using var f = new BetterGiFixture();
        var (run, group) = await f.LaunchAsync(f.Adapter(contractVerified: true));
        await using var _run = run;
        var graceful = await run.RequestStopAsync(StopMode.Graceful, default);
        Assert.Equal(CleanupState.Pending, graceful.Cleanup);
        Assert.Equal("graceful_unavailable", graceful.ReasonCode);
        Assert.Equal(0, group.TerminateCalls);
        Assert.False(run.WaitForVerdictAsync(default).IsCompleted); // still running: nothing was sent to it

        var emergency = await run.RequestStopAsync(StopMode.Emergency, default);
        Assert.Equal(CleanupState.Confirmed, emergency.Cleanup);
        var verdict = await run.WaitForVerdictAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        Assert.NotEqual(RunOutcome.Success, verdict.Outcome);
        Assert.NotEqual(RunOutcome.Failed, verdict.Outcome);
    }

    [Fact]
    public async Task TerminationThatCannotBeConfirmedIsUnconfirmed()
    {
        await using var f = new BetterGiFixture();
        var (run, group) = await f.LaunchAsync(f.Adapter());
        group.TerminateResult = CleanupState.Unconfirmed;
        Assert.Equal(CleanupState.Unconfirmed, (await run.RequestStopAsync(StopMode.Emergency, default)).Cleanup);
        Assert.Equal(CleanupState.Unconfirmed, await run.ReconcileAsync(default));
        group.TerminateThrows = true;
        Assert.Equal(CleanupState.Unconfirmed, (await run.RequestStopAsync(StopMode.Emergency, default)).Cleanup);
        await run.DisposeAsync();
        Assert.True(f.Registry.IsBusy);
    }

    [Fact]
    public async Task ASecondLaunchOnTheSameInstallationIsRefusedWithoutStartingAnything()
    {
        await using var f = new BetterGiFixture();
        var adapter = f.Adapter();
        var (run, group) = await f.LaunchAsync(adapter);
        var other = f.Context with { RunId = Guid.NewGuid() };
        var ex = await Assert.ThrowsAsync<EngineStartException>(() => adapter.LaunchAsync(other, f.Supervisor, default));
        Assert.True(ex.NothingStarted);
        Assert.Equal(1, f.Supervisor.Calls);
        var pre = await f.Adapter(inspect: BetterGiFixture.Report(CompatibilityClass.PendingValidation)).PreflightAsync(other, default);
        Assert.Equal(ReasonCodes.ForeignProcess, pre.ReasonCode);

        await FinishAsync(f, run, group, BgiLog.SuccessfulRun(group.RootProcessId, Own(group)), 0);
        Assert.Equal(CleanupState.Confirmed, await run.ReconcileAsync(default));
        await run.DisposeAsync();
        Assert.False(f.Registry.IsBusy);
    }

    [Fact]
    public async Task LaunchFailuresBeforeTheSupervisorReportNothingStarted()
    {
        await using var f = new BetterGiFixture();
        f.Logs.Throw = true;
        var ex = await Assert.ThrowsAsync<EngineStartException>(() => f.Adapter().LaunchAsync(f.Context, f.Supervisor, default));
        Assert.True(ex.NothingStarted);
        Assert.Equal(0, f.Supervisor.Calls);
        Assert.False(f.Registry.IsBusy);

        await using var missing = new BetterGiFixture(createInstall: false);
        var ex2 = await Assert.ThrowsAsync<EngineStartException>(() => missing.Adapter().LaunchAsync(missing.Context, missing.Supervisor, default));
        Assert.True(ex2.NothingStarted);
        Assert.Equal(0, missing.Supervisor.Calls);
    }

    [Fact]
    public async Task EventsCarryOnlyThisRunId()
    {
        await using var f = new BetterGiFixture();
        var (run, group) = await f.LaunchAsync(f.Adapter(contractVerified: true));
        await using var _run = run;
        await FinishAsync(f, run, group, BgiLog.SuccessfulRun(group.RootProcessId, Own(group)), 0);
        var events = new List<RunEvent>();
        await foreach (var e in run.Events) events.Add(e);
        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.Equal(f.Context.RunId, e.RunId));
        Assert.Contains(events, e => e.Kind == RunEventKind.Evidence);
    }
}
