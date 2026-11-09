using VIvian.Core.Engines;
using VIvian.Core.Execution;

namespace VIvian.Infrastructure.Engines.BetterGi;

internal sealed record BetterGiRunObservation(int ProcessId, DateTimeOffset ProcessStartedAt, bool OwnedPrimary,
    bool FlowEnded, bool DailyRewardVerified, bool ForeignActivitySeen, bool AdverseSignal, int? ExitCode);

/// <summary>
/// Collects the attributable BetterGI markers of one run and turns them into a verdict through the shared
/// <see cref="CompletionEvidenceEvaluator"/>. Completion counts only when, for this run's own process:
/// the one-dragon flow was activated (with the requested configuration, if one was named), exactly one completion
/// line followed the activation, and no configuration fallback, cancellation, nothing-done, error or fatal record
/// appeared. Process exit alone, or a completion line from any other process or an earlier run, never counts.
/// </summary>


public sealed class BetterGiRunEvidence(Guid runId, BetterGiTaskRequest request, BetterGiAttribution attribution)
{
    private readonly Lock _gate = new();
    private readonly List<ObservedSignal> _signals = [];
    private readonly List<string> _activations = [];
    private int _completions;
    private bool _completionBeforeActivation;
    private bool _configMismatch;
    private bool _progressSeen;
    private BetterGiMarkerKind _dailyRewardCheck;
    private readonly bool _requiresDailyReward = request.OneDragonPlan?.EnabledTasks.Contains(BetterGiMarkers.DailyRewardTask) == true;

    public Guid RunId => runId;

    /// <summary>A BetterGI process other than the owned root wrote records after this run started.</summary>
    public bool ForeignActivitySeen { get; private set; }

    internal BetterGiRunObservation Observe(int? exitCode)
    {
        lock (_gate)
            return new(attribution.RootProcessId, attribution.RootStartTime, OwnedPrimarySeen, _completions == 1 && _activations.Count == 1 && !_completionBeforeActivation && !_configMismatch,
                _dailyRewardCheck == BetterGiMarkerKind.DailyRewardVerified, ForeignActivitySeen, _signals.Count != 0, exitCode);
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
                case BetterGiMarkerKind.Activated:
                    _activations.Add(marker.Value ?? "");
                    if (request.Kind == BetterGiTaskKind.OneDragonNamed && !string.Equals(marker.Value, request.ConfigName, StringComparison.Ordinal))
                    {
                        // A different configuration ran: its completion line says nothing about the requested one.
                        _configMismatch = true;
                    }
                    break;
                case BetterGiMarkerKind.Progress:
                    _progressSeen = _activations.Count == 1;
                    break;
                case BetterGiMarkerKind.DailyRewardVerified:
                case BetterGiMarkerKind.DailyRewardUnverified:
                    if (_progressSeen && _completions == 0) _dailyRewardCheck = marker.Kind;
                    break;
                case BetterGiMarkerKind.Completed:
                    _completions++;
                    if (_activations.Count == 0) _completionBeforeActivation = true;
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
        lock (_gate)
        {
            signals = [.. _signals];
            var completionValid = request.HasKnownCompletionContract
                && _completions == 1 && _activations.Count == 1 && !_completionBeforeActivation && !_configMismatch;
            if (completionValid && (!_requiresDailyReward || _dailyRewardCheck == BetterGiMarkerKind.DailyRewardVerified))
                signals.Add(new(runId, SignalKind.CompletionMarker, _requiresDailyReward
                    ? "BetterGI 日志：流程结束且本次检查确认每日奖励已领取"
                    : "BetterGI 日志：一条龙和配置组任务结束", at));
            if (detached)
                signals.Add(new(runId, SignalKind.FailureMarker, "BetterGI 任务可能离开受监管进程，执行与清理均未确认", at));
            else if (stoppedByVivian)
                signals.Add(new(runId, SignalKind.ProcessExit, "BetterGI 主进程（由 VIvian 终止，退出码不作为证据）", at, ExitCode: null));
            else if (exitCode is { } code)
                signals.Add(new(runId, SignalKind.ProcessExit, "BetterGI 主进程", at, code));
        }
        var verdict = CompletionEvidenceEvaluator.Evaluate(runId, contractVerified && request.HasKnownCompletionContract, signals);
        // Losing ownership is never reported as a plain engine failure: what the detached process does is unknown.
        if (detached && verdict.Outcome != RunOutcome.Indeterminate)
            return verdict with { Outcome = RunOutcome.Indeterminate, ReasonCode = ReasonCodes.CleanupUnconfirmed };
        return verdict;
    }
}
