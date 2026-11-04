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
    /// <summary>Writes one line to the root process's standard input. False if the engine no longer reads it.</summary>
    Task<bool> SendInputLineAsync(string line, CancellationToken ct);
    /// <summary>Number of processes alive in the ownership boundary. May include OS helpers the engine started
    /// implicitly (e.g. a console host), so it is not a count of engine processes.</summary>
    int ActiveProcessCount { get; }
    /// <summary>
    /// Whether the given process is inside this group's ownership boundary (VIvian's own job, not merely any job).
    /// Takes a handle, not a PID, so a reused PID cannot be mistaken for an owned process.
    /// </summary>
    bool OwnsProcess(Microsoft.Win32.SafeHandles.SafeProcessHandle process);
    /// <summary>Terminates every owned process and waits until none remain or the timeout elapses.</summary>
    Task<CleanupState> TerminateAllAsync(TimeSpan timeout, CancellationToken ct);
}

/// <summary>Receives run state changes (persistence, UI). Failures here never stop the safety logic.</summary>
public interface IRunJournal
{
    Task PublishAsync(RunUpdate update, CancellationToken ct);
    /// <summary>Stores a batch of events for one or more runs. Called from a single background writer,
    /// so slow storage never blocks the scheduler's safety paths.</summary>
    Task AppendEventsAsync(IReadOnlyList<RunEvent> events, CancellationToken ct);
    /// <summary>Runs whose cleanup is unconfirmed and not yet acknowledged, plus runs that never reached a terminal phase.
    /// While any exist, no new queue may start.</summary>
    Task<int> CountUnresolvedAsync(CancellationToken ct);
}

/// <summary>A stored run. The user's acknowledgement is kept separately and never rewrites the automatic verdict.</summary>
public sealed record RunRecord(
    Guid RunId, Guid QueueId, GameId Game, string RuleVersion, bool IsSimulation,
    RunPhase Phase, RunOutcome? Outcome, CleanupState Cleanup, string? ReasonCode,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? AcknowledgedAt, string? AcknowledgementNote)
{
    /// <summary>Unconfirmed cleanup not yet acknowledged, or a record that never reached a terminal phase.</summary>
    public bool IsUnresolved =>
        (Cleanup == CleanupState.Unconfirmed && AcknowledgedAt is null) || Phase is not (RunPhase.Finished or RunPhase.Blocked);
}

public interface IRunHistory
{
    Task<IReadOnlyList<RunRecord>> ReadRecentRunsAsync(int limit, CancellationToken ct = default);
    Task<IReadOnlyList<RunEvent>> ReadEventsAsync(Guid runId, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<RunRecord>> ReadUnresolvedAsync(CancellationToken ct = default);
    /// <summary>
    /// Call once at startup, only while holding the single-instance lock: runs left unfinished by a crash are
    /// closed without ever being marked successful. Started runs become Blocked with cleanup Unconfirmed.
    /// </summary>
    Task<IReadOnlyList<RunRecord>> ReconcileInterruptedRunsAsync(DateTimeOffset now, CancellationToken ct = default);
    /// <summary>The user confirms they checked the machine. Stored as a separate note; outcome and cleanup are unchanged.</summary>
    Task AcknowledgeAsync(Guid runId, string note, DateTimeOffset at, CancellationToken ct = default);
}
