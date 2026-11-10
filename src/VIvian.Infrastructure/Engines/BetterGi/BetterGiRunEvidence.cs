using VIvian.Core.Engines;
using VIvian.Core.Execution;

namespace VIvian.Infrastructure.Engines.BetterGi;

internal sealed record BetterGiRunObservation(int ProcessId, DateTimeOffset ProcessStartedAt, bool OwnedPrimary,
    bool FlowEnded, bool DailyRewardVerified, bool ForeignActivitySeen, bool AdverseSignal, int? ExitCode,
    bool DomainRewardsVerified, bool PlannedTasksVerified)
{
    public bool DailyRewardClaimUnconfirmed { get; init; }
    public BetterGiCraftAction CraftAction { get; init; }
    public long CraftActionTimestamp { get; init; }
    public int? CraftedCount { get; init; }
}

/// <summary>
/// Collects the attributable BetterGI markers of one run and turns them into a verdict through the shared
/// <see cref="CompletionEvidenceEvaluator"/>. Completion counts only when, for this run's own process:
/// the one-dragon flow was activated with the expected configuration, exactly one completion line followed the
/// activation, and required task outcomes were verified. Configuration fallback, cancellation, nothing-done, error
/// or fatal records contradict completion. Process exit or records from earlier runs do not prove completion.
/// </summary>
public sealed class BetterGiRunEvidence(Guid runId, BetterGiTaskRequest request, BetterGiAttribution attribution)
{
    private readonly Lock _gate = new();
    private readonly List<ObservedSignal> _signals = [];
    private readonly List<string> _activations = [];
    private int _completions;
    private bool _completionBeforeActivation;
    private bool _configMismatch;
    private BetterGiMarkerKind _dailyRewardCheck;
    private int _builtInFlowSteps;
    private bool _builtInSequenceInvalid;
    private string? _builtInSlot;
    private bool _craftStarted;
    private bool _craftEnded;
    private BetterGiCraftAction _craftAction;
    private long _craftActionTimestamp;
    private readonly bool _requiresDailyReward = request.OneDragonPlan?.EnabledTasks.Contains(BetterGiMarkers.DailyRewardTask) == true;
    private readonly bool _requiresCraft = request.OneDragonPlan?.EnabledTasks.Contains(BetterGiMarkers.CraftResinTask) == true;
    private readonly int _requiredDomains = request.OneDragonPlan?.EnabledTasks.Count(task => task == BetterGiMarkers.DomainTask) ?? 0;
    private int _taskIndex;
    private int _builtInTasksSeen;
    private int _verifiedDomains;
    private DomainEvidence? _activeDomain;

    private bool FlowEnded => _completions == 1 && _activations.Count == 1 && !_completionBeforeActivation && !_configMismatch;
    private bool TracksBuiltInFlow => _requiresDailyReward || _requiresCraft || _requiredDomains > 0;
    private bool DailyRewardVerified => FlowEnded && !_builtInSequenceInvalid && _dailyRewardCheck == BetterGiMarkerKind.DailyRewardVerified;
    private bool DomainRewardsVerified => _requiredDomains > 0 && _verifiedDomains == _requiredDomains
        && _taskIndex == request.OneDragonPlan!.EnabledTasks.Count;
    private bool PlannedTasksVerified => request.HasKnownCompletionContract && !_requiresCraft
        && (!_requiresDailyReward || DailyRewardVerified) && (_requiredDomains == 0 || DomainRewardsVerified);

    public Guid RunId => runId;

    /// <summary>A BetterGI process other than the owned root wrote records after this run started.</summary>
    public bool ForeignActivitySeen { get; private set; }

    internal BetterGiRunObservation Observe(int? exitCode)
    {
        lock (_gate)
            return new(attribution.RootProcessId, attribution.RootStartTime, OwnedPrimarySeen, FlowEnded, DailyRewardVerified,
                ForeignActivitySeen, _signals.Count != 0, exitCode, DomainRewardsVerified, PlannedTasksVerified)
            {
                DailyRewardClaimUnconfirmed = FlowEnded && !ForeignActivitySeen && !_builtInSequenceInvalid &&
                    _dailyRewardCheck == BetterGiMarkerKind.DailyRewardUnverified,
                CraftAction = _requiresCraft && (_builtInSequenceInvalid || !_craftEnded ||
                    _builtInFlowSteps != request.OneDragonPlan!.EnabledTasks.Count) ? BetterGiCraftAction.Conflict : _craftAction,
                CraftActionTimestamp = _craftActionTimestamp,
            };
    }

    public int IgnoredRecords { get; private set; }
    internal bool OwnedPrimarySeen { get; private set; }

    public enum Disposition { Owned, Historical, Foreign }

    public sealed record Accepted(Disposition Disposition, BetterGiMarker Marker);

    /// <summary>Classifies a record and, when it belongs to this run, records its marker.</summary>
    public Accepted Add(BetterGiLogRecord record, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(record);
        var marker = BetterGiMarkers.Classify(record);
        lock (_gate)
        {
            if (!attribution.Owns(record))
            {
                IgnoredRecords++;
                // Written by an instance that started no earlier than our root: some BetterGI process we do not own
                // is active (another copy, or an elevated self-restart that left the job).
                var recent = record.InstanceStartedAtUnixMs >= (attribution.RootStartTime - attribution.Tolerance).ToUnixTimeMilliseconds();
                if (recent && record.ProcessId != attribution.RootProcessId) ForeignActivitySeen = true;
                return new(recent ? Disposition.Foreign : Disposition.Historical, marker);
            }
            OwnedPrimarySeen = true;
            switch (marker.Kind)
            {
                case BetterGiMarkerKind.Activated when !TracksBuiltInFlow || IsFlowRecord(record):
                    _activations.Add(marker.Value ?? "");
                    if (request.Kind == BetterGiTaskKind.OneDragonNamed && !string.Equals(marker.Value, request.ConfigName, StringComparison.Ordinal))
                    {
                        // A different configuration ran: its completion line says nothing about the requested one.
                        _configMismatch = true;
                    }
                    break;
                case BetterGiMarkerKind.Progress when TracksBuiltInFlow && IsFlowRecord(record) &&
                    _activations.Count == 1 && _completions == 0:
                    var builtIn = record.Message.StartsWith(BetterGiMarkers.BuiltInProgressPrefix, StringComparison.Ordinal);
                    if (_requiresDailyReward || _requiresCraft) RecordBuiltInStep(marker, builtIn);
                    if (_requiredDomains > 0) StartTask(marker, builtIn);
                    break;
                case BetterGiMarkerKind.DailyRewardVerified:
                case BetterGiMarkerKind.DailyRewardUnverified:
                    if (_requiresDailyReward && _completions == 0)
                    {
                        if (_activations.Count != 1 || _dailyRewardCheck != default ||
                            _builtInFlowSteps != request.OneDragonPlan!.EnabledTasks.Count)
                            _builtInSequenceInvalid = true;
                        _dailyRewardCheck = marker.Kind;
                    }
                    break;
                case BetterGiMarkerKind.CraftStarted:
                case BetterGiMarkerKind.CraftAttempt:
                case BetterGiMarkerKind.CraftSkipped:
                case BetterGiMarkerKind.CraftEnded:
                    if (_requiresCraft) RecordCraftAction(marker.Kind);
                    break;
                case BetterGiMarkerKind.Completed when !TracksBuiltInFlow || IsFlowRecord(record):
                    FinishActiveDomain();
                    _completions++;
                    if (_activations.Count == 0) _completionBeforeActivation = true;
                    break;
                case BetterGiMarkerKind.DomainStarted:
                case BetterGiMarkerKind.DomainTarget:
                case BetterGiMarkerKind.DomainRewardAttempt:
                case BetterGiMarkerKind.DomainResinProgress:
                case BetterGiMarkerKind.DomainRewardVerified:
                case BetterGiMarkerKind.DomainRewardUnverified:
                case BetterGiMarkerKind.DomainEnded:
                    _activeDomain?.Add(marker);
                    break;
                case BetterGiMarkerKind.ConfigNotFound:
                    _signals.Add(new(runId, SignalKind.FailureMarker, "BetterGI 日志：未找到指定的一条龙配置（会改为运行当前选中配置）", at));
                    break;
                case BetterGiMarkerKind.Cancelled:
                    _signals.Add(new(runId, SignalKind.CancelMarker, "BetterGI 日志：任务被取消", at));
                    break;
                case BetterGiMarkerKind.NothingDone:
                    _signals.Add(new(runId, SignalKind.NothingDone, "BetterGI 日志：没有可执行的任务", at));
                    break;
                case BetterGiMarkerKind.Failure:
                    _signals.Add(new(runId, SignalKind.FailureMarker, $"BetterGI 日志：{record.Level} 记录", at));
                    break;
                case BetterGiMarkerKind.Fatal:
                    _signals.Add(new(runId, SignalKind.FatalError, "BetterGI 日志：致命异常", at));
                    break;
            }
            return new(Disposition.Owned, marker);
        }
    }

    private static bool IsFlowRecord(BetterGiLogRecord record) => record.SourceContext == BetterGiMarkers.FlowSource && record.Level == "INF";

    private void RecordBuiltInStep(BetterGiMarker marker, bool builtIn)
    {
        var tasks = request.OneDragonPlan!.EnabledTasks;
        _builtInFlowSteps++;
        if (!builtIn || _dailyRewardCheck != default || marker.Done != _builtInFlowSteps || marker.Total != tasks.Count ||
            _builtInFlowSteps > tasks.Count || (_craftStarted && !_craftEnded))
        {
            _builtInSequenceInvalid = true;
            _builtInSlot = null;
            return;
        }
        _builtInSlot = tasks[_builtInFlowSteps - 1];
    }

    private void RecordCraftAction(BetterGiMarkerKind kind)
    {
        if (_completions != 0 || _builtInSlot != BetterGiMarkers.CraftResinTask || _craftEnded)
        {
            _craftAction = BetterGiCraftAction.Conflict;
            return;
        }
        switch (kind)
        {
            case BetterGiMarkerKind.CraftStarted when !_craftStarted:
                _craftStarted = true;
                break;
            case BetterGiMarkerKind.CraftAttempt or BetterGiMarkerKind.CraftSkipped when _craftStarted && _craftAction == BetterGiCraftAction.None:
                _craftAction = kind == BetterGiMarkerKind.CraftAttempt ? BetterGiCraftAction.Attempt : BetterGiCraftAction.Skipped;
                _craftActionTimestamp = TimeProvider.System.GetTimestamp();
                break;
            case BetterGiMarkerKind.CraftEnded when _craftAction is BetterGiCraftAction.Attempt or BetterGiCraftAction.Skipped:
                _craftEnded = true;
                break;
            default:
                _craftAction = BetterGiCraftAction.Conflict;
                break;
        }
    }

    private void StartTask(BetterGiMarker progress, bool builtIn)
    {
        FinishActiveDomain();
        var plan = request.OneDragonPlan!;
        var index = _taskIndex++;
        if (!builtIn) return;

        _builtInTasksSeen++;
        // The pinned flow logs an ordinal for built-ins and a separate ordinal for groups. Each enabled slot
        // emits one of those records, and the built-in progress denominator includes both kinds of task.
        if (progress.Done == _builtInTasksSeen && progress.Total == plan.EnabledTasks.Count
            && index < plan.EnabledTasks.Count && plan.EnabledTasks[index] == BetterGiMarkers.DomainTask
            && !string.IsNullOrEmpty(plan.DomainName) && request.DomainResinPlan is { HasExplicitBudget: true } budget)
            _activeDomain = new DomainEvidence(plan.DomainName, budget);
    }

    private void FinishActiveDomain()
    {
        if (_activeDomain?.Verified == true) _verifiedDomains++;
        _activeDomain = null;
    }

    /// <summary>
    /// Builds the verdict once the root process has ended.
    /// </summary>
    /// <param name="exitCode">Root exit code, or null when unknown.</param>
    /// <param name="stoppedByVivian">VIvian terminated the process: its exit code says nothing about the engine.</param>
    /// <param name="detached">Ownership was lost (elevated self-restart): no success is possible.</param>
    /// <param name="contractVerified">The completion signals were verified on the real release.</param>
    public RunVerdict Evaluate(int? exitCode, bool stoppedByVivian, bool detached, bool contractVerified, DateTimeOffset at)
    {
        List<ObservedSignal> signals;
        var taskNotes = new List<Evidence>();
        lock (_gate)
        {
            signals = [.. _signals];
            if (request.HasKnownCompletionContract && FlowEnded)
            {
                // GoToCraftingBenchTask @0af68c2 logs the attempt before confirming and never checks inventory after it.
                if (_requiresCraft)
                    taskNotes.Add(new(runId, EvidenceKind.Note, "BetterGI 合成流程缺少浓缩树脂入包核查。", at));
                if (_requiredDomains > 0 && !DomainRewardsVerified)
                    taskNotes.Add(new(runId, EvidenceKind.Note, "BetterGI 秘境流程缺少与目标及树脂计划相符的逐轮领奖核查。", at));
                if (PlannedTasksVerified)
                {
                    var description = _requiresDailyReward
                        ? "BetterGI 日志：流程结束且本次检查确认每日奖励已领取"
                        : "BetterGI 日志：一条龙和配置组任务结束";
                    if (_requiredDomains > 0) description += "，指定秘境的树脂计划与逐轮奖励核查一致";
                    signals.Add(new(runId, SignalKind.CompletionMarker, description, at));
                }
            }
            if (detached)
                signals.Add(new(runId, SignalKind.FailureMarker, "BetterGI 任务可能离开受监管进程，执行与清理均未确认", at));
            else if (stoppedByVivian)
                signals.Add(new(runId, SignalKind.ProcessExit, "BetterGI 主进程（由 VIvian 终止，退出码不作为证据）", at, ExitCode: null));
            else if (exitCode is { } code)
                signals.Add(new(runId, SignalKind.ProcessExit, "BetterGI 主进程", at, code));
        }
        var verdict = CompletionEvidenceEvaluator.Evaluate(runId, contractVerified && request.HasKnownCompletionContract, signals);
        if (taskNotes.Count > 0) verdict = verdict with { Evidence = [.. verdict.Evidence, .. taskNotes] };
        // Losing ownership is never reported as a plain engine failure: what the detached process does is unknown.
        if (detached && verdict.Outcome != RunOutcome.Indeterminate)
            return verdict with { Outcome = RunOutcome.Indeterminate, ReasonCode = ReasonCodes.CleanupUnconfirmed };
        return verdict;
    }

    private sealed class DomainEvidence(string expectedTarget, BetterGiDomainResinPlan budget)
    {
        private enum Stage { Start, Target, Round, Resin, Reward, Ended, Invalid }

        private readonly Dictionary<string, int> _expected = new(StringComparer.Ordinal)
        {
            ["原粹树脂"] = budget.Original,
            ["原粹树脂20"] = budget.Original20,
            ["原粹树脂40"] = budget.Original40,
            ["浓缩树脂"] = budget.Condensed,
            ["须臾树脂"] = budget.Transient,
            ["脆弱树脂"] = budget.Fragile,
        };
        private readonly Dictionary<string, int> _claimed = new(StringComparer.Ordinal);
        private Stage _stage;
        private string? _pendingResin;

        public bool Verified => _stage == Stage.Ended;

        public void Add(BetterGiMarker marker)
        {
            switch (marker.Kind)
            {
                case BetterGiMarkerKind.DomainStarted when _stage == Stage.Start:
                    _stage = Stage.Target;
                    break;
                case BetterGiMarkerKind.DomainTarget when _stage == Stage.Target && marker.Value == expectedTarget:
                    _stage = Stage.Round;
                    break;
                case BetterGiMarkerKind.DomainRewardAttempt when _stage == Stage.Round:
                    _stage = Stage.Resin;
                    break;
                case BetterGiMarkerKind.DomainResinProgress when _stage == Stage.Resin && marker.Value is { } resin
                    && _expected.TryGetValue(resin, out var total) && marker.Total == total
                    && marker.Done is { } done && done <= total && done - 1 == _claimed.GetValueOrDefault(resin):
                    _pendingResin = resin;
                    _stage = Stage.Reward;
                    break;
                case BetterGiMarkerKind.DomainRewardVerified when _stage == Stage.Reward && _pendingResin is { } pending:
                    _claimed[pending] = _claimed.GetValueOrDefault(pending) + 1;
                    _pendingResin = null;
                    _stage = Stage.Round;
                    break;
                case BetterGiMarkerKind.DomainEnded when _stage == Stage.Round
                    && _expected.All(pair => _claimed.GetValueOrDefault(pair.Key) == pair.Value):
                    _stage = Stage.Ended;
                    break;
                default:
                    _stage = Stage.Invalid;
                    break;
            }
        }
    }
}
