using System.Threading.Channels;
using VIvian.Core.Execution;

namespace VIvian.Infrastructure.Engines.BetterGi;

/// <summary>Timing and evidence settings of a BetterGI run.</summary>
public sealed record BetterGiRunOptions
{
    /// <summary>How long Emergency stop and reconciliation wait for owned processes to end.</summary>
    public TimeSpan TerminateTimeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>After the root exits, the log is still read until it has been quiet this long …</summary>
    public TimeSpan LogQuietPeriod { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>… or at most this long (the tailer polls; BetterGI writes its last lines just before exiting).</summary>
    public TimeSpan LogDrainLimit { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Clock tolerance when comparing the log's instance start time with the owned root's creation time.</summary>
    public TimeSpan AttributionTolerance { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Only tests may set this; production always uses <see cref="BetterGiContract.CompletionSignalsVerified"/>.</summary>
    internal bool? CompletionContractVerifiedOverride { get; init; }
    internal bool CompletionContractVerified => CompletionContractVerifiedOverride ?? BetterGiContract.CompletionSignalsVerified;
}

/// <summary>
/// One BetterGI run, created by the adapter after the owned root process started inside VIvian's job.
/// <list type="bullet">
/// <item>Evidence comes from log records attributed to the owned root (PID and instance start time), read only from
/// content appended after the baseline taken before launch.</item>
/// <item>Graceful stop: BetterGI 0.66.0 has no external stop channel that can be bound to this run (its own stop is a
/// global hotkey), so nothing is sent; the scheduler escalates to Emergency.</item>
/// <item>Emergency stop and reconciliation only terminate processes inside this run's job. If BetterGI restarted
/// itself elevated (exit 553) or any BetterGI process VIvian does not own wrote to the log during the run, cleanup is
/// reported Unconfirmed, whatever the job says.</item>
/// </list>
/// </summary>
internal sealed class BetterGiRun : IEngineRun
{
    private readonly ISupervisedProcessGroup _group;
    private readonly IBetterGiLogSubscription _log;
    private readonly BetterGiRunOptions _options;
    private readonly TimeProvider _time;
    private readonly BetterGiRunEvidence _evidence;
    private readonly Action<CleanupState> _released;
    private readonly Channel<RunEvent> _events = Channel.CreateUnbounded<RunEvent>();
    private readonly CancellationTokenSource _readers = new();
    private readonly BetterGiLogRecordParser _parser = new();
    private readonly Lock _parserGate = new();
    private readonly Task _logReader;
    private readonly Task _outputReader;
    private readonly Task<RunVerdict> _verdict;
    private long _lastLogTimestamp;
    private volatile bool _stoppedByVivian;
    private volatile bool _detached;
    private CleanupState _lastCleanup = CleanupState.Pending;
    private int _disposed;

    public BetterGiRun(Guid runId, ISupervisedProcessGroup group, IBetterGiLogSubscription log, BetterGiTaskRequest request,
        BetterGiRunOptions options, TimeProvider time, Action<CleanupState> released)
    {
        RunId = runId;
        _group = group;
        _log = log;
        _options = options;
        _time = time;
        _released = released;
        _evidence = new BetterGiRunEvidence(runId, request,
            new BetterGiAttribution(group.RootProcessId, group.RootStartTime, options.AttributionTolerance));
        _lastLogTimestamp = time.GetTimestamp();
        Emit(RunEventKind.Progress, $"BetterGI 已在 VIvian 的作业内启动：PID {group.RootProcessId}，创建时间 {group.RootStartTime:O}；参数 {string.Join(' ', request.ToArguments())}。");
        if (!request.HasKnownCompletionContract)
            Emit(RunEventKind.Warning, "配置组运行没有已知的完成记录，结果最多只能是“未知”。");
        _logReader = ReadLogAsync();
        _outputReader = ReadOutputAsync();
        _verdict = DecideAsync();
    }

    public Guid RunId { get; }
    public IAsyncEnumerable<RunEvent> Events => _events.Reader.ReadAllAsync();
    internal BetterGiRunEvidence Evidence => _evidence;
    internal bool Detached => _detached;

    private void Emit(RunEventKind kind, string message) =>
        _events.Writer.TryWrite(new RunEvent(RunId, _time.GetUtcNow(), kind, message));

    private async Task ReadLogAsync()
    {
        try
        {
            await foreach (var line in _log.Lines.WithCancellation(_readers.Token).ConfigureAwait(false))
            {
                Interlocked.Exchange(ref _lastLogTimestamp, _time.GetTimestamp());
                BetterGiLogRecord? record;
                lock (_parserGate) record = _parser.Feed(line);
                if (record is not null) Handle(record);
            }
        }
        catch (OperationCanceledException) when (_readers.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Emit(RunEventKind.Warning, $"读取 BetterGI 日志出错（{ex.GetType().Name}），之后的日志不作为证据。");
        }
    }

    private void Handle(BetterGiLogRecord record)
    {
        var accepted = _evidence.Add(record, _time.GetUtcNow());
        switch (accepted.Disposition)
        {
            case BetterGiRunEvidence.Disposition.Foreign:
                Emit(RunEventKind.Warning, $"发现不属于本次运行的 BetterGI 进程（PID {record.ProcessId}）写入日志：不对其做任何操作，本次清理将无法确认。");
                return;
            case BetterGiRunEvidence.Disposition.Historical:
                return; // an earlier process's record: never evidence, not worth an event each
        }
        var kind = accepted.Marker.Kind switch
        {
            BetterGiMarkerKind.Progress or BetterGiMarkerKind.Activated or BetterGiMarkerKind.ArgumentConfig => RunEventKind.Progress,
            BetterGiMarkerKind.Completed or BetterGiMarkerKind.Cancelled or BetterGiMarkerKind.NothingDone => RunEventKind.Evidence,
            BetterGiMarkerKind.Failure or BetterGiMarkerKind.Fatal or BetterGiMarkerKind.ConfigNotFound => RunEventKind.Warning,
            _ => RunEventKind.Output,
        };
        Emit(kind, $"[{record.Level}] {Truncate(record.Message)}");
    }

    private async Task ReadOutputAsync()
    {
        // BetterGI is a GUI program; anything on its standard streams is shown for diagnosis but never used as evidence.
        try
        {
            await foreach (var line in _group.OutputLines.WithCancellation(_readers.Token).ConfigureAwait(false))
                Emit(RunEventKind.Output, Truncate(line));
        }
        catch (Exception)
        {
            // Output errors carry no evidence.
        }
    }

    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500] + "…";

    private async Task<RunVerdict> DecideAsync()
    {
        int? exitCode;
        try { exitCode = await _group.WaitForRootExitAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { exitCode = null; }

        if (exitCode == BetterGiContract.ElevationRestartExitCode && !_stoppedByVivian)
        {
            _detached = true;
            Emit(RunEventKind.Warning, $"BetterGI 以退出码 {exitCode} 结束：它以管理员权限重新启动了自己，新进程不在 VIvian 的作业内，无法确认其状态。");
        }
        else if (exitCode == BetterGiContract.SingleInstanceOrIntegrationExitCode && !_stoppedByVivian)
        {
            Emit(RunEventKind.Warning, "BetterGI 以 0xFFFF 结束：已有实例在运行，或安装缺少关键文件夹。");
        }

        await DrainLogAsync().ConfigureAwait(false);
        BetterGiLogRecord? last;
        lock (_parserGate) last = _parser.Flush();
        if (last is not null) Handle(last);
        await _readers.CancelAsync().ConfigureAwait(false);

        var verdict = _evidence.Evaluate(exitCode, _stoppedByVivian, _detached, _options.CompletionContractVerified, _time.GetUtcNow());
        Emit(RunEventKind.Output, $"BetterGI 主进程已退出，退出码 {exitCode?.ToString() ?? "未知"}（退出码不作为成功依据）。");
        _events.Writer.TryComplete();
        return verdict;
    }

    private async Task DrainLogAsync()
    {
        var started = _time.GetTimestamp();
        while (!_logReader.IsCompleted)
        {
            var now = _time.GetTimestamp();
            if (_time.GetElapsedTime(started, now) >= _options.LogDrainLimit) return;
            if (_time.GetElapsedTime(Interlocked.Read(ref _lastLogTimestamp), now) >= _options.LogQuietPeriod) return;
            await Task.Delay(TimeSpan.FromMilliseconds(50), _time).ConfigureAwait(false);
        }
    }

    public Task<RunVerdict> WaitForVerdictAsync(CancellationToken ct) => _verdict.WaitAsync(ct);

    public async Task<StopResult> RequestStopAsync(StopMode mode, CancellationToken ct)
    {
        if (mode == StopMode.Graceful)
        {
            Emit(RunEventKind.Warning, "BetterGI 0.66.0 没有可绑定到本次运行的停止通道（不发送热键或窗口消息）；将等待超时后终止本次运行拥有的进程。");
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

    /// <summary>The job only proves what is inside it. A detached or foreign BetterGI process keeps cleanup unconfirmed.</summary>
    private CleanupState Restrict(CleanupState cleanup)
    {
        if (cleanup != CleanupState.Confirmed) return CleanupState.Unconfirmed;
        if (_detached || _evidence.ForeignActivitySeen)
        {
            Emit(RunEventKind.Warning, "作业内的进程已结束，但存在不属于本次运行的 BetterGI 进程（提权重启或其他实例）：清理未确认，队列保持阻断。");
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
        await _readers.CancelAsync().ConfigureAwait(false);
        _events.Writer.TryComplete();
        try { await Task.WhenAll(_logReader, _outputReader).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception) { /* readers only read */ }
        try { await _log.DisposeAsync().ConfigureAwait(false); }
        catch (Exception) { /* read-only */ }
        try { await _group.DisposeAsync().ConfigureAwait(false); }
        catch (Exception) { _lastCleanup = CleanupState.Unconfirmed; }
        _released(_lastCleanup);
    }
}
