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
    /// <summary>"任务被取消，退出执行", "一条龙在启动阶段被取消" or a TaskRunner interruption by cancellation.</summary>
    Cancelled,
    /// <summary>"没有配置,退出执行!" or "没有配置组任务,退出执行!" — nothing (more) was run.</summary>
    NothingDone,
    /// <summary>A known failure line, or any ERR-level record (TaskRunner logs swallowed task exceptions as ERR).</summary>
    Failure,
    /// <summary>"UnHandle Exception [FATAL]" or an FTL-level record.</summary>
    Fatal,
}

public sealed record BetterGiMarker(BetterGiMarkerKind Kind, string? Value = null, int? Done = null, int? Total = null);

/// <summary>
/// Classifies a record. Only the message line is inspected and matching is exact (or an exact prefix), so text inside
/// an exception or stack trace can never produce a completion marker. All strings are level B
/// (OneDragonFlowViewModel.cs, TaskRunner.cs, App.xaml.cs @0af68c2) and unverified on the real release.
/// </summary>
public static partial class BetterGiMarkers
{
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

    public static BetterGiMarker Classify(BetterGiLogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var message = record.Message;
        if (record.Level == "FTL" || message.StartsWith(FatalPrefix, StringComparison.Ordinal))
            return new(BetterGiMarkerKind.Fatal);
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
        if (ProgressLine().Match(message) is { Success: true } p)
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
