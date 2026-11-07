namespace VIvian.Core.Execution;

/// <summary>Where a run currently is. Independent from <see cref="RunOutcome"/> and <see cref="CleanupState"/>.</summary>
public enum RunPhase { Pending, Preflight, Starting, Running, Stopping, Reconciling, Finished, Blocked }

/// <summary>What the run achieved. Null while the run is still in progress.</summary>
public enum RunOutcome { Success, Failed, Cancelled, Indeterminate }

/// <summary>Whether the run no longer occupies the machine. Only Confirmed/NotRequired allow the next task.</summary>
public enum CleanupState { NotRequired, Pending, Confirmed, Unconfirmed }

public enum StopMode { Graceful, Emergency }

public enum QueueStatus { Completed, Blocked, Cancelled }

public enum EvidenceKind
{
    /// <summary>Engine explicitly reported that the planned tasks completed.</summary>
    CompletionReported,
    /// <summary>Engine explicitly reported a terminal failure.</summary>
    FailureReported,
    /// <summary>Engine explicitly reported that it was cancelled.</summary>
    CancelReported,
    /// <summary>Informational only; never decides the outcome (e.g. a recovered error line).</summary>
    Note,
}

public static class ReasonCodes
{
    public const string Completed = "completed";
    public const string ReportedFailure = "reported_failure";
    public const string ResolutionCircuitOpen = "resolution_circuit_open";
    public const string ReportedCancel = "reported_cancel";
    public const string NoCompletionEvidence = "no_completion_evidence";
    public const string ConflictingEvidence = "conflicting_evidence";
    public const string StartFailed = "start_failed";
    public const string AdapterError = "adapter_error";
    public const string PreflightError = "preflight_error";
    public const string TaskTimeout = "task_timeout";
    public const string Stalled = "stalled";
    public const string UserStop = "user_stop";
    public const string CleanupUnconfirmed = "cleanup_unconfirmed";
    public const string InterruptedByCrash = "interrupted_by_crash";
    public const string ForeignProcess = "foreign_process";
    public const string NotConfigured = "not_configured";
    public const string SuccessWithoutEvidence = "success_without_evidence";
    public const string JournalUnavailable = "journal_unavailable";
    public const string UnresolvedRuns = "unresolved_runs";
    public const string InternalError = "internal_error";
    public const string AdapterUnresponsive = "adapter_unresponsive";
    public const string RealAdapterUnverified = "real_adapter_unverified";
}

public sealed record RunContext(Guid RunId, Guid QueueId, GameId Game, DateTimeOffset CreatedAt);

/// <summary>Preflight decision. <paramref name="BlocksQueue"/> stops the whole queue (e.g. an unowned engine
/// process is already running); otherwise only this game is skipped (e.g. not configured).</summary>
public sealed record PreflightResult(bool CanStart, string? ReasonCode, string? Detail, bool BlocksQueue)
{
    public static PreflightResult Ok() => new(true, null, null, false);
    public static PreflightResult Block(string reasonCode, string detail, bool blocksQueue = true)
        => new(false, reasonCode, detail, blocksQueue);
}

/// <summary>One piece of evidence. Evidence carrying another RunId is ignored (old logs, other instances).</summary>
public sealed record Evidence(Guid RunId, EvidenceKind Kind, string Source, DateTimeOffset At);

public sealed record RunVerdict(RunOutcome Outcome, string ReasonCode, IReadOnlyList<Evidence> Evidence);

public sealed record StopResult(CleanupState Cleanup, string ReasonCode);

public enum RunEventKind { Output, Progress, Evidence, Warning }

public sealed record RunEvent(Guid RunId, DateTimeOffset At, RunEventKind Kind, string Message);

/// <summary>Snapshot published whenever a run's phase, outcome or cleanup state changes.</summary>
public sealed record RunUpdate(
    Guid RunId, Guid QueueId, GameId Game, string RuleVersion, bool IsSimulation,
    RunPhase Phase, RunOutcome? Outcome, CleanupState Cleanup, string? ReasonCode, DateTimeOffset At);

public sealed record QueueItem
{
    public QueueItem(GameId game, IGameAdapter adapter, RunLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        if (adapter.Game != game) throw new ArgumentException("适配器与队列中的游戏不一致。", nameof(adapter));
        (Game, Adapter, Limits) = (game, adapter, limits);
    }
    public GameId Game { get; }
    public IGameAdapter Adapter { get; }
    public RunLimits? Limits { get; }
}

public sealed record QueueResult(Guid QueueId, QueueStatus Status, IReadOnlyList<RunUpdate> Runs,
    IReadOnlyList<GameId> NotStarted, bool JournalFaulted, string? BlockReason = null);

/// <summary>An acknowledgement was refused because an abandoned adapter call of the run has not returned yet.</summary>
public sealed class RunStillActiveException()
    : InvalidOperationException("该运行仍有未结束的适配器操作，可能仍占用电脑，暂时不能确认。");

/// <summary>Thrown by adapters when start fails. <paramref name="NothingStarted"/> must only be true when
/// the adapter is certain no process was created; otherwise cleanup is treated as unconfirmed.</summary>
public sealed class EngineStartException(string reasonCode, bool nothingStarted, string message)
    : Exception(message)
{
    public string ReasonCode { get; } = reasonCode;
    public bool NothingStarted { get; } = nothingStarted;
}

/// <summary>
/// Turns this run's evidence into an outcome. Exit codes, missing errors or a closed game window are
/// never treated as success: only an explicit terminal completion report for this RunId is.
/// </summary>
public static class VerdictPolicy
{
    public static RunVerdict Decide(Guid runId, IEnumerable<Evidence> evidence)
    {
        var own = evidence.Where(e => e.RunId == runId && e.Kind != EvidenceKind.Note).ToList();
        var kinds = own.Select(e => e.Kind).Distinct().ToList();
        if (kinds.Count > 1) return new(RunOutcome.Indeterminate, ReasonCodes.ConflictingEvidence, own);
        if (kinds.Count == 0) return new(RunOutcome.Indeterminate, ReasonCodes.NoCompletionEvidence, own);
        return kinds[0] switch
        {
            EvidenceKind.CompletionReported => new(RunOutcome.Success, ReasonCodes.Completed, own),
            EvidenceKind.FailureReported => new(RunOutcome.Failed, ReasonCodes.ReportedFailure, own),
            EvidenceKind.CancelReported => new(RunOutcome.Cancelled, ReasonCodes.ReportedCancel, own),
            _ => new(RunOutcome.Indeterminate, ReasonCodes.NoCompletionEvidence, own),
        };
    }
}

/// <summary>Guards the legal phase transitions of a single run and the invariants between the three layers.</summary>
public sealed class RunStateMachine
{
    private static readonly Dictionary<RunPhase, RunPhase[]> Allowed = new()
    {
        [RunPhase.Pending] = [RunPhase.Preflight, RunPhase.Finished],
        [RunPhase.Preflight] = [RunPhase.Starting, RunPhase.Blocked, RunPhase.Finished],
        [RunPhase.Starting] = [RunPhase.Running, RunPhase.Stopping, RunPhase.Reconciling],
        [RunPhase.Running] = [RunPhase.Stopping, RunPhase.Reconciling],
        [RunPhase.Stopping] = [RunPhase.Reconciling],
        [RunPhase.Reconciling] = [RunPhase.Finished, RunPhase.Blocked],
        [RunPhase.Finished] = [],
        [RunPhase.Blocked] = [],
    };

    public RunPhase Phase { get; private set; } = RunPhase.Pending;
    public RunOutcome? Outcome { get; private set; }
    public CleanupState Cleanup { get; private set; } = CleanupState.NotRequired;
    public string? ReasonCode { get; private set; }

    public static bool CanTransition(RunPhase from, RunPhase to) => Allowed[from].Contains(to);

    public bool IsTerminal => Phase is RunPhase.Finished or RunPhase.Blocked;

    public void MoveTo(RunPhase next)
    {
        if (!CanTransition(Phase, next))
            throw new InvalidOperationException($"Illegal run transition {Phase} -> {next}.");
        if (next == RunPhase.Finished)
        {
            if (Outcome is null) throw new InvalidOperationException("A finished run must have an outcome.");
            if (Cleanup is not (CleanupState.Confirmed or CleanupState.NotRequired))
                throw new InvalidOperationException("A run cannot finish while cleanup is not confirmed.");
        }
        if (next == RunPhase.Starting) Cleanup = CleanupState.Pending;
        Phase = next;
    }

    public void SetOutcome(RunOutcome outcome, string reasonCode)
    {
        if (Outcome is not null) throw new InvalidOperationException("Outcome is already decided.");
        if (Phase is RunPhase.Pending or RunPhase.Preflight && outcome != RunOutcome.Cancelled)
            throw new InvalidOperationException("Only cancellation can decide a run that never started.");
        Outcome = outcome;
        ReasonCode = reasonCode;
    }

    public void SetCleanup(CleanupState cleanup)
    {
        if (IsTerminal) throw new InvalidOperationException("Run is already terminal.");
        Cleanup = cleanup;
    }

    /// <summary>Records why a run is blocked when no outcome applies (e.g. preflight refused to start).</summary>
    public void SetBlockReason(string reasonCode) => ReasonCode = reasonCode;

    /// <summary>True when the next queued task may start after this run.</summary>
    public bool AllowsNextTask =>
        Phase == RunPhase.Finished
        && (Cleanup is CleanupState.Confirmed or CleanupState.NotRequired)
        && Outcome is not RunOutcome.Cancelled;
}
