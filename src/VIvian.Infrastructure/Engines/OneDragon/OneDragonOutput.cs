using System.Text.RegularExpressions;
using VIvian.Core.Engines;
using VIvian.Core.Execution;

namespace VIvian.Infrastructure.Engines.OneDragon;

public enum OneDragonMarkerKind
{
    None,
    EngagementClaim,
    EngagementMaxed,
    EngagementEnded,
    EngagementSkipped,
    EngagementFailed,
    /// <summary>"OneDragon 集成启动器 {version}" — the launcher process itself is running the one-dragon code.</summary>
    RuntimeStarted,
    /// <summary>"未开启代码自动更新，跳过" — no code was fetched before the run.</summary>
    AutoUpdateSkipped,
    /// <summary>"首次运行，正在同步代码仓库..." or "正在检查代码更新..." — upstream code may be replaced before the run.</summary>
    AutoUpdateAttempted,
    /// <summary>"指定运行实例: [..]" — the account instances OneDragon accepted.</summary>
    InstancesSelected,
    /// <summary>"指令[ 一条龙 ] 执行成功 …" at INFO — the one-dragon application finished successfully.</summary>
    Completed,
    /// <summary>An ERROR/CRITICAL record, a traceback, "指令[ 一条龙 ] 执行失败", or a launcher argument error.</summary>
    Failure,
}

public sealed record OneDragonMarker(OneDragonMarkerKind Kind, string? Value = null, string? Level = null);

/// <summary>
/// Classifies lines from the launcher's own standard streams. The pinned console format is
/// <c>[%H:%M:%S.mmm] [file line] [LEVEL]: message</c> (log_utils.py @5f2e473, level B). The file log (.log\log.txt)
/// carries no process id, so it is never used as evidence: only output from the process VIvian started counts, which
/// makes every marker attributable by construction. All strings are level B and unverified on the real release.
/// </summary>
public static partial class OneDragonMarkers
{
    public const string RuntimeStartedPrefix = "OneDragon 集成启动器 ";
    public const string AutoUpdateSkippedLine = "未开启代码自动更新，跳过";
    public const string FirstSyncLine = "首次运行，正在同步代码仓库...";
    public const string CheckUpdateLine = "正在检查代码更新...";
    public const string InstancesPrefix = "指定运行实例: ";
    public const string CompletedPrefix = "指令[ 一条龙 ] 执行成功";
    public const string FailedPrefix = "指令[ 一条龙 ] 执行失败";
    public const string NoValidInstanceLine = "未找到有效的实例";
    public const string ArgumentErrorPrefix = "错误：参数";
    public const string TracebackLine = "Traceback (most recent call last):";

    [GeneratedRegex(@"^\[\d{2}:\d{2}:\d{2}\.\d{3}\] \[(?<source>[^\]\r\n]{1,128})\] \[(?<level>DEBUG|INFO|WARNING|ERROR|CRITICAL)\]: (?<message>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Record();

    public static OneDragonMarker Classify(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var trimmed = line.TrimEnd('\r');
        if (trimmed.StartsWith(TracebackLine, StringComparison.Ordinal)) return new(OneDragonMarkerKind.Failure);
        if (trimmed.StartsWith(ArgumentErrorPrefix, StringComparison.Ordinal)) return new(OneDragonMarkerKind.Failure);
        var match = Record().Match(trimmed);
        if (!match.Success) return new(OneDragonMarkerKind.None);
        var level = match.Groups["level"].Value;
        var message = match.Groups["message"].Value;
        if (match.Groups["source"].Value.StartsWith("operation.py ", StringComparison.Ordinal)
            && ClassifyEngagement(level, message) is { } engagement)
            return new(engagement, message, level);
        if (level is "ERROR" or "CRITICAL") return new(OneDragonMarkerKind.Failure, message, level);
        if (message.StartsWith(FailedPrefix, StringComparison.Ordinal) || message == NoValidInstanceLine)
            return new(OneDragonMarkerKind.Failure, message, level);
        if (message.StartsWith(CompletedPrefix, StringComparison.Ordinal))
            return level == "INFO" ? new(OneDragonMarkerKind.Completed, message, level) : new(OneDragonMarkerKind.Failure, message, level);
        if (message.StartsWith(RuntimeStartedPrefix, StringComparison.Ordinal))
            return new(OneDragonMarkerKind.RuntimeStarted, message[RuntimeStartedPrefix.Length..].Trim(), level);
        if (message == AutoUpdateSkippedLine) return new(OneDragonMarkerKind.AutoUpdateSkipped, null, level);
        if (message is FirstSyncLine or CheckUpdateLine) return new(OneDragonMarkerKind.AutoUpdateAttempted, null, level);
        if (message.StartsWith(InstancesPrefix, StringComparison.Ordinal))
            return new(OneDragonMarkerKind.InstancesSelected, message[InstancesPrefix.Length..].Trim(), level);
        return new(OneDragonMarkerKind.None, message, level);
    }
    private static OneDragonMarkerKind? ClassifyEngagement(string level, string message)
    {
        if (level == "ERROR" && message.StartsWith("指令[ 活跃度奖励 ] 执行失败 返回状态 ", StringComparison.Ordinal))
            return OneDragonMarkerKind.EngagementFailed;
        if (level != "INFO") return null;
        if (message.StartsWith("指令[ 执行应用组 one_dragon ] 节点 ", StringComparison.Ordinal)
            && message.EndsWith("执行应用 返回状态 应用已完成 活跃度奖励", StringComparison.Ordinal))
            return OneDragonMarkerKind.EngagementSkipped;
        if (message.StartsWith("指令[ 活跃度奖励 ] 执行成功 返回状态 ", StringComparison.Ordinal))
            return OneDragonMarkerKind.EngagementEnded;
        return message switch
        {
            "指令[ 活跃度奖励 ] 节点 点击奖励 -> 查看奖励结果 返回状态 日常奖励领取成功" =>
                OneDragonMarkerKind.EngagementClaim,
            "指令[ 活跃度奖励 ] 节点 查看奖励结果 -> 识别活跃度 返回状态 活跃度已满" =>
                OneDragonMarkerKind.EngagementMaxed,
            _ => null,
        };
    }

}

public enum OneDragonEngagementResult
{
    NoEvidence,
    ClaimConfirmed,
    RewardUnconfirmed,
    SkippedByUpstreamRecord,
    Failed,
    Conflicting,
}

internal sealed class OneDragonEngagementEvidence
{
    private bool _claim;
    private bool _maxed;
    private bool _ended;
    private bool _skipped;
    private bool _failed;
    private bool _conflicting;

    internal OneDragonEngagementResult Result =>
        _failed ? OneDragonEngagementResult.Failed
        : _conflicting ? OneDragonEngagementResult.Conflicting
        : _skipped ? OneDragonEngagementResult.SkippedByUpstreamRecord
        : _ended && _maxed
            ? (_claim ? OneDragonEngagementResult.ClaimConfirmed : OneDragonEngagementResult.RewardUnconfirmed)
            : OneDragonEngagementResult.NoEvidence;

    internal void Add(OneDragonMarkerKind kind)
    {
        switch (kind)
        {
            case OneDragonMarkerKind.EngagementClaim:
                _conflicting |= _claim || _maxed || _ended || _skipped;
                _claim = true;
                break;
            case OneDragonMarkerKind.EngagementMaxed:
                _conflicting |= _maxed || _ended || _skipped;
                _maxed = true;
                break;
            case OneDragonMarkerKind.EngagementEnded:
                _conflicting |= _ended || _skipped;
                _ended = true;
                break;
            case OneDragonMarkerKind.EngagementSkipped:
                _conflicting |= _skipped || _claim || _maxed || _ended;
                _skipped = true;
                break;
            case OneDragonMarkerKind.EngagementFailed:
                _failed = true;
                break;
        }
    }
}

/// <summary>
/// Evidence of one OneDragon run, from the owned launcher's output only. Success requires, in this order of facts:
/// the launcher ran the one-dragon code in its own process (start line with the pinned version), code auto-update
/// was skipped, the requested instances (if any) were echoed back exactly, exactly one one-dragon success line, no
/// failure line, and the launcher exiting on its own with code 0. The launcher exiting is never enough by itself:
/// without the in-process start line the work may have been handed to an elevated copy outside VIvian's job.
/// </summary>
public sealed class OneDragonRunEvidence(Guid runId, OneDragonTaskRequest request, string pinnedVersion)
{
    private readonly Lock _gate = new();
    private readonly List<ObservedSignal> _signals = [];
    private readonly OneDragonEngagementEvidence _engagement = new();
    private int _completions;
    private bool _completionBeforeStart;
    private bool _instancesMatched;
    private bool _instancesMismatched;

    public bool InProcessStartSeen { get; private set; }
    public bool VersionMatched { get; private set; }
    public bool AutoUpdateSkipped { get; private set; }
    public bool AutoUpdateAttempted { get; private set; }

    public OneDragonEngagementResult EngagementResult
    {
        get { lock (_gate) return _engagement.Result; }
    }

    public OneDragonMarker Add(string line, DateTimeOffset at)
    {
        var marker = OneDragonMarkers.Classify(line);
        lock (_gate)
        {
            if (request.EngagementRewardOnly && InProcessStartSeen && VersionMatched && AutoUpdateSkipped
                && !AutoUpdateAttempted && _instancesMatched && !_instancesMismatched && _completions == 0)
                _engagement.Add(marker.Kind);
            switch (marker.Kind)
            {
                case OneDragonMarkerKind.RuntimeStarted:
                    InProcessStartSeen = true;
                    VersionMatched = EngineReadinessInspector.VersionMatches(pinnedVersion, marker.Value);
                    break;
                case OneDragonMarkerKind.AutoUpdateSkipped:
                    AutoUpdateSkipped = true;
                    break;
                case OneDragonMarkerKind.AutoUpdateAttempted:
                    AutoUpdateAttempted = true;
                    break;
                case OneDragonMarkerKind.InstancesSelected:
                    if (request.ExpectedInstanceEcho is { } expected && marker.Value == expected) _instancesMatched = true;
                    else _instancesMismatched = true;
                    break;
                case OneDragonMarkerKind.Completed:
                    _completions++;
                    if (!InProcessStartSeen) _completionBeforeStart = true;
                    break;
                case OneDragonMarkerKind.EngagementFailed:
                case OneDragonMarkerKind.Failure:
                    _signals.Add(new(runId, SignalKind.FailureMarker, "OneDragon 输出：失败或异常记录", at));
                    break;
            }
        }
        return marker;
    }

    /// <param name="exitCode">Launcher exit code, or null when unknown.</param>
    /// <param name="stoppedByVivian">VIvian terminated the launcher; its exit code is not engine evidence.</param>
    /// <param name="contractVerified">The completion signals were verified on the real release.</param>
    /// <param name="outputComplete">Both supervised streams drained without interruption.</param>
    public RunVerdict Evaluate(int? exitCode, bool stoppedByVivian, bool contractVerified, DateTimeOffset at,
        bool outputComplete = true)
    {
        List<ObservedSignal> signals;
        lock (_gate)
        {
            signals = [.. _signals];
            var instancesOk = request.Instances.Count == 0 ? !_instancesMismatched : _instancesMatched && !_instancesMismatched;
            var completionValid = outputComplete && !stoppedByVivian && _completions == 1 && !_completionBeforeStart && InProcessStartSeen && VersionMatched
                && AutoUpdateSkipped && !AutoUpdateAttempted && instancesOk
                && (!request.EngagementRewardOnly || _engagement.Result == OneDragonEngagementResult.ClaimConfirmed);
            if (completionValid) signals.Add(new(runId, SignalKind.CompletionMarker, "OneDragon 输出：一条龙执行成功", at));
            if (stoppedByVivian)
                signals.Add(new(runId, SignalKind.ProcessExit, "OneDragon 启动器（由 VIvian 终止，退出码不作为证据）", at, ExitCode: null));
            else if (!InProcessStartSeen)
                // The launcher may have handed the work to an elevated copy and exited: its exit code says nothing.
                signals.Add(new(runId, SignalKind.ProcessExit, "OneDragon 启动器（未在本进程内开始运行，退出码不作为证据）", at, ExitCode: null));
            else if (exitCode is { } code)
                signals.Add(new(runId, SignalKind.ProcessExit, "OneDragon 启动器", at, code));
        }
        return CompletionEvidenceEvaluator.Evaluate(runId, contractVerified, signals);
    }
}
