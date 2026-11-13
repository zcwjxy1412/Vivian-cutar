namespace VIvian.Core.Execution;

public sealed record ExecutionPlanRequestItem(GameId Game, Guid AccountId);

/// <summary>All labels and revisions are read by the host, never trusted from model output.</summary>
public sealed record ExecutionPlanItem(int Ordinal, GameId Game, Guid AccountId, string AccountName,
    string TaskId, string ConfigurationRevision, long TaskPlanRevision, string ResourcePolicy, string ResourceSummary);

/// <summary>Order is significant. Every plan uses stop-on-failure; no automatic replay.</summary>
public sealed record PreparedExecutionPlan(Guid PlanId, DateTimeOffset ExpiresAt, IReadOnlyList<ExecutionPlanItem> Items);

public enum ExecutionPlanState { Accepted, Running, Succeeded, Stopped, Interrupted }
public enum ExecutionPlanItemState { Pending, Running, Succeeded, Failed, Cancelled, Blocked, Interrupted, Skipped }
public enum ExecutionPlanDisposition { Accepted, Existing, Rejected, Conflict }

public sealed record ExecutionPlanPreparation(PreparedExecutionPlan? Plan, string? ReasonCode);
public sealed record ExecutionPlanSubmission(ExecutionPlanDisposition Disposition, ExecutionPlanReceipt? Receipt,
    string? ReasonCode = null);

public sealed record ExecutionPlanItemResult(RunOutcome Outcome, CleanupState Cleanup,
    bool ConfigurationRestored, string ReasonCode)
{
    public bool AllowsNext => Outcome == RunOutcome.Success &&
        (Cleanup is CleanupState.Confirmed or CleanupState.NotRequired) && ConfigurationRestored;
}

/// <summary>RunIds are durable pre-launch reservations; association alone never proves an engine started.</summary>
public sealed record ExecutionPlanItemReceipt(ExecutionPlanItem Item, Guid ChildBatchId,
    ExecutionPlanItemState State, IReadOnlyList<Guid> RunIds, ExecutionPlanItemResult? Result);

public sealed record ExecutionPlanReceipt(PreparedExecutionPlan Plan, string PayloadHash, ExecutionPlanState State,
    DateTimeOffset AcceptedAt, DateTimeOffset UpdatedAt, IReadOnlyList<ExecutionPlanItemReceipt> Items,
    string? ReasonCode = null);

/// <summary>Confirmation UI prepares and submits through the host. Cancellation never owns accepted execution.</summary>
public interface IExecutionPlanService
{
    Task<ExecutionPlanPreparation> PrepareAsync(IReadOnlyList<ExecutionPlanRequestItem> items, CancellationToken ct);
    Task<ExecutionPlanSubmission> SubmitConfirmedAsync(PreparedExecutionPlan plan, CancellationToken ct);
    Task<ExecutionPlanReceipt?> ReadAsync(Guid planId, CancellationToken ct);
}

/// <summary>Atomic durable operations. All IDs are exact; mutations reject invalid state transitions.</summary>
public interface IExecutionPlanStore
{
    Task<ExecutionPlanSubmission> AcceptAsync(PreparedExecutionPlan plan, CancellationToken ct = default);
    Task<ExecutionPlanReceipt?> ReadAsync(Guid planId, CancellationToken ct = default);
    Task<bool> TryStartAsync(Guid planId, CancellationToken ct = default);
    Task StartItemAsync(Guid planId, int ordinal, CancellationToken ct = default);
    /// <summary>Must be awaited before starting an engine or applying temporary configuration.</summary>
    Task RegisterRunAsync(Guid planId, int ordinal, Guid runId, CancellationToken ct = default);
    Task CompleteItemAsync(Guid planId, int ordinal, ExecutionPlanItemResult result, CancellationToken ct = default);
    Task CompleteAsync(Guid planId, ExecutionPlanState state, string? reasonCode, CancellationToken ct = default);
    /// <summary>Only at host startup while owning the profile's single-instance guard. Never schedules work.</summary>
    Task<int> InterruptUnfinishedAsync(CancellationToken ct = default);
}
