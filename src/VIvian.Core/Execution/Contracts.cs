namespace VIvian.Core.Execution;

/// <summary>
/// Integration point for one automation engine. Adapters never locate or stop processes by name;
/// every process they create goes through <see cref="IProcessSupervisor"/> so ownership is provable.
/// </summary>
public interface IGameAdapter
{
    GameId Game { get; }
    /// <summary>Version of the adapter's detection rules; stored with every run record.</summary>
    string RuleVersion { get; }
    /// <summary>True for simulated engines. Simulated results must never be shown as real game results.</summary>
    bool IsSimulation { get; }
    Task<PreflightResult> PreflightAsync(RunContext context, CancellationToken ct);
    /// <summary>Starts the engine. On failure throw <see cref="EngineStartException"/>.</summary>
    Task<IEngineRun> StartAsync(RunContext context, IProcessSupervisor supervisor, CancellationToken ct);
}

public interface IEngineRun : IAsyncDisposable
{
    Guid RunId { get; }
    /// <summary>Events belonging to this run only. Completes when the engine has ended.</summary>
    IAsyncEnumerable<RunEvent> Events { get; }
    /// <summary>Completes when the engine has ended on its own, with an evidence-based verdict.</summary>
    Task<RunVerdict> WaitForVerdictAsync(CancellationToken ct);
    /// <summary>Graceful asks the engine to finish; Emergency stops owned processes immediately (no countdown).</summary>
    Task<StopResult> RequestStopAsync(StopMode mode, CancellationToken ct);
    /// <summary>After the engine ended, verifies no owned process remains (terminating leftovers it owns).</summary>
    Task<CleanupState> ReconcileAsync(CancellationToken ct);
}

public sealed record ProcessLaunchSpec(
    string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment = null);

/// <summary>Starts processes inside an ownership boundary (a Windows Job Object in production).</summary>
public interface IProcessSupervisor
{
    Task<ISupervisedProcessGroup> StartAsync(ProcessLaunchSpec spec, Guid runId, CancellationToken ct);
}

/// <summary>A started root process plus every descendant that stayed inside the ownership boundary.</summary>
public interface ISupervisedProcessGroup : IAsyncDisposable
{
    Guid RunId { get; }
    int RootProcessId { get; }
    /// <summary>Root process creation time; together with the PID it guards against PID reuse.</summary>
    DateTimeOffset RootStartTime { get; }
    /// <summary>Lines from the root process's standard output and error, in arrival order.</summary>
    IAsyncEnumerable<string> OutputLines { get; }
    Task<int> WaitForRootExitAsync(CancellationToken ct);
    /// <summary>Number of owned processes still alive (root and descendants).</summary>
    int ActiveProcessCount { get; }
    /// <summary>Terminates every owned process and waits until none remain or the timeout elapses.</summary>
    Task<CleanupState> TerminateAllAsync(TimeSpan timeout, CancellationToken ct);
}

/// <summary>Receives run state changes (persistence, UI). Failures here never stop the safety logic.</summary>
public interface IRunJournal
{
    Task PublishAsync(RunUpdate update, CancellationToken ct);
    Task AppendEventAsync(RunEvent runEvent, CancellationToken ct);
}
