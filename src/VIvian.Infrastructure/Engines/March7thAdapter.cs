using System.Threading.Channels;
using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Engines.Cli;

namespace VIvian.Infrastructure.Engines;

/// <summary>Fixed-release task runner. The scheduler's real-execution admission remains closed until UAC ownership is validated.</summary>
public sealed class March7thAdapter(string installDirectory, March7thReleaseEvidence release, string task,
    Func<bool> userConfirmedThisRun, IProcessSupervisor statusSupervisor, Action<IEngineRun>? onStarted = null,
    March7thCircuitOptions? circuitOptions = null) : IGameAdapter
{
    public GameId Game => GameId.StarRail;
    public string RuleVersion => "m7a-v2026.10.31-task-1";
    public bool IsSimulation => false;

    public async Task<PreflightResult> PreflightAsync(RunContext context, CancellationToken ct)
    {
        if (!RealExecutionPolicy.AllowsTask(task))
            return PreflightResult.Block("task_not_authorized", "此构建未授权该真实任务。");
        if (!userConfirmedThisRun()) return PreflightResult.Block("real_run_not_confirmed", "请在界面中确认本次真实任务。");
        if (release.Version != "v2026.10.31") return PreflightResult.Block("release_mismatch", "上游版本不匹配。");
        try { March7thTaskContract.StartArguments(task); }
        catch (ArgumentException) { return PreflightResult.Block("task_not_allowed", "任务不在固定版白名单中。"); }
        var install = await March7thReadOnlyInspector.InspectAsync(installDirectory, release, ct).ConfigureAwait(false);
        return install.Compatibility == March7thCompatibility.Verified
            ? PreflightResult.Ok() : PreflightResult.Block("install_not_verified", install.Detail);
    }

    public async Task<IEngineRun> StartAsync(RunContext context, IProcessSupervisor supervisor, CancellationToken ct)
    {
        if (!RealExecutionPolicy.AllowsTask(task))
            throw new EngineStartException(ReasonCodes.RealAdapterUnverified, true, "跨 UAC 工作进程清理尚未验证，真实任务闸门关闭。");
        if (!userConfirmedThisRun())
            throw new EngineStartException(ReasonCodes.RealAdapterUnverified, true, "本次真实运行尚未由用户确认。");
        var install = await March7thReadOnlyInspector.InspectAsync(installDirectory, release, ct).ConfigureAwait(false);
        if (install.Compatibility != March7thCompatibility.Verified || install.CliPath is null)
            throw new EngineStartException(ReasonCodes.RealAdapterUnverified, true, "上游身份核对未通过。");
        // The scheduler's per-run capability is deliberately single-use. The read-only status query
        // owns and cleans up its own Job; the capability remains available for the actual task launch.
        var status = await March7thStatusCommand.CheckAsync(statusSupervisor, install.CliPath, installDirectory,
                context.RunId, ct)
            .ConfigureAwait(false);
        if (!status.Status.ReadyForUacValidation)
            throw new EngineStartException(ReasonCodes.RealAdapterUnverified,
                status.Cleanup == CleanupState.Confirmed, "上游状态阻断：" + status.Status.Detail);
        var group = await supervisor.StartAsync(new ProcessLaunchSpec(install.CliPath,
            March7thTaskContract.StartArguments(task), installDirectory), context.RunId, ct).ConfigureAwait(false);
        // The task capability is single-use; exact-RunId control commands are separately Job-supervised.
        var run = new March7thRun(context.RunId, group, statusSupervisor, install.CliPath, installDirectory, task,
            March7thDailyTemplates.Read(Path.Combine(installDirectory, "config.yaml")), circuitOptions);
        onStarted?.Invoke(run);
        return run;
    }
}

internal sealed class March7thRun : IEngineRun, IPausableEngineRun
{
    private readonly ISupervisedProcessGroup _group;
    private readonly IProcessSupervisor _supervisor;
    private readonly string _cli;
    private readonly string _directory;
    private readonly string _task;
    private readonly March7thDailyTemplates _templates;
    private readonly March7thCircuitOptions _circuitOptions;
    private readonly Channel<RunEvent> _events = Channel.CreateUnbounded<RunEvent>();
    private readonly List<string> _output = [];
    private readonly Task<RunVerdict> _verdict;
    private string? _upstreamRunId;
    private bool _runIdConflict;
    private bool _outputTruncated;
    private int _resolutionErrors;
    private int _circuitTripped;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now.AddSeconds(-5);
    /// <summary>How long a stopped worker may take to reach its next checkpoint; upstream force-exits after 60 s.</summary>
    internal static TimeSpan WorkerExitWait { get; set; } = TimeSpan.FromSeconds(65);
    internal static TimeSpan StopAcknowledgeWait { get; set; } = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _controlGate = new(1, 1);
    private volatile bool _paused;
    private volatile bool _stopRequested;
    public bool IsPaused => _paused;

    public March7thRun(Guid runId, ISupervisedProcessGroup group, IProcessSupervisor supervisor,
        string cli, string directory, string task, March7thDailyTemplates? templates = null,
        March7thCircuitOptions? circuitOptions = null)
    {
        RunId = runId; _group = group; _supervisor = supervisor; _cli = cli; _directory = directory; _task = task;
        _templates = templates ?? March7thDailyTemplates.Default;
        _circuitOptions = circuitOptions ?? March7thCircuitOptions.Default;
        _verdict = ObserveAsync();
        _ = Task.Run(MonitorStartupLogAsync);
    }

    public Guid RunId { get; }
    public IAsyncEnumerable<RunEvent> Events => _events.Reader.ReadAllAsync();

    private async Task<RunVerdict> ObserveAsync()
    {
        var reader = Task.Run(async () =>
        {
            await foreach (var line in _group.OutputLines.ConfigureAwait(false))
            {
                string? displayed = null;
                lock (_output)
                {
                    if (line.Length > 32768) _outputTruncated = true;
                    var retained = line.Length <= 32768 ? line : line[..32768];
                    displayed = line.Length <= 4000 ? line : line[..4000];
                    if (_output.Count == 4000) { _output.RemoveAt(0); _outputTruncated = true; }
                    _output.Add(retained);
                    var found = March7thTaskContract.CaptureRunId([line]);
                    if (found is not null && !_runIdConflict)
                    {
                        if (_upstreamRunId is null) _upstreamRunId = found;
                        else if (_upstreamRunId != found) { _upstreamRunId = null; _runIdConflict = true; }
                    }
                }
                if (displayed is not null)
                    _events.Writer.TryWrite(new RunEvent(RunId, DateTimeOffset.UtcNow, RunEventKind.Output, displayed));
            }
        });
        try
        {
            var exit = await _group.WaitForRootExitAsync(CancellationToken.None).ConfigureAwait(false);
            await reader.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            string? id; string output; bool truncated;
            lock (_output) { id = _upstreamRunId; output = string.Join('\n', _output); truncated = _outputTruncated; }
            if (id is null) return Unknown("上游运行编号缺失或冲突。");
            if (truncated && output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Length >= 32768)
                return Unknown("上游终态记录过长，无法完整核对。");
            var parsed = March7thTaskContract.ParseFinalRecord(output, _task, exit, id, _templates);
            if (Volatile.Read(ref _circuitTripped) != 0)
                return new RunVerdict(RunOutcome.Failed, ReasonCodes.ResolutionCircuitOpen,
                    [new Evidence(RunId, EvidenceKind.FailureReported, "上游日志连续报告游戏分辨率获取失败；已发送本次任务的停止命令。", DateTimeOffset.UtcNow)]);
            // The verdict's reason is part of the run's own record (UI log, database events, diagnostic export).
            _events.Writer.TryWrite(new RunEvent(RunId, DateTimeOffset.UtcNow,
                parsed.Outcome == RunOutcome.Success ? RunEventKind.Evidence : RunEventKind.Warning,
                "[VIvian 判定] " + parsed.Detail + "（上游运行编号 " + id + "）"));
            if (parsed.Outcome == RunOutcome.Success)
            {
                var own = await _group.TerminateAllAsync(TimeSpan.FromSeconds(10), CancellationToken.None)
                    .ConfigureAwait(false);
                if (own != CleanupState.Confirmed || March7thWorkerExit.Check(output, id, _task) != WorkerExitState.Exited)
                    return Unknown("完成步骤已记录，但无法确认本次工作进程全部退出。");
            }
            var reason = parsed.Outcome switch
            {
                RunOutcome.Success => ReasonCodes.Completed,
                RunOutcome.Failed => ReasonCodes.ReportedFailure,
                RunOutcome.Cancelled => ReasonCodes.ReportedCancel,
                _ => ReasonCodes.NoCompletionEvidence,
            };
            var kind = parsed.Outcome == RunOutcome.Success ? EvidenceKind.CompletionReported : EvidenceKind.Note;
            return new RunVerdict(parsed.Outcome, reason, [new Evidence(RunId, kind, parsed.Detail, DateTimeOffset.UtcNow)]);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        { return Unknown("入口进程或日志采集未能确认终态。"); }
        finally { _events.Writer.TryComplete(); }
    }

    private RunVerdict Unknown(string detail) => new(RunOutcome.Indeterminate, ReasonCodes.NoCompletionEvidence,
        [new Evidence(RunId, EvidenceKind.Note, detail, DateTimeOffset.UtcNow)]);

    private async Task MonitorStartupLogAsync()
    {
        var readLines = 0;
        var started = DateTimeOffset.UtcNow;
        while (!_verdict.IsCompleted && Volatile.Read(ref _circuitTripped) == 0)
        {
            var id = UpstreamRunId();
            if (id is not null)
            {
                var path = Path.Combine(March7thUpstreamActivity.RunsDirectory(_directory), id + ".log");
                try
                {
                    if (File.Exists(path) && new FileInfo(path).Length < 5_000_000)
                    {
                        var lines = File.ReadAllLines(path);
                        if (lines.Length < readLines) readLines = 0;
                        foreach (var line in lines.Skip(readLines))
                            if (line.Contains("游戏分辨率获取失败", StringComparison.Ordinal) &&
                                Interlocked.Increment(ref _resolutionErrors) >= _circuitOptions.MaxResolutionFailures)
                            { await TripCircuitAsync(id, path).ConfigureAwait(false); break; }
                        readLines = lines.Length;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { Note(RunEventKind.Warning, "[VIvian] 无法读取本次上游日志：" + ex.Message); }
            }
            if (DateTimeOffset.UtcNow - started > _circuitOptions.MaxStartupWait && id is null)
            {
                Note(RunEventKind.Warning, "[VIvian 熔断] 启动等待超时且未取得上游运行编号；正在结束本次拥有的进程。上游清理将保持未确认。");
                Interlocked.Exchange(ref _circuitTripped, 1);
                _stopRequested = true;
                await _group.TerminateAllAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
                return;
            }
            await Task.Delay(500).ConfigureAwait(false);
        }
    }

    private async Task TripCircuitAsync(string id, string logPath)
    {
        if (Interlocked.Exchange(ref _circuitTripped, 1) != 0) return;
        Note(RunEventKind.Warning, "[VIvian 熔断] 连续两次游戏分辨率获取失败；正在停止当前任务。请检查游戏窗口与显示模式。日志：" + logPath);
        try { await RequestStopAsync(StopMode.Emergency, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { Note(RunEventKind.Warning, "[VIvian 熔断] 停止命令异常：" + ex.Message); }
    }

    public Task<RunVerdict> WaitForVerdictAsync(CancellationToken ct) => _verdict.WaitAsync(ct);

    public async Task<bool> SetPausedAsync(bool pause, CancellationToken ct)
    {
        await _controlGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_verdict.IsCompleted || _stopRequested) return false;
            if (_paused == pause) return true;
            var id = UpstreamRunId();
            if (id is null || _stopRequested || March7thUpstreamActivity.WorkerState(_directory, id) != WorkerExitState.Running)
                return false;
            var control = March7thUpstreamActivity.TrySendControl(_directory, id, pause ? "pause" : "resume");
            if (control is null) return false;
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(8);
            while (DateTimeOffset.UtcNow < deadline && !_verdict.IsCompleted)
            {
                if (_stopRequested)
                {
                    March7thUpstreamActivity.TrySendStop(_directory, id);
                    return false;
                }
                var state = March7thUpstreamActivity.ReadControlState(control);
                if (state == (pause ? "paused" : "running"))
                {
                    _paused = pause;
                    Note(RunEventKind.Warning, pause ? "[VIvian] 星铁助手已确认暂停并释放键鼠。" : "[VIvian] 星铁助手已确认继续当前任务。");
                    return true;
                }
                if (state == "stopping") return false;
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
            return false;
        }
        finally { _controlGate.Release(); }
    }

    public async Task<StopResult> RequestStopAsync(StopMode mode, CancellationToken ct)
    {
        _stopRequested = true;
        var id = UpstreamRunId();
        if (id is null) return new(CleanupState.Unconfirmed, ReasonCodes.CleanupUnconfirmed);
        // Fast path: the control file this exact run polls every 0.3 s. Keyboard and mouse are released as soon as
        // the worker reads it; "stopping" is its receipt. No process has to start first.
        if (March7thUpstreamActivity.TrySendStop(_directory, id) is { } controlFile)
        {
            Note(RunEventKind.Warning, "[VIvian] 已向星铁助手发出停止指令（运行编号 " + id + "）。");
            var deadline = DateTimeOffset.UtcNow + StopAcknowledgeWait;
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (March7thUpstreamActivity.ReadControlState(controlFile) == "stopping")
                {
                    Note(RunEventKind.Warning, "[VIvian] 星铁助手已确认停止：键鼠已释放，不会再操作游戏。正在等它退出（通常几秒，最长约 60 秒）。");
                    return new(CleanupState.Pending, ReasonCodes.UserStop);
                }
                if (_verdict.IsCompleted) return new(CleanupState.Pending, ReasonCodes.UserStop);
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
        await using var control = await _supervisor.StartAsync(new ProcessLaunchSpec(_cli,
            March7thTaskContract.StopArguments(id), _directory), RunId, ct).ConfigureAwait(false);
        var controlOutput = new List<string>();
        var controlTruncated = false;
        var drain = Task.Run(async () =>
        {
            await foreach (var line in control.OutputLines.ConfigureAwait(false))
            {
                if (controlOutput.Count < 20 && line.Length <= 4000) controlOutput.Add(line);
                else controlTruncated = true;
            }
        });
        var exit = await control.WaitForRootExitAsync(ct).ConfigureAwait(false);
        var cleanup = await control.TerminateAllAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        await drain.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        var requested = exit == 0 && cleanup == CleanupState.Confirmed && !controlTruncated
            && March7thStopContract.Acknowledged(string.Join('\n', controlOutput), id);
        return new(requested ? CleanupState.Pending : CleanupState.Unconfirmed,
            requested ? ReasonCodes.UserStop : ReasonCodes.CleanupUnconfirmed);
    }

    public async Task<CleanupState> ReconcileAsync(CancellationToken ct)
    {
        // A stop receipt can precede worker exit. Give the frontend a bounded chance to follow
        // the elevated process and emit its final record before closing our own Job.
        var id = UpstreamRunId();
        // The elevated worker is outside our Job and cannot be ended by VIvian: wait (bounded) until its own record
        // shows its exact PID + creation time gone. A stop acknowledgement alone is never enough.
        var deadline = DateTimeOffset.UtcNow + WorkerExitWait;
        while (id is not null && DateTimeOffset.UtcNow < deadline)
        {
            var state = March7thUpstreamActivity.WorkerState(_directory, id);
            // Provably alive: keep waiting even if the entry window has gone. Not yet decidable: wait for the entry.
            if (state == WorkerExitState.Exited || (state == WorkerExitState.Unknown && _verdict.IsCompleted)) break;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        try { await _verdict.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
        catch (TimeoutException) { /* the entry may linger; checked below */ }
        var own = await _group.TerminateAllAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        string output;
        lock (_output) output = string.Join('\n', _output);
        if (own != CleanupState.Confirmed || id is null) return CleanupState.Unconfirmed;
        if (March7thWorkerExit.Check(output, id, _task) == WorkerExitState.Exited) return CleanupState.Confirmed;
        // No final record from the entry (stopped, window closed, crash): the worker's own record decides.
        var exited = March7thUpstreamActivity.WorkerState(_directory, id) == WorkerExitState.Exited;
        Note(exited ? RunEventKind.Evidence : RunEventKind.Warning, exited
            ? "[VIvian] 已核实星铁助手工作进程已退出（运行编号 " + id + "）。"
            : "[VIvian] 无法核实星铁助手工作进程已退出（运行编号 " + id + "）。");
        return exited ? CleanupState.Confirmed : CleanupState.Unconfirmed;
    }

    private string? UpstreamRunId()
    {
        lock (_output)
        {
            if (_upstreamRunId is not null || _runIdConflict) return _upstreamRunId;
        }
        // The launch line was not captured (yet): the lock holder for this task that started after us is ours.
        return March7thUpstreamActivity.ActiveRunStartedSince(_directory, _task, _startedAt);
    }

    private void Note(RunEventKind kind, string message) =>
        _events.Writer.TryWrite(new RunEvent(RunId, DateTimeOffset.UtcNow, kind, message));

    public ValueTask DisposeAsync() => _group.DisposeAsync();
}
