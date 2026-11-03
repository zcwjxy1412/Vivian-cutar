using System.Diagnostics;
using System.Threading.Channels;
using VIvian.Core;
using VIvian.Core.Execution;

namespace VIvian.Infrastructure.Simulation;

/// <summary>
/// Adapter for the FakeEngine test program. It exercises the full real path — process creation in a job object,
/// output parsing, evidence, stop and reconciliation — without any game. Results are always marked as simulation.
/// </summary>
public sealed class FakeEngineAdapter(GameId game, string engineExecutable, string scenario,
    TimeSpan? emergencyTimeout = null, TimeSpan? outputDrainTimeout = null) : IGameAdapter
{
    public GameId Game { get; } = game;
    public string RuleVersion => "fake-engine-1";
    public bool IsSimulation => true;
    private TimeSpan EmergencyTimeout { get; } = emergencyTimeout ?? TimeSpan.FromSeconds(10);
    private TimeSpan OutputDrainTimeout { get; } = outputDrainTimeout ?? TimeSpan.FromSeconds(2);

    public Task<PreflightResult> PreflightAsync(RunContext context, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(engineExecutable) || !File.Exists(engineExecutable))
            return Task.FromResult(PreflightResult.Block(ReasonCodes.NotConfigured, "模拟引擎程序不存在。", blocksQueue: false));
        // Detection only: an engine process that VIvian did not start blocks the queue. It is never touched.
        return Task.FromResult(ForeignProcessIds(engineExecutable).Count > 0
            ? PreflightResult.Block(ReasonCodes.ForeignProcess, "发现不属于本次运行的同名引擎进程，已停止队列，未对其做任何操作。")
            : PreflightResult.Ok());
    }

    public async Task<IEngineRun> StartAsync(RunContext context, IProcessSupervisor supervisor, CancellationToken ct)
    {
        var spec = new ProcessLaunchSpec(engineExecutable,
            ["--run-id", context.RunId.ToString("D"), "--scenario", scenario],
            Path.GetDirectoryName(engineExecutable)!);
        var group = await supervisor.StartAsync(spec, context.RunId, ct).ConfigureAwait(false);
        return new FakeEngineRun(context.RunId, group, EmergencyTimeout, OutputDrainTimeout);
    }

    /// <summary>Processes running the same executable file. Access-denied processes count as foreign (unknown = unsafe).</summary>
    public static IReadOnlyList<int> ForeignProcessIds(string executable)
    {
        var name = Path.GetFileNameWithoutExtension(executable);
        var result = new List<int>();
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is null || string.Equals(Path.GetFullPath(path), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase))
                        result.Add(process.Id);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    if (!process.HasExitedSafe()) result.Add(process.Id);
                }
            }
        }
        return result;
    }
}

internal static class ProcessExtensions
{
    public static bool HasExitedSafe(this Process process)
    {
        try { return process.HasExited; }
        catch (Exception) { return false; }
    }
}

/// <summary>Turns FakeEngine output lines into run events and evidence for one RunId.</summary>
public static class FakeEngineProtocol
{
    public const string Prefix = "VIVIAN-FAKE|";

    public sealed record Parsed(RunEvent Event, Evidence? Evidence);

    public static Parsed Parse(Guid runId, string line, DateTimeOffset at)
    {
        if (!line.StartsWith(Prefix, StringComparison.Ordinal))
            return new(new RunEvent(runId, at, RunEventKind.Output, line), null);
        var parts = line.Split('|', 4);
        if (parts.Length != 4 || !Guid.TryParse(parts[1], out var lineRun))
            return new(new RunEvent(runId, at, RunEventKind.Warning, "无法解析的模拟引擎输出：" + line), null);
        var (kind, text) = (parts[2], parts[3]);
        if (lineRun != runId)
        {
            // Stale or foreign output is recorded for diagnosis but carries no evidence for this run.
            return new(new RunEvent(runId, at, RunEventKind.Warning, $"忽略不属于本次运行的输出（{lineRun:D}）：{kind} {text}"), null);
        }
        if (kind != "evidence")
            return new(new RunEvent(runId, at, kind == "progress" ? RunEventKind.Progress : RunEventKind.Output, text), null);
        EvidenceKind? evidence = text switch
        {
            "completion" => EvidenceKind.CompletionReported,
            "failure" => EvidenceKind.FailureReported,
            "cancel" => EvidenceKind.CancelReported,
            _ => null,
        };
        return evidence is { } k
            ? new(new RunEvent(runId, at, RunEventKind.Evidence, text), new Evidence(runId, k, "fake-engine stdout", at))
            : new(new RunEvent(runId, at, RunEventKind.Warning, "未知的证据类型：" + text), null);
    }
}

internal sealed class FakeEngineRun : IEngineRun
{
    private readonly ISupervisedProcessGroup _group;
    private readonly TimeSpan _emergencyTimeout;
    private readonly TimeSpan _drainTimeout;
    private readonly Channel<RunEvent> _events = Channel.CreateUnbounded<RunEvent>();
    private readonly List<Evidence> _evidence = [];
    private readonly Task _reader;
    private readonly Task<RunVerdict> _verdict;

    public FakeEngineRun(Guid runId, ISupervisedProcessGroup group, TimeSpan emergencyTimeout, TimeSpan drainTimeout)
    {
        RunId = runId;
        _group = group;
        _emergencyTimeout = emergencyTimeout;
        _drainTimeout = drainTimeout;
        _events.Writer.TryWrite(new RunEvent(runId, DateTimeOffset.UtcNow, RunEventKind.Progress,
            $"模拟引擎已启动：PID {group.RootProcessId}，创建时间 {group.RootStartTime:O}"));
        _reader = ReadAsync();
        _verdict = DecideAsync();
    }

    public Guid RunId { get; }
    public IAsyncEnumerable<RunEvent> Events => _events.Reader.ReadAllAsync();

    private async Task ReadAsync()
    {
        try
        {
            await foreach (var line in _group.OutputLines.ConfigureAwait(false))
            {
                var parsed = FakeEngineProtocol.Parse(RunId, line, DateTimeOffset.UtcNow);
                if (parsed.Evidence is { } evidence) lock (_evidence) _evidence.Add(evidence);
                _events.Writer.TryWrite(parsed.Event);
            }
        }
        catch (Exception)
        {
            // Output errors carry no evidence; the verdict falls back to Indeterminate.
        }
    }

    private async Task<RunVerdict> DecideAsync()
    {
        var exitCode = await _group.WaitForRootExitAsync(CancellationToken.None).ConfigureAwait(false);
        // Output can stay open after the root exits (a descendant may hold the pipe); wait only briefly.
        await Task.WhenAny(_reader, Task.Delay(_drainTimeout)).ConfigureAwait(false);
        _events.Writer.TryWrite(new RunEvent(RunId, DateTimeOffset.UtcNow, RunEventKind.Output,
            $"模拟引擎主进程已退出，退出码 {exitCode}（退出码不作为成功依据）。"));
        _events.Writer.TryComplete();
        Evidence[] evidence;
        lock (_evidence) evidence = [.. _evidence];
        return VerdictPolicy.Decide(RunId, evidence);
    }

    public Task<RunVerdict> WaitForVerdictAsync(CancellationToken ct) => _verdict.WaitAsync(ct);

    public async Task<StopResult> RequestStopAsync(StopMode mode, CancellationToken ct)
    {
        if (mode == StopMode.Graceful)
        {
            var sent = await _group.SendInputLineAsync("stop", ct).ConfigureAwait(false);
            return new StopResult(CleanupState.Pending, sent ? "graceful_requested" : "graceful_unavailable");
        }
        var cleanup = await _group.TerminateAllAsync(_emergencyTimeout, ct).ConfigureAwait(false);
        return new StopResult(cleanup, "terminated_owned_processes");
    }

    public async Task<CleanupState> ReconcileAsync(CancellationToken ct)
    {
        // Leftover descendants (e.g. an orphaned child) are still ours: they are inside this run's job.
        return _group.ActiveProcessCount == 0
            ? CleanupState.Confirmed
            : await _group.TerminateAllAsync(_emergencyTimeout, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        await _group.DisposeAsync().ConfigureAwait(false);
    }
}
