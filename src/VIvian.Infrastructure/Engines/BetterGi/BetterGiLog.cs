using System.Globalization;
using System.Text.RegularExpressions;

namespace VIvian.Infrastructure.Engines.BetterGi;

/// <summary>
/// One BetterGI file-log record. The pinned Serilog template is
/// <c>[{Timestamp:HH:mm:ss.fff}] [{Level:u3}] [{BgiInstance}] {SourceContext}{NewLine}{Message}{NewLine}{Exception}{NewLine}</c>
/// with <c>BgiInstance = "{InstanceType}:S{SessionId}:P{ProcessId}:T{StartedAtUnixMs}"</c> (level B, App.xaml.cs and
/// Service/Instance/InstanceContext.cs @0af68c2). The instance field names the writing process, which is what makes a
/// record attributable to the process VIvian started.
/// </summary>
public sealed record BetterGiLogRecord(
    string Time, string Level, string InstanceType, int SessionId, int ProcessId, long InstanceStartedAtUnixMs,
    string SourceContext, string Message, string Body);

/// <summary>
/// Turns log lines into records. A record starts at a header line; every following line up to the next header is its
/// body. Lines before the first header (for example the tail of a record written before VIvian started reading) are
/// discarded, never guessed at.
/// </summary>
public sealed partial class BetterGiLogRecordParser
{
    public const int MaxBodyLines = 400;

    private string? _header;
    private Match? _headerMatch;
    private readonly List<string> _body = [];

    [GeneratedRegex(@"^\[(\d{2}:\d{2}:\d{2}\.\d{3})\] \[([A-Z]{3})\] \[(Primary|ChildSession|WebView):S(\d{1,6}):P(\d{1,10}):T(\d{1,16})\] ?(.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Header();

    public static bool IsHeader(string line) => Header().IsMatch(line);

    /// <summary>Feeds one line. Returns the previous record when this line starts a new one.</summary>
    public BetterGiLogRecord? Feed(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var match = Header().Match(line);
        if (match.Success)
        {
            var completed = Complete();
            _header = line;
            _headerMatch = match;
            _body.Clear();
            return completed;
        }
        if (_header is not null && _body.Count < MaxBodyLines) _body.Add(line);
        return null;
    }

    /// <summary>Returns the record still being collected, if any (call once the source has ended).</summary>
    public BetterGiLogRecord? Flush()
    {
        var completed = Complete();
        _header = null;
        _headerMatch = null;
        _body.Clear();
        return completed;
    }

    private BetterGiLogRecord? Complete()
    {
        if (_headerMatch is not { } m) return null;
        if (!int.TryParse(m.Groups[4].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var session)
            || !int.TryParse(m.Groups[5].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            || !long.TryParse(m.Groups[6].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var started))
            return null;
        // Message = first body line; the exception text and the template's trailing blank lines follow it.
        var message = _body.Count > 0 ? _body[0].TrimEnd() : "";
        return new BetterGiLogRecord(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, session, pid, started,
            m.Groups[7].Value.Trim(), message, string.Join('\n', _body));
    }
}

public enum BetterGiMarkerKind
{
    None,
    /// <summary>"参数指定的一条龙配置：{name}" — BetterGI read the configuration name from the command line.</summary>
    ArgumentConfig,
    /// <summary>"未找到，请检查。" — the named configuration does not exist; BetterGI then runs the selected one.</summary>
    ConfigNotFound,
    /// <summary>"启用一条龙配置：{name}" — the one-dragon run started with this configuration.</summary>
    Activated,
    /// <summary>"一条龙任务执行: X/Y" or "配置组任务执行: X/Y".</summary>
    Progress,
    /// <summary>"一条龙和配置组任务结束" — the only completion line of the one-dragon flow.</summary>
    Completed,
    DailyRewardVerified,
    DailyRewardUnverified,
    CraftStarted,
    CraftAttempt,
    CraftSkipped,
    CraftEnded,
    /// <summary>"任务被取消，退出执行", "一条龙在启动阶段被取消" or a TaskRunner interruption by cancellation.</summary>
    Cancelled,
    /// <summary>"没有配置,退出执行!" or "没有配置组任务,退出执行!" — nothing (more) was run.</summary>
    NothingDone,
    /// <summary>A known failure line, or any ERR-level record (TaskRunner logs swallowed task exceptions as ERR).</summary>
    Failure,
    /// <summary>"UnHandle Exception [FATAL]" or an FTL-level record.</summary>
    Fatal,
    DomainStarted,
    DomainTarget,
    DomainRewardAttempt,
    DomainResinProgress,
    DomainRewardVerified,
    DomainRewardUnverified,
    DomainEnded,
}

public sealed record BetterGiMarker(BetterGiMarkerKind Kind, string? Value = null, int? Done = null, int? Total = null);

/// <summary>
/// Classifies a record. Only the message line is inspected and matching is exact (or an exact prefix), so text inside
/// an exception or stack trace can never produce a completion marker. All strings are level B
/// (OneDragonFlowViewModel.cs, TaskRunner.cs, App.xaml.cs @0af68c2) and unverified on the real release.
/// </summary>
public static partial class BetterGiMarkers
{
    public const string DailyRewardTask = "领取每日奖励";
    internal const string CraftResinTask = "合成树脂";
    internal const string DomainTask = "自动秘境";
    internal const string FlowSource = "BetterGenshinImpact.ViewModel.Pages.OneDragonFlowViewModel";
    internal const string BuiltInProgressPrefix = "一条龙任务执行: ";
    private const string TaskControlSource = "BetterGenshinImpact.GameTask.Common.TaskControl";
    private const string DomainTargetPrefix = "自动秘境：传送到秘境\"";

    public const string CompletedLine = "一条龙和配置组任务结束";
    public const string ActivatedPrefix = "启用一条龙配置：";
    public const string ArgumentConfigPrefix = "参数指定的一条龙配置：";
    public const string ConfigNotFoundLine = "未找到，请检查。";
    public const string CancelledLine = "任务被取消，退出执行";
    public const string CancelledAtStartLine = "一条龙在启动阶段被取消";
    public const string NoConfigLine = "没有配置,退出执行!";
    public const string NoGroupTaskLine = "没有配置组任务,退出执行!";
    public const string GroupTaskFailedLine = "执行配置组任务时失败";
    public const string TaskStartFailedPrefix = "任务启动失败";
    public const string FatalPrefix = "UnHandle Exception [FATAL]";
    public const string UnhandledLine = "UnHandle Exception";
    public const string InterruptedPrefix = "任务中断:";

    [GeneratedRegex(@"^(?:一条龙任务执行|配置组任务执行): (\d{1,4})/(\d{1,4})$", RegexOptions.CultureInvariant)]
    private static partial Regex ProgressLine();

    [GeneratedRegex("""^自动秘境："(浓缩树脂|原粹树脂40|原粹树脂20|原粹树脂|须臾树脂|脆弱树脂)" 刷取 ([1-9][0-9]{0,9})/([1-9][0-9]{0,9})$""",
        RegexOptions.CultureInvariant)]
    private static partial Regex DomainResinProgressLine();

    [GeneratedRegex("""^自动秘境：本轮奖励识别结果 "\S.* x[1-9][0-9]*"$""", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex DomainRewardResultLine();

    public static BetterGiMarker Classify(BetterGiLogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var message = record.Message;
        if (record.Level == "FTL" || message.StartsWith(FatalPrefix, StringComparison.Ordinal))
            return new(BetterGiMarkerKind.Fatal);
        if (record.SourceContext == TaskControlSource)
        {
            // CheckRewardsTask and AutoDomainTask use TaskControl.Logger. The file template uses {Message},
            // so Serilog puts quotes around structured string values.
            if (record.Level == "INF")
            {
                if (message == "检查每日奖励结果：\"今日奖励已领取\"") return new(BetterGiMarkerKind.DailyRewardVerified);
                if (message == "→ \"前往合成台\" 开始") return new(BetterGiMarkerKind.CraftStarted);
                if (message == "合成\"浓缩树脂\"") return new(BetterGiMarkerKind.CraftAttempt);
                if (message == "无需合成浓缩树脂") return new(BetterGiMarkerKind.CraftSkipped);
                if (message == "→ \"前往合成台\" 结束") return new(BetterGiMarkerKind.CraftEnded);
                if (message == "自动秘境任务：执行") return new(BetterGiMarkerKind.DomainStarted);
                if (message.StartsWith(DomainTargetPrefix, StringComparison.Ordinal) && message.Length > DomainTargetPrefix.Length
                    && message.EndsWith('"'))
                    return new(BetterGiMarkerKind.DomainTarget, message[DomainTargetPrefix.Length..^1]);
                if (message == "自动秘境：\"5. 领取奖励\"") return new(BetterGiMarkerKind.DomainRewardAttempt);
                if (DomainResinProgressLine().Match(message) is { Success: true } resin
                    && int.TryParse(resin.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var done)
                    && int.TryParse(resin.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var total))
                    return new(BetterGiMarkerKind.DomainResinProgress, resin.Groups[1].Value, done, total);
                if (DomainRewardResultLine().IsMatch(message)) return new(BetterGiMarkerKind.DomainRewardVerified);
                if (message == "体力耗尽或者设置轮次已达标，结束自动秘境") return new(BetterGiMarkerKind.DomainEnded);
                if (message == "自动秘境：原粹树脂已用尽，退出秘境") return new(BetterGiMarkerKind.DomainRewardUnverified);
            }
            if (record.Level == "WRN" && message == "检查每日奖励结果：\"未领取\"，请手动检查！")
                return new(BetterGiMarkerKind.DailyRewardUnverified);
            if (record.Level == "WRN" && (message is "树脂不足" or "自动秘境：本轮奖励识别结果为空"
                or "自动秘境：奖励结果页未检测到退出按钮，已跳过本轮奖励识别"
                or "自动秘境：奖励识别失败，已跳过本轮奖励汇总"
                or "自动秘境：指定树脂领取次数时，当前可用树脂选项无法满足配置。你可能设置的刷取次数过多！退出秘境。"
                || message.StartsWith("自动秘境：检测到树脂不足提示：", StringComparison.Ordinal)
                || message.StartsWith("自动秘境：未找到可用的树脂，", StringComparison.Ordinal)))
                return new(BetterGiMarkerKind.DomainRewardUnverified);
        }
        if (message == CompletedLine) return record.Level == "INF" ? new(BetterGiMarkerKind.Completed) : new(BetterGiMarkerKind.Failure);
        if (message.StartsWith(ActivatedPrefix, StringComparison.Ordinal))
            return new(BetterGiMarkerKind.Activated, message[ActivatedPrefix.Length..]);
        if (message.StartsWith(ArgumentConfigPrefix, StringComparison.Ordinal))
            return new(BetterGiMarkerKind.ArgumentConfig, message[ArgumentConfigPrefix.Length..]);
        if (message == ConfigNotFoundLine) return new(BetterGiMarkerKind.ConfigNotFound);
        if (message is CancelledLine or CancelledAtStartLine) return new(BetterGiMarkerKind.Cancelled);
        if (message.StartsWith(InterruptedPrefix, StringComparison.Ordinal) && message.Contains("任务被取消", StringComparison.Ordinal))
            return new(BetterGiMarkerKind.Cancelled);
        if (message is NoConfigLine or NoGroupTaskLine) return new(BetterGiMarkerKind.NothingDone);
        if (message == GroupTaskFailedLine || message.StartsWith(TaskStartFailedPrefix, StringComparison.Ordinal)
            || message == UnhandledLine)
            return new(BetterGiMarkerKind.Failure);
        if (record.Level == "INF" && ProgressLine().Match(message) is { Success: true } p)
            return new(BetterGiMarkerKind.Progress, null,
                int.Parse(p.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(p.Groups[2].Value, CultureInfo.InvariantCulture));
        // TaskRunner logs exceptions swallowed inside a sub-task at ERR and the flow then continues to its completion
        // line with exit code 0. Any error record therefore blocks success.
        if (record.Level == "ERR") return new(BetterGiMarkerKind.Failure);
        return new(BetterGiMarkerKind.None);
    }
}

/// <summary>
/// Decides whether a record was written by the process this run started. Required: the primary instance type, the
/// owned root PID, and an instance start time no earlier than the owned root's creation (minus a small clock
/// tolerance). Old records of a process that had the same PID, records of another BetterGI copy, and records of an
/// elevated self-restart (a new PID outside the job) are all rejected.
/// </summary>
public sealed record BetterGiAttribution(int RootProcessId, DateTimeOffset RootStartTime, TimeSpan Tolerance)
{
    public bool Owns(BetterGiLogRecord record) =>
        record.InstanceType == "Primary"
        && record.ProcessId == RootProcessId
        && record.InstanceStartedAtUnixMs >= (RootStartTime - Tolerance).ToUnixTimeMilliseconds();
}
