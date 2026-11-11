using System.Threading.Channels;
using VIvian.Core.Execution;

namespace VIvian.Infrastructure.Engines.OneDragon;

public sealed record OneDragonRunOptions
{
    /// <summary>How long Emergency stop and reconciliation wait for owned processes to end.</summary>
    public TimeSpan TerminateTimeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>After the launcher exits, its output may still be draining (a descendant can hold the pipe).</summary>
    public TimeSpan OutputDrainLimit { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>Only tests may set this; production always uses <see cref="OneDragonContract.CompletionSignalsVerified"/>.</summary>
    internal bool? CompletionContractVerifiedOverride { get; init; }
    internal bool CompletionContractVerified => CompletionContractVerifiedOverride ?? OneDragonContract.CompletionSignalsVerified;
}

/// <summary>
/// One OneDragon run. The integrated launcher is the owned root; per the pinned source it runs the one-dragon code in
/// its own process when it is already elevated, and otherwise asks for elevation, starts an elevated copy outside
/// VIvian's job and exits with code 0 at once. The two cases are told apart only by the in-process start line on the
/// owned process's output:
/// <list type="bullet">
/// <item>Start line seen: launcher lifetime = worker lifetime; the job proves cleanup.</item>
/// <item>Not seen when the launcher ends: the worker may be running detached. The verdict is Indeterminate and
/// cleanup is Unconfirmed even when the job is empty, so the queue stays blocked.</item>
/// </list>
/// There is no stop channel that can be bound to this run, so Graceful sends nothing; Emergency terminates only the
/// processes inside this run's job.
/// </summary>
internal sealed class OneDragonRun : IEngineRun
{
    private readonly ISupervisedProcessGroup _group;
    private readonly OneDragonRunOptions _options;
    private readonly TimeProvider _time;
    private readonly OneDragonRunEvidence _evidence;
    private readonly bool _engagementOnly;
    private readonly OneDragonHandoffEvidence? _handoff;
    private readonly Action<CleanupState> _released;
    private readonly Channel<RunEvent> _events = Channel.CreateUnbounded<RunEvent>();
    private readonly CancellationTokenSource _reader = new();
    private readonly Task<bool> _outputReader;
    private readonly Task<RunVerdict> _verdict;
    private volatile bool _stoppedByVivian;
    private volatile bool _launcherEndedWithoutWorker;
    private CleanupState _lastCleanup = CleanupState.Pending;
    private int _disposed;

    public OneDragonRun(Guid runId, ISupervisedProcessGroup group, OneDragonTaskRequest request, string pinnedVersion,
        OneDragonRunOptions options, TimeProvider time, Action<CleanupState> released, OneDragonHandoff? handoff = null)
    {
        RunId = runId;
        _group = group;
        _options = options;
        _time = time;
        _released = released;
        _engagementOnly = request.EngagementRewardOnly;
        _handoff = handoff is null ? null : new(runId, group.RootProcessId, handoff);
        _evidence = new OneDragonRunEvidence(runId, request, pinnedVersion);
        Emit(RunEventKind.Progress, $"OneDragon 集成启动器已在 VIvian 的作业内启动：PID {group.RootProcessId}，创建时间 {group.RootStartTime:O}；参数 {string.Join(' ', request.ToArguments())}。");
        _outputReader = ReadOutputAsync();
        _verdict = DecideAsync();
    }

    public Guid RunId { get; }
    public IAsyncEnumerable<RunEvent> Events => _events.Reader.ReadAllAsync();
    internal OneDragonRunEvidence Evidence => _evidence;
    internal bool LauncherEndedWithoutWorker => _launcherEndedWithoutWorker;

    private void Emit(RunEventKind kind, string message) =>
        _events.Writer.TryWrite(new RunEvent(RunId, _time.GetUtcNow(), kind, message));

    private async Task<bool> ReadOutputAsync()
    {
        try
        {
            await foreach (var line in _group.OutputLines.WithCancellation(_reader.Token).ConfigureAwait(false))
            {
                if (_handoff is { } handoff)
                {
                    if (handoff.Read(line) is { } progress) Emit(RunEventKind.Progress, progress);
                    continue;
                }
                var marker = _evidence.Add(line, _time.GetUtcNow());
                var kind = marker.Kind switch
                {
                    OneDragonMarkerKind.Completed => RunEventKind.Evidence,
                    OneDragonMarkerKind.Failure or OneDragonMarkerKind.EngagementFailed
                        or OneDragonMarkerKind.AutoUpdateAttempted => RunEventKind.Warning,
                    OneDragonMarkerKind.RuntimeStarted or OneDragonMarkerKind.InstancesSelected or OneDragonMarkerKind.AutoUpdateSkipped => RunEventKind.Progress,
                    _ => RunEventKind.Output,
                };
                if (marker.Kind == OneDragonMarkerKind.AutoUpdateAttempted)
                    Emit(RunEventKind.Warning, "OneDragon 在运行前检查或同步了上游代码：本次运行的代码可能已不是固定版本，结果不会判为成功。");
                Emit(kind, line.Length <= 500 ? line : line[..500] + "…");
            }
            return true;
        }
        catch (OperationCanceledException) when (_reader.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            Emit(RunEventKind.Warning, $"读取 OneDragon 输出出错（{ex.GetType().Name}），输出不完整。");
            return false;
        }
    }

    private bool WorkerConfirmed => _handoff?.Started ?? _evidence.InProcessStartSeen;

    private async Task<RunVerdict> DecideAsync()
    {
        int? exitCode;
        try { exitCode = await _group.WaitForRootExitAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { exitCode = null; }
        var stopped = _stoppedByVivian;
        var drained = await Task.WhenAny(_outputReader, Task.Delay(_options.OutputDrainLimit, _time)).ConfigureAwait(false);
        var outputComplete = drained == _outputReader && await _outputReader.ConfigureAwait(false);
        await _reader.CancelAsync().ConfigureAwait(false);
        if (drained != _outputReader)
            Emit(RunEventKind.Warning, "OneDragon 标准流未在时限内结束，无法用它确认成功。");

        if (!stopped && !WorkerConfirmed)
        {
            _launcherEndedWithoutWorker = true;
            Emit(RunEventKind.Warning, $"OneDragon 启动器以退出码 {exitCode?.ToString() ?? "未知"} 结束，但没有在本进程内开始运行：它可能已请求管理员权限并在 VIvian 的作业之外启动了新进程。启动器退出不代表任务完成，清理无法确认。");
        }
        var verdict = _handoff?.Verdict(exitCode, outputComplete, _time.GetUtcNow()) ?? _evidence.Evaluate(exitCode, stopped, _options.CompletionContractVerified, _time.GetUtcNow(), outputComplete);
        if (stopped && exitCode.HasValue)
            verdict = verdict with { Outcome = RunOutcome.Cancelled, ReasonCode = ReasonCodes.UserStop };
        else if (_launcherEndedWithoutWorker && verdict.Outcome != RunOutcome.Indeterminate)
            verdict = verdict with { Outcome = RunOutcome.Indeterminate, ReasonCode = ReasonCodes.CleanupUnconfirmed };
        if (_engagementOnly && outputComplete && !stopped)
            Emit(RunEventKind.Progress, $"OneDragon 活跃度奖励标准流判定：{_evidence.EngagementResult}。");
        Emit(RunEventKind.Output, $"OneDragon 启动器已退出，退出码 {exitCode?.ToString() ?? "未知"}（退出码不作为成功依据）。");
        _events.Writer.TryComplete();
        return verdict;
    }

    public Task<RunVerdict> WaitForVerdictAsync(CancellationToken ct) => _verdict.WaitAsync(ct);

    public async Task<StopResult> RequestStopAsync(StopMode mode, CancellationToken ct)
    {
        if (mode == StopMode.Graceful)
        {
            Emit(RunEventKind.Warning, "OneDragon v2.6.0 的命令行模式没有可绑定到本次运行的停止通道（不发送热键或窗口消息）；将等待超时后终止本次运行拥有的进程。");
            return new StopResult(CleanupState.Pending, "graceful_unavailable");
        }
        _stoppedByVivian = true;
        var cleanup = await TerminateOwnedAsync(ct).ConfigureAwait(false);
        return new StopResult(cleanup, cleanup == CleanupState.Confirmed ? "terminated_owned_processes" : ReasonCodes.CleanupUnconfirmed);
    }

    public async Task<CleanupState> ReconcileAsync(CancellationToken ct)
    {
        CleanupState cleanup;
        try
        {
            cleanup = _group.ActiveProcessCount == 0 ? CleanupState.Confirmed : await TerminateOwnedAsync(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            cleanup = CleanupState.Unconfirmed;
        }
        return Record(Restrict(cleanup));
    }

    private async Task<CleanupState> TerminateOwnedAsync(CancellationToken ct)
    {
        CleanupState cleanup;
        try { cleanup = await _group.TerminateAllAsync(_options.TerminateTimeout, ct).ConfigureAwait(false); }
        catch (Exception) { cleanup = CleanupState.Unconfirmed; }
        return Record(Restrict(cleanup));
    }

    private CleanupState Restrict(CleanupState cleanup)
    {
        if (cleanup != CleanupState.Confirmed) return CleanupState.Unconfirmed;
        // Without the in-process start line nobody can tell whether the launcher handed the work to an elevated copy
        // outside the job (it does so right before exiting), even when VIvian itself ended the launcher.
        if (_launcherEndedWithoutWorker || !WorkerConfirmed)
        {
            Emit(RunEventKind.Warning, "作业已空，但没有证据表明工作在本次运行的进程内进行，工作进程可能在作业之外：清理未确认，队列保持阻断。");
            return CleanupState.Unconfirmed;
        }
        return CleanupState.Confirmed;
    }

    private CleanupState Record(CleanupState cleanup)
    {
        _lastCleanup = cleanup;
        return cleanup;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _reader.CancelAsync().ConfigureAwait(false);
        _events.Writer.TryComplete();
        try { await _outputReader.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception) { /* reader only reads */ }
        try { await _group.DisposeAsync().ConfigureAwait(false); }
        catch (Exception) { _lastCleanup = CleanupState.Unconfirmed; }
        _released(_lastCleanup);
    }
}
