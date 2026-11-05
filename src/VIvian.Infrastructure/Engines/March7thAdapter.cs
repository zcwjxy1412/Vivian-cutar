using System.Threading.Channels;
using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Engines.Cli;

namespace VIvian.Infrastructure.Engines;

/// <summary>Fixed-release task runner. The scheduler's real-execution admission remains closed until UAC ownership is validated.</summary>
internal sealed class March7thAdapter(string installDirectory, March7thReleaseEvidence release, string task,
    Func<bool> userConfirmedThisRun) : IGameAdapter
{
    public GameId Game => GameId.StarRail;
    public string RuleVersion => "m7a-v2026.10.31-task-1";
    public bool IsSimulation => false;

    public async Task<PreflightResult> PreflightAsync(RunContext context, CancellationToken ct)
    {
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
        if (!RealExecutionPolicy.BuildPermitsRealExecution)
            throw new EngineStartException(ReasonCodes.RealAdapterUnverified, true, "跨 UAC 工作进程清理尚未验证，真实任务闸门关闭。");
        if (!userConfirmedThisRun())
            throw new EngineStartException(ReasonCodes.RealAdapterUnverified, true, "本次真实运行尚未由用户确认。");
        var install = await March7thReadOnlyInspector.InspectAsync(installDirectory, release, ct).ConfigureAwait(false);
        if (install.Compatibility != March7thCompatibility.Verified || install.CliPath is null)
            throw new EngineStartException(ReasonCodes.RealAdapterUnverified, true, "上游身份核对未通过。");
        var group = await supervisor.StartAsync(new ProcessLaunchSpec(install.CliPath,
            March7thTaskContract.StartArguments(task), installDirectory), context.RunId, ct).ConfigureAwait(false);
        return new March7thRun(context.RunId, group, supervisor, install.CliPath, installDirectory, task);
    }
}

internal sealed class March7thRun : IEngineRun
{
    private readonly ISupervisedProcessGroup _group;
    private readonly IProcessSupervisor _supervisor;
    private readonly string _cli;
    private readonly string _directory;
    private readonly string _task;
    private readonly Channel<RunEvent> _events = Channel.CreateUnbounded<RunEvent>();
    private readonly List<string> _output = [];
    private readonly Task<RunVerdict> _verdict;
    private string? _upstreamRunId;
    private bool _outputTruncated;

    public March7thRun(Guid runId, ISupervisedProcessGroup group, IProcessSupervisor supervisor,
        string cli, string directory, string task)
    {
        RunId = runId; _group = group; _supervisor = supervisor; _cli = cli; _directory = directory; _task = task;
        _verdict = ObserveAsync();
    }

    public Guid RunId { get; }
    public IAsyncEnumerable<RunEvent> Events => _events.Reader.ReadAllAsync();

    private async Task<RunVerdict> ObserveAsync()
    {
        var reader = Task.Run(async () =>
        {
            await foreach (var line in _group.OutputLines.ConfigureAwait(false))
            {
                lock (_output)
                {
                    if (_output.Count < 4000) _output.Add(line.Length <= 4000 ? line : line[..4000]);
                    else _outputTruncated = true;
                    _upstreamRunId = March7thTaskContract.CaptureRunId(_output);
                }
                _events.Writer.TryWrite(new RunEvent(RunId, DateTimeOffset.UtcNow, RunEventKind.Output,
                    line.Length <= 4000 ? line : line[..4000]));
            }
        });
        try
        {
            var exit = await _group.WaitForRootExitAsync(CancellationToken.None).ConfigureAwait(false);
            await reader.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            string? id; string output; bool truncated;
            lock (_output) { id = _upstreamRunId; output = string.Join('\n', _output); truncated = _outputTruncated; }
            if (id is null || truncated) return Unknown("上游运行编号或完整输出缺失。");
            var parsed = March7thTaskContract.ParseFinalRecord(output, _task, exit, id);
            var reason = parsed.Outcome == RunOutcome.Indeterminate ? ReasonCodes.NoCompletionEvidence
                : parsed.Outcome == RunOutcome.Failed ? ReasonCodes.ReportedFailure : ReasonCodes.ReportedCancel;
            return new RunVerdict(parsed.Outcome, reason, [new Evidence(RunId, EvidenceKind.Note, parsed.Detail, DateTimeOffset.UtcNow)]);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        { return Unknown("入口进程或日志采集未能确认终态。"); }
        finally { _events.Writer.TryComplete(); }
    }

    private RunVerdict Unknown(string detail) => new(RunOutcome.Indeterminate, ReasonCodes.NoCompletionEvidence,
        [new Evidence(RunId, EvidenceKind.Note, detail, DateTimeOffset.UtcNow)]);

    public Task<RunVerdict> WaitForVerdictAsync(CancellationToken ct) => _verdict.WaitAsync(ct);

    public async Task<StopResult> RequestStopAsync(StopMode mode, CancellationToken ct)
    {
        string? id;
        lock (_output) id = _upstreamRunId;
        if (id is null) return new(CleanupState.Unconfirmed, ReasonCodes.CleanupUnconfirmed);
        await using var control = await _supervisor.StartAsync(new ProcessLaunchSpec(_cli,
            March7thTaskContract.StopArguments(id), _directory), RunId, ct).ConfigureAwait(false);
        var exit = await control.WaitForRootExitAsync(ct).ConfigureAwait(false);
        var cleanup = await control.TerminateAllAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        var requested = exit == 0 && cleanup == CleanupState.Confirmed;
        return new(requested ? CleanupState.Pending : CleanupState.Unconfirmed,
            requested ? ReasonCodes.UserStop : ReasonCodes.CleanupUnconfirmed);
    }

    public async Task<CleanupState> ReconcileAsync(CancellationToken ct)
    {
        await _group.TerminateAllAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        // UAC may detach the worker from our Job. A successful CLI stop request alone cannot prove its exit.
        return CleanupState.Unconfirmed;
    }

    public ValueTask DisposeAsync() => _group.DisposeAsync();
}
