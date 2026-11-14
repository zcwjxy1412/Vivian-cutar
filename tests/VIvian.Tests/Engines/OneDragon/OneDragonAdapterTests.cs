using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Engines;
using VIvian.Infrastructure.Engines.OneDragon;
using VIvian.Tests.Execution;
using Xunit;

namespace VIvian.Tests.Engines.OneDragon;

/// <summary>A launcher process group that never runs anything. Output, exit and termination are scripted.</summary>
internal sealed class FakeLauncherGroup(Guid runId) : ISupervisedProcessGroup
{
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Channel<string> Output { get; } = Channel.CreateUnbounded<string>();
    public Guid RunId { get; } = runId;
    public int RootProcessId => 5151;
    public DateTimeOffset RootStartTime { get; } = DateTimeOffset.UtcNow;
    public IAsyncEnumerable<string> OutputLines => Output.Reader.ReadAllAsync();
    public int ActiveProcessCount { get; set; } = 1;
    public CleanupState TerminateResult { get; set; } = CleanupState.Confirmed;
    public int TerminateCalls;
    public bool Disposed;
    public int DisposeCalls;
    public bool FailDispose;

    public void Write(params string[] lines)
    {
        foreach (var line in lines) Output.Writer.TryWrite(line);
    }

    /// <summary>The launcher exits; <paramref name="workerStillRunning"/> keeps a descendant inside the job.</summary>
    public void Exit(int code, bool workerStillRunning = false)
    {
        ActiveProcessCount = workerStillRunning ? 1 : 0;
        Output.Writer.TryComplete();
        _exit.TrySetResult(code);
    }

    public Task<int> WaitForRootExitAsync(CancellationToken ct) => _exit.Task.WaitAsync(ct);
    public Task<bool> SendInputLineAsync(string line, CancellationToken ct) => Task.FromResult(false);
    public bool OwnsProcess(SafeProcessHandle process) => false;

    public Task<CleanupState> TerminateAllAsync(TimeSpan timeout, CancellationToken ct)
    {
        Interlocked.Increment(ref TerminateCalls);
        if (TerminateResult == CleanupState.Confirmed)
        {
            ActiveProcessCount = 0;
            Output.Writer.TryComplete();
            _exit.TrySetResult(1);
        }
        return Task.FromResult(TerminateResult);
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref DisposeCalls);
        Disposed = true;
        if (FailDispose) throw new IOException("Synthetic supervisor disposal failure");
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeLauncherSupervisor : IProcessSupervisor
{
    public int Calls;
    public ProcessLaunchSpec? LastSpec;
    public FakeLauncherGroup? Group;

    public Task<ISupervisedProcessGroup> StartAsync(ProcessLaunchSpec spec, Guid runId, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        LastSpec = spec;
        Group = new FakeLauncherGroup(runId);
        return Task.FromResult<ISupervisedProcessGroup>(Group);
    }
}

internal sealed class FakeOdProbe : IOneDragonProcessProbe
{
    public int Running { get; set; }
    public int CountRunning(string programPath) => Running;
}

/// <summary>Synthetic console lines in the pinned OneDragon format. No real log content is used.</summary>
internal static class OdOut
{
    public static string Line(string message, string level = "INFO", string source = "runtime_launcher.py 27") =>
        $"[12:00:00.123] [{source}] [{level}]: {message}";

    public static string[] SuccessfulRun(string? instances = null) =>
    [
        Line(OneDragonMarkers.RuntimeStartedPrefix + "v2.6.0"),
        Line(OneDragonMarkers.AutoUpdateSkippedLine),
        .. instances is null ? Array.Empty<string>() : [Line(OneDragonMarkers.InstancesPrefix + instances, source: "application_launcher.py 52")],
        Line("指令[ 每日签到 ] 执行成功 返回状态 成功", source: "operation.py 700"),
        Line("指令[ 一条龙 ] 执行成功 返回状态 成功", source: "operation.py 700"),
    ];
}

internal sealed class OneDragonFixture : IAsyncDisposable
{
    public string Root { get; }
    public FakeLauncherSupervisor Supervisor { get; } = new();
    public FakeOdProbe Probe { get; } = new();
    public OneDragonRunRegistry Registry { get; } = new();
    public RunContext Context { get; } = new(Guid.NewGuid(), Guid.NewGuid(), GameId.ZenlessZoneZero, DateTimeOffset.UtcNow);

    public OneDragonFixture(bool createInstall = true, bool runtimeLauncher = true, string folderPrefix = "vivian-od-")
    {
        Root = Path.Combine(Path.GetTempPath(), folderPrefix + Guid.NewGuid().ToString("N"));
        if (!createInstall) return;
        Directory.CreateDirectory(Root);
        File.WriteAllBytes(Path.Combine(Root, runtimeLauncher ? OneDragonContract.EntryProgram : OneDragonContract.RawLauncherProgram), [0x4D, 0x5A]);
        Directory.CreateDirectory(Path.Combine(Root, "src"));
        Directory.CreateDirectory(Path.Combine(Root, ".runtime"));
    }

    public static Func<string?, CancellationToken, Task<EngineReadinessReport>> Report(CompatibilityClass compatibility) =>
        (root, _) => Task.FromResult(new EngineReadinessReport(GameId.ZenlessZoneZero, "ZenlessZoneZero-OneDragon", "v2.6.0", compatibility,
            "2.6.0", root is null ? null : Path.Combine(root, OneDragonContract.EntryProgram), null,
            compatibility is CompatibilityClass.PendingValidation
                ? []
                : [new ReadinessCheck("synthetic", "synthetic", CheckState.Failed, EngineErrorCategory.UnsupportedVersion, "synthetic")],
            DateTimeOffset.UtcNow));

    public OneDragonAdapter Adapter(OneDragonTaskRequest? request = null, bool? contractVerified = null,
        Func<string?, CancellationToken, Task<EngineReadinessReport>>? inspect = null) =>
        new(() => Root, request ?? OneDragonArguments.OneDragon().Request!, Probe, Registry,
            new OneDragonRunOptions
            {
                TerminateTimeout = TimeSpan.FromSeconds(1),
                OutputDrainLimit = TimeSpan.FromSeconds(1),
                CompletionContractVerifiedOverride = contractVerified,
            }, TimeProvider.System, inspect);

    public async Task<(OneDragonRun Run, FakeLauncherGroup Group)> LaunchAsync(OneDragonAdapter adapter)
    {
        var run = (OneDragonRun)await adapter.LaunchAsync(Context, Supervisor, CancellationToken.None);
        return (run, Supervisor.Group!);
    }

    public ValueTask DisposeAsync()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        return ValueTask.CompletedTask;
    }
}

public sealed class OneDragonArgumentTests
{
    [Fact]
    public void AllowlistedRequestsProduceTheExactPinnedArgumentVectors()
    {
        Assert.Equal(["-o"], OneDragonArguments.OneDragon().Request!.ToArguments());
        Assert.Equal(["-o", "-c"], OneDragonArguments.OneDragon(closeGame: true).Request!.ToArguments());
        Assert.Equal(["-o", "-c", "-i", "1,2"], OneDragonArguments.OneDragon(true, [1, 2]).Request!.ToArguments());
        Assert.Equal(["-o", "-i", "3"], OneDragonArguments.Parse(false, "3").Request!.ToArguments());
    }

    [Theory]
    [InlineData("1, 2")]
    [InlineData("-1")]
    [InlineData("1,,2")]
    [InlineData("1,1")]
    [InlineData("100")]
    [InlineData("a")]
    [InlineData("1;2")]
    [InlineData("１")]
    [InlineData("1,2,3,4,5,6,7,8,9")]
    [InlineData("1 --shutdown")]
    public void InvalidInstanceSelectionsAreRejected(string text)
    {
        var result = OneDragonArguments.Parse(false, text);
        Assert.False(result.IsValid);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void PowerActionsGuiModeAndQueriesAreNeverProduced()
    {
        foreach (var request in new[] { OneDragonArguments.OneDragon().Request!, OneDragonArguments.OneDragon(true, [0, 99]).Request! })
        {
            var args = request.ToArguments();
            Assert.Equal("-o", args[0]);
            Assert.DoesNotContain(args, a => a is "-s" or "--shutdown" or "--hibernate" or "--sleep" or "-v" or "--version" or "-h" or "--help");
        }
    }

    [Fact]
    public void PinnedFactsAreRecordedNotVerified()
    {
        Assert.StartsWith("5f2e473", OneDragonContract.PinnedCommit);
        Assert.False(OneDragonContract.LaunchContractVerified);
        Assert.False(OneDragonContract.CompletionSignalsVerified);
        Assert.False(OneDragonAdapter.ExecutionGateOpen);
        Assert.Equal("utf-8", OneDragonContract.Environment["PYTHONIOENCODING"]);
        Assert.False(OneDragonContract.Environment.ContainsKey("PYTHONUTF8"));
    }
}

public sealed class OneDragonMarkerTests
{
    [Theory]
    [InlineData("[12:00:00.123] [runtime_launcher.py 27] [INFO]: OneDragon 集成启动器 v2.6.0", OneDragonMarkerKind.RuntimeStarted)]
    [InlineData("[12:00:00.123] [runtime_launcher.py 36] [INFO]: 未开启代码自动更新，跳过", OneDragonMarkerKind.AutoUpdateSkipped)]
    [InlineData("[12:00:00.123] [runtime_launcher.py 55] [INFO]: 正在检查代码更新...", OneDragonMarkerKind.AutoUpdateAttempted)]
    [InlineData("[12:00:00.123] [runtime_launcher.py 55] [INFO]: 首次运行，正在同步代码仓库...", OneDragonMarkerKind.AutoUpdateAttempted)]
    [InlineData("[12:00:00.123] [application_launcher.py 52] [INFO]: 指定运行实例: [1, 2]", OneDragonMarkerKind.InstancesSelected)]
    [InlineData("[12:00:00.123] [operation.py 700] [INFO]: 指令[ 一条龙 ] 执行成功 返回状态 成功", OneDragonMarkerKind.Completed)]
    [InlineData("[12:00:00.123] [operation.py 702] [ERROR]: 指令[ 一条龙 ] 执行失败 返回状态 失败", OneDragonMarkerKind.Failure)]
    [InlineData("[12:00:00.123] [operation.py 700] [WARNING]: 指令[ 一条龙 ] 执行成功", OneDragonMarkerKind.Failure)]
    [InlineData("[12:00:00.123] [application_launcher.py 68] [ERROR]: 未找到有效的实例", OneDragonMarkerKind.Failure)]
    [InlineData("[12:00:00.123] [x.py 1] [CRITICAL]: boom", OneDragonMarkerKind.Failure)]
    [InlineData("Traceback (most recent call last):", OneDragonMarkerKind.Failure)]
    [InlineData("错误：参数 --close-game, --shutdown, --hibernate, --sleep, --instance 只能在指定 --onedragon 时使用", OneDragonMarkerKind.Failure)]
    [InlineData("指令[ 一条龙 ] 执行成功", OneDragonMarkerKind.None)]
    [InlineData("[12:00:00.123] [operation.py 700] [INFO]: 指令[ 每日签到 ] 执行成功 返回状态 成功", OneDragonMarkerKind.None)]
    [InlineData("    raise ValueError('指令[ 一条龙 ] 执行成功')", OneDragonMarkerKind.None)]
    public void MarkersMatchThePinnedFormatExactly(string line, OneDragonMarkerKind expected) =>
        Assert.Equal(expected, OneDragonMarkers.Classify(line).Kind);
}

public sealed class OneDragonPreflightTests
{
    private static readonly RunContext Ctx = new(Guid.NewGuid(), Guid.NewGuid(), GameId.ZenlessZoneZero, DateTimeOffset.UtcNow);

    [Fact]
    public async Task MissingRuntimeBlocksOnlyThisGame()
    {
        await using var f = new OneDragonFixture(createInstall: false);
        var result = await f.Adapter().PreflightAsync(Ctx, default);
        Assert.False(result.CanStart);
        Assert.Equal(ReasonCodes.NotConfigured, result.ReasonCode);
        Assert.False(result.BlocksQueue);
    }

    [Fact]
    public async Task RawLauncherOnlyPackageIsUnsupported()
    {
        await using var f = new OneDragonFixture(runtimeLauncher: false);
        var report = await OneDragonInstallationInspector.InspectAsync(f.Root);
        Assert.Contains(report.Checks, c => c.Id == "od:entry" && c.State == CheckState.Failed);
        Assert.Equal(CompatibilityClass.UnsafeLayout, report.Compatibility);
        Assert.False((await f.Adapter().PreflightAsync(Ctx, default)).CanStart);
        var ex = await Assert.ThrowsAsync<EngineStartException>(() => f.Adapter().LaunchAsync(f.Context, f.Supervisor, default));
        Assert.True(ex.NothingStarted);
        Assert.Equal(0, f.Supervisor.Calls);
    }

    [Fact]
    public async Task MissingSrcFolderIsUnsupported()
    {
        await using var f = new OneDragonFixture();
        Directory.Delete(Path.Combine(f.Root, "src"));
        var report = await OneDragonInstallationInspector.InspectAsync(f.Root);
        Assert.Contains(report.Checks, c => c.Id == "od:src" && c.State == CheckState.Failed);
        Assert.Equal("execution", report.Checks[^1].Id);
    }

    [Theory]
    [InlineData(@"D:\OneDragon", true)]
    [InlineData(@"D:\Games\ZZZ-OneDragon_2", true)]
    [InlineData(@"D:\One Dragon", false)]
    [InlineData(@"D:\游戏\OneDragon", false)]
    [InlineData(@"\\server\share\OneDragon", false)]
    public void TheDocumentedPathRuleIsApplied(string path, bool allowed) =>
        Assert.Equal(allowed, EngineReadinessInspector.IsAsciiPathWithoutSpaces(path));

    [Fact]
    public async Task InvalidInstallationPathIsRefused()
    {
        // A space and non-ASCII characters break the documented rule on every OS (a plain temp path may pass on Windows).
        await using var f = new OneDragonFixture(folderPrefix: "vivian od 测试-");
        var report = await OneDragonInstallationInspector.InspectAsync(f.Root);
        Assert.Contains(report.Checks, c => c.Id == "od:path" && c.State == CheckState.Failed);
        Assert.False((await f.Adapter().PreflightAsync(Ctx, default)).CanStart);
    }

    [Fact]
    public async Task UnsupportedPackageIsRefused()
    {
        await using var f = new OneDragonFixture();
        var result = await f.Adapter(inspect: OneDragonFixture.Report(CompatibilityClass.UnsupportedVersion)).PreflightAsync(Ctx, default);
        Assert.False(result.CanStart);
        Assert.Equal(ReasonCodes.PreflightError, result.ReasonCode);
    }

    [Fact]
    public async Task RunningLauncherBlocksTheQueueAndIsNeverTouched()
    {
        await using var f = new OneDragonFixture();
        f.Probe.Running = 1;
        var result = await f.Adapter(inspect: OneDragonFixture.Report(CompatibilityClass.PendingValidation)).PreflightAsync(Ctx, default);
        Assert.Equal(ReasonCodes.ForeignProcess, result.ReasonCode);
        Assert.True(result.BlocksQueue);
    }

    [Fact]
    public async Task AValidInstallationIsStillRefusedWhileTheExecutionGateIsClosed()
    {
        await using var f = new OneDragonFixture();
        var adapter = f.Adapter(inspect: OneDragonFixture.Report(CompatibilityClass.PendingValidation));
        var result = await adapter.PreflightAsync(Ctx, default);
        Assert.Equal(ReasonCodes.RealAdapterUnverified, result.ReasonCode);
        var ex = await Assert.ThrowsAsync<EngineStartException>(() => adapter.StartAsync(Ctx, f.Supervisor, default));
        Assert.True(ex.NothingStarted);
        Assert.Equal(0, f.Supervisor.Calls);
    }

    [Fact]
    public async Task TheSchedulerBlocksTheAdapterBeforePreflightOrLaunch()
    {
        await using var f = new OneDragonFixture();
        var result = await new QueueScheduler(new InMemoryJournal(), f.Supervisor)
            .RunAsync([new QueueItem(GameId.ZenlessZoneZero, f.Adapter(inspect: OneDragonFixture.Report(CompatibilityClass.PendingValidation)))]);
        Assert.Equal(QueueStatus.Blocked, result.Status);
        Assert.Equal(ReasonCodes.RealAdapterUnverified, result.BlockReason);
        Assert.Equal(0, f.Supervisor.Calls);
    }
}

public sealed class OneDragonLifecycleTests
{
    private static Task<RunVerdict> Verdict(OneDragonRun run) =>
        run.WaitForVerdictAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);

    [Fact]
    public async Task LaunchUsesTheIntegratedLauncherWithAllowlistedArguments()
    {
        await using var f = new OneDragonFixture();
        var (run, _) = await f.LaunchAsync(f.Adapter(OneDragonArguments.OneDragon(true, [1, 2]).Request));
        await using var _run = run;
        var spec = f.Supervisor.LastSpec!;
        Assert.Equal(Path.Combine(Path.GetFullPath(f.Root), OneDragonContract.EntryProgram), spec.FileName);
        Assert.Equal(["-o", "-c", "-i", "1,2"], spec.Arguments);
        Assert.Equal(Path.GetFullPath(f.Root), spec.WorkingDirectory);
        Assert.Equal("utf-8", spec.Environment!["PYTHONIOENCODING"]);
    }

    [Fact]
    public async Task CompleteInProcessEvidenceIsSuccessOnlyOnceTheContractIsVerified()
    {
        await using var f = new OneDragonFixture();
        var (run, group) = await f.LaunchAsync(f.Adapter(contractVerified: true));
        await using var _run = run;
        group.Write(OdOut.SuccessfulRun());
        group.Exit(0);
        Assert.Equal(RunOutcome.Success, (await Verdict(run)).Outcome);
        Assert.Equal(CleanupState.Confirmed, await run.ReconcileAsync(default));
    }

    [Fact]
    public async Task DamagedStandardStreamCannotConfirmEvenAnApparentSuccess()
    {
        await using var f = new OneDragonFixture();
        var (run, group) = await f.LaunchAsync(f.Adapter(contractVerified: true));
        await using var _run = run;
        group.Write(OdOut.SuccessfulRun());
        group.Output.Writer.TryComplete(new IOException("simulated pipe failure"));
        group.Exit(0);
        var verdict = await Verdict(run);
        Assert.Equal(RunOutcome.Indeterminate, verdict.Outcome);
        Assert.Equal(ReasonCodes.NoCompletionEvidence, verdict.ReasonCode);
    }

    [Theory]
    [InlineData("none", OneDragonEngagementResult.NoEvidence, RunOutcome.Indeterminate)]
    [InlineData("skip", OneDragonEngagementResult.SkippedByUpstreamRecord, RunOutcome.Indeterminate)]
    [InlineData("max-only", OneDragonEngagementResult.RewardUnconfirmed, RunOutcome.Indeterminate)]
    [InlineData("wrong-source", OneDragonEngagementResult.RewardUnconfirmed, RunOutcome.Indeterminate)]
    [InlineData("confirmed", OneDragonEngagementResult.ClaimConfirmed, RunOutcome.Success)]
    [InlineData("failed", OneDragonEngagementResult.Failed, RunOutcome.Failed)]
    public async Task ShortTaskRequiresGameDerivedEvidenceFromTheSelectedRun(string caseId,
        OneDragonEngagementResult expectedTask, RunOutcome expectedRun)
    {
        await using var f = new OneDragonFixture();
        var request = OneDragonArguments.EngagementReward(1).Request!;
        var (run, group) = await f.LaunchAsync(f.Adapter(request, contractVerified: true));
        await using var _run = run;
        var normal = OdOut.SuccessfulRun("[1]");
        group.Write(normal[..3]);
        if (caseId == "skip")
            group.Write(OdOut.Line("指令[ 执行应用组 one_dragon ] 节点 执行应用 返回状态 应用已完成 活跃度奖励",
                source: "operation.py 451"));
        if (caseId is "confirmed" or "wrong-source")
            group.Write(OdOut.Line("指令[ 活跃度奖励 ] 节点 点击奖励 -> 查看奖励结果 返回状态 日常奖励领取成功",
                source: caseId == "confirmed" ? "operation.py 451" : "other.py 451"));
        if (caseId is "confirmed" or "max-only" or "wrong-source")
            group.Write(OdOut.Line("指令[ 活跃度奖励 ] 节点 查看奖励结果 -> 识别活跃度 返回状态 活跃度已满",
                    source: "operation.py 451"),
                OdOut.Line("指令[ 活跃度奖励 ] 执行成功 返回状态 成功", source: "operation.py 700"));
        if (caseId == "failed")
            group.Write(OdOut.Line("指令[ 活跃度奖励 ] 执行失败 返回状态 失败", level: "ERROR", source: "operation.py 702"));
        group.Write(normal[^1]);
        group.Exit(0);

        Assert.Equal(expectedRun, (await Verdict(run)).Outcome);
        Assert.Equal(expectedTask, run.Evidence.EngagementResult);
    }

    [Fact]
    public async Task ProductionContractNeverYieldsSuccess()
    {
        await using var f = new OneDragonFixture();
        var (run, group) = await f.LaunchAsync(f.Adapter());
        await using var _run = run;
        group.Write(OdOut.SuccessfulRun());
        group.Exit(0);
        var verdict = await Verdict(run);
        Assert.Equal(RunOutcome.Indeterminate, verdict.Outcome);
        Assert.Equal(ReasonCodes.RealAdapterUnverified, verdict.ReasonCode);
    }

    [Fact]
    public async Task LauncherExitingBeforeTheWorkerIsNeverCompletion()
    {
        // Non-elevated launcher: asks for elevation, starts an elevated copy outside the job, exits 0 at once.
        await using var f = new OneDragonFixture();
        var (run, group) = await f.LaunchAsync(f.Adapter(contractVerified: true));
        group.Exit(0);
        var verdict = await Verdict(run);
        Assert.Equal(RunOutcome.Indeterminate, verdict.Outcome);
        Assert.True(run.LauncherEndedWithoutWorker);
        Assert.Equal(0, group.ActiveProcessCount);                               // the job is empty …
        Assert.Equal(CleanupState.Unconfirmed, await run.ReconcileAsync(default)); // … but the worker may be outside it
        await run.DisposeAsync();
        Assert.True(f.Registry.IsBusy(f.Root));
    }

    [Fact]
    public async Task WorkerLeftInsideTheJobAfterTheLauncherExitsIsTerminatedOnReconcile()
    {
        await using var f = new OneDragonFixture();
        var (run, group) = await f.LaunchAsync(f.Adapter(contractVerified: true));
        await using var _run = run;
        group.Write(OdOut.SuccessfulRun());
        group.Exit(0, workerStillRunning: true);
        await Verdict(run);
        Assert.Equal(CleanupState.Confirmed, await run.ReconcileAsync(default));
        Assert.Equal(1, group.TerminateCalls);
    }

    [Theory]
    [InlineData("[12:00:00.123] [operation.py 702] [ERROR]: 指令[ 每日签到 ] 执行失败 返回状态 失败")]
    [InlineData("Traceback (most recent call last):")]
    [InlineData("[12:00:00.123] [application_launcher.py 68] [ERROR]: 未找到有效的实例")]
    public async Task WorkerFailureLinesOverrideCompletion(string failure)
    {
        await using var f = new OneDragonFixture();
        var (run, group) = await f.LaunchAsync(f.Adapter(contractVerified: true));
        await using var _run = run;
        group.Write(OdOut.SuccessfulRun());
        group.Write(failure);
        group.Exit(0);
        var verdict = await Verdict(run);
        Assert.NotEqual(RunOutcome.Success, verdict.Outcome);
    }

    [Fact]
    public async Task WorkerFailureWithoutCompletionIsFailed()
    {
        await using var f = new OneDragonFixture();
        var (run, group) = await f.LaunchAsync(f.Adapter(contractVerified: true));
        await using var _run = run;
        group.Write(OdOut.Line(OneDragonMarkers.RuntimeStartedPrefix + "v2.6.0"), OdOut.Line(OneDragonMarkers.AutoUpdateSkippedLine),
            OdOut.Line("指令[ 一条龙 ] 执行失败 返回状态 失败", "ERROR", "operation.py 702"));
        group.Exit(0);
        Assert.Equal(RunOutcome.Failed, (await Verdict(run)).Outcome);
    }

    [Fact]
    public async Task MissingCompletionEvidenceIsIndeterminate()
    {
        await using var f = new OneDragonFixture();
        var (run, group) = await f.LaunchAsync(f.Adapter(contractVerified: true));
        await using var _run = run;
        group.Write(OdOut.Line(OneDragonMarkers.RuntimeStartedPrefix + "v2.6.0"), OdOut.Line(OneDragonMarkers.AutoUpdateSkippedLine));
        group.Exit(0);
        var verdict = await Verdict(run);
        Assert.Equal(RunOutcome.Indeterminate, verdict.Outcome);
        Assert.Equal(CleanupState.Confirmed, await run.ReconcileAsync(default));
    }

    [Fact]
    public async Task CodeAutoUpdateOrAnotherVersionPreventsSuccess()
    {
        await using var f = new OneDragonFixture();
        var (run, group) = await f.LaunchAsync(f.Adapter(contractVerified: true));
        await using var _run = run;
        group.Write(OdOut.Line(OneDragonMarkers.RuntimeStartedPrefix + "v2.6.0"), OdOut.Line(OneDragonMarkers.CheckUpdateLine));
        group.Write(OdOut.SuccessfulRun()[2..]);
        group.Exit(0);
        Assert.Equal(RunOutcome.Indeterminate, (await Verdict(run)).Outcome);

        await using var g = new OneDragonFixture();
        var (run2, group2) = await g.LaunchAsync(g.Adapter(contractVerified: true));
        await using var _run2 = run2;
        group2.Write(OdOut.Line(OneDragonMarkers.RuntimeStartedPrefix + "v2.7.0"));
        group2.Write(OdOut.SuccessfulRun()[1..]);
        group2.Exit(0);
        Assert.Equal(RunOutcome.Indeterminate, (await Verdict(run2)).Outcome);
    }

    [Fact]
    public async Task RequestedInstancesMustBeEchoedExactly()
    {
        await using var f = new OneDragonFixture();
        var (run, group) = await f.LaunchAsync(f.Adapter(OneDragonArguments.OneDragon(false, [1, 2]).Request, contractVerified: true));
        await using var _run = run;
        group.Write(OdOut.SuccessfulRun("[1]"));
        group.Exit(0);
        Assert.Equal(RunOutcome.Indeterminate, (await Verdict(run)).Outcome);

        await using var g = new OneDragonFixture();
        var (run2, group2) = await g.LaunchAsync(g.Adapter(OneDragonArguments.OneDragon(false, [1, 2]).Request, contractVerified: true));
        await using var _run2 = run2;
        group2.Write(OdOut.SuccessfulRun("[1, 2]"));
        group2.Exit(0);
        Assert.Equal(RunOutcome.Success, (await Verdict(run2)).Outcome);
    }

    [Fact]
    public async Task CancellationTerminatesOnlyOwnedProcessesAndIsNeverFailure()
    {
        await using var f = new OneDragonFixture();
        var (run, group) = await f.LaunchAsync(f.Adapter(contractVerified: true));
        group.Write(OdOut.Line(OneDragonMarkers.RuntimeStartedPrefix + "v2.6.0"), OdOut.Line(OneDragonMarkers.AutoUpdateSkippedLine));
        await Task.Delay(50);
        var graceful = await run.RequestStopAsync(StopMode.Graceful, default);
        Assert.Equal("graceful_unavailable", graceful.ReasonCode);
        Assert.Equal(0, group.TerminateCalls);
        var stop = await run.RequestStopAsync(StopMode.Emergency, default);
        Assert.Equal(CleanupState.Confirmed, stop.Cleanup);
        var verdict = await Verdict(run);
        Assert.Equal(RunOutcome.Cancelled, verdict.Outcome);
        Assert.Equal(ReasonCodes.UserStop, verdict.ReasonCode);
        Assert.Equal(CleanupState.Confirmed, await run.ReconcileAsync(default));
        await run.DisposeAsync();
        Assert.True(group.Disposed);
        Assert.False(f.Registry.IsBusy(f.Root));
    }

    [Fact]
    public async Task CancellationBeforeTheWorkerIsProvenInProcessIsUnconfirmed()
    {
        await using var f = new OneDragonFixture();
        var (run, group) = await f.LaunchAsync(f.Adapter(contractVerified: true));
        var stop = await run.RequestStopAsync(StopMode.Emergency, default);
        Assert.Equal(CleanupState.Unconfirmed, stop.Cleanup);
        await Verdict(run);
        Assert.Equal(CleanupState.Unconfirmed, await run.ReconcileAsync(default));
        await run.DisposeAsync();
        Assert.True(f.Registry.IsBusy(f.Root));
    }

    [Fact]
    public async Task TerminationThatCannotBeConfirmedIsUnconfirmed()
    {
        await using var f = new OneDragonFixture();
        var (run, group) = await f.LaunchAsync(f.Adapter());
        group.Write(OdOut.Line(OneDragonMarkers.RuntimeStartedPrefix + "v2.6.0"));
        await Task.Delay(50);
        group.TerminateResult = CleanupState.Unconfirmed;
        Assert.Equal(CleanupState.Unconfirmed, (await run.RequestStopAsync(StopMode.Emergency, default)).Cleanup);
        Assert.Equal(CleanupState.Unconfirmed, await run.ReconcileAsync(default));
        await run.DisposeAsync();
        Assert.True(f.Registry.IsBusy(f.Root));
    }

    [Fact]
    public async Task ASecondLaunchOnTheSameInstallationIsRefused()
    {
        await using var f = new OneDragonFixture();
        var adapter = f.Adapter();
        var (run, group) = await f.LaunchAsync(adapter);
        var ex = await Assert.ThrowsAsync<EngineStartException>(() => adapter.LaunchAsync(f.Context with { RunId = Guid.NewGuid() }, f.Supervisor, default));
        Assert.True(ex.NothingStarted);
        Assert.Equal(1, f.Supervisor.Calls);
        group.Write(OdOut.Line(OneDragonMarkers.RuntimeStartedPrefix + "v2.6.0"));
        group.Exit(0);
        await Verdict(run);
        Assert.Equal(CleanupState.Confirmed, await run.ReconcileAsync(default));
        await run.DisposeAsync();
        Assert.False(f.Registry.IsBusy(f.Root));
    }

    [Fact]
    public async Task EventsCarryOnlyThisRunId()
    {
        await using var f = new OneDragonFixture();
        var (run, group) = await f.LaunchAsync(f.Adapter(contractVerified: true));
        await using var _run = run;
        group.Write(OdOut.SuccessfulRun());
        group.Exit(0);
        await Verdict(run);
        var events = new List<RunEvent>();
        await foreach (var e in run.Events) events.Add(e);
        Assert.All(events, e => Assert.Equal(f.Context.RunId, e.RunId));
        Assert.Contains(events, e => e.Kind == RunEventKind.Evidence);
    }
}
