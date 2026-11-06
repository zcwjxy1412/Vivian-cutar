using System.Text.RegularExpressions;
using VIvian.Core.Engines;
using VIvian.Core.Execution;

namespace VIvian.Infrastructure.Engines.OneDragon;

public enum OneDragonMarkerKind
{
    None,
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

    [GeneratedRegex(@"^\[\d{2}:\d{2}:\d{2}\.\d{3}\] \[[^\]\r\n]{1,128}\] \[(DEBUG|INFO|WARNING|ERROR|CRITICAL)\]: (.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Record();

    public static OneDragonMarker Classify(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var trimmed = line.TrimEnd('\r');
        if (trimmed.StartsWith(TracebackLine, StringComparison.Ordinal)) return new(OneDragonMarkerKind.Failure);
        if (trimmed.StartsWith(ArgumentErrorPrefix, StringComparison.Ordinal)) return new(OneDragonMarkerKind.Failure);
        var match = Record().Match(trimmed);
        if (!match.Success) return new(OneDragonMarkerKind.None);
        var level = match.Groups[1].Value;
        var message = match.Groups[2].Value;
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
    private int _completions;
    private bool _completionBeforeStart;
    private bool _instancesMatched;
    private bool _instancesMismatched;

    public bool InProcessStartSeen { get; private set; }
    public bool VersionMatched { get; private set; }
    public bool AutoUpdateSkipped { get; private set; }
    public bool AutoUpdateAttempted { get; private set; }

    public OneDragonMarker Add(string line, DateTimeOffset at)
    {
        var marker = OneDragonMarkers.Classify(line);
        lock (_gate)
        {
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
    public RunVerdict Evaluate(int? exitCode, bool stoppedByVivian, bool contractVerified, DateTimeOffset at)
    {
        List<ObservedSignal> signals;
        lock (_gate)
        {
            signals = [.. _signals];
            var instancesOk = request.Instances.Count == 0 ? !_instancesMismatched : _instancesMatched && !_instancesMismatched;
            var completionValid = _completions == 1 && !_completionBeforeStart && InProcessStartSeen && VersionMatched
                && AutoUpdateSkipped && !AutoUpdateAttempted && instancesOk;
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
