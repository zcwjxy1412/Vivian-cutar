using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VIvian.Infrastructure.Engines.Cli;

/// <summary>Notification texts the pinned upstream prints for daily practice (config.yaml notify_template).</summary>
public sealed partial record March7thDailyTemplates(string Completed, string NotCompleted)
{
    public static March7thDailyTemplates Default { get; } = new("每日实训已完成", "每日实训未完成");

    /// <summary>
    /// Reads the user's single-line templates from config.yaml. Anything unusual (multi-line, placeholders,
    /// missing) falls back to the defaults, which can only make the verdict more conservative.
    /// </summary>
    public static March7thDailyTemplates Read(string configPath)
    {
        try
        {
            if (!File.Exists(configPath) || new FileInfo(configPath).Length > 4_000_000) return Default;
            var lines = File.ReadAllLines(configPath, new UTF8Encoding(false, true));
            string? Value(string key)
            {
                var inTemplate = false;
                foreach (var line in lines)
                {
                    if (line.Length > 0 && !char.IsWhiteSpace(line[0])) inTemplate = line.TrimEnd() == "notify_template:";
                    else if (inTemplate && TemplateLine().Match(line) is { Success: true } m && m.Groups["key"].Value == key)
                    {
                        var raw = m.Groups["value"].Value.Trim();
                        if (raw.Length >= 2 && (raw[0] == '"' || raw[0] == '\'') && raw[^1] == raw[0]) raw = raw[1..^1];
                        return raw.Length is > 0 and <= 200 && !raw.Contains('{') && !raw.Contains("\\n") ? raw.Trim() : null;
                    }
                }
                return null;
            }
            return new(Value("DailyPracticeCompleted") ?? Default.Completed, Value("DailyPracticeNotCompleted") ?? Default.NotCompleted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException) { return Default; }
    }

    [GeneratedRegex(@"^[ \t]+(?<key>[A-Za-z]+):[ \t]*(?<value>.*?)[ \t]*$", RegexOptions.CultureInvariant)]
    private static partial Regex TemplateLine();
}

/// <summary>
/// Daily completion rules for v2026.10.31 (tasks/daily/daily.py, tasks/reward/quest.py). Only lines captured from
/// this run's own launcher/worker output are considered.
/// <para>Quest.run prints DailyPracticeCompleted after finding the 500-point image and collecting a reward.
/// If there is nothing left to collect, confirm the image through the same worker's committed diagnostic trace,
/// or accept the game's full-activity text followed by the upstream check for unclaimed rewards.
/// "任务无法完成: X" lines are normal: upstream logs them for sub-tasks it does not implement (e.g. 累计消耗120点开拓力)
/// and keeps trying others, so they do not contradict a later verified 500 points.</para>
/// </summary>
public static partial class March7thDailyEvidence
{
    private const string Start = "开始日常任务";
    private const string FullActivity = "检测到本日活跃度已满提示，跳过后续任务检测";
    private const string NoUnclaimedReward = "未检测到每日实训奖励";

    public static bool ProvesComplete(string output) => ProvesComplete(output, March7thDailyTemplates.Default);

    public static bool ProvesComplete(string output, March7thDailyTemplates templates) => DescribeProof(output, templates) is not null;

    internal static string? DescribeProof(string output, March7thDailyTemplates templates,
        string? installDirectory = null, int? pid = null, string? startedAt = null, string? endedAt = null)
    {
        var lines = Lines(output);
        var start = lines.FindLastIndex(l => l.Trim('|', ' ') == Start);
        if (start < 0) return null;
        var after = lines.GetRange(start + 1, lines.Count - start - 1);
        if (after.Any(l => l.Contains("发生错误", StringComparison.Ordinal) ||
                           l.StartsWith("Traceback (most recent call last)", StringComparison.Ordinal))) return null;
        var notComplete = after.FindLastIndex(l => MessageIs(l, templates.NotCompleted));
        if (after.FindLastIndex(l => MessageIs(l, templates.Completed)) > notComplete) return Describe(output);

        var fullActivity = after.FindLastIndex(l => MessageIs(l, FullActivity));
        if (fullActivity > notComplete && after.FindLastIndex(l => MessageIs(l, NoUnclaimedReward)) > fullActivity)
            return "已从每日实训界面核对本日活跃度已满，随后助手检查无待领取实训奖励。";

        return installDirectory is not null && pid is > 0 &&
               DateTimeOffset.TryParse(startedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var started) &&
               DateTimeOffset.TryParse(endedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var ended) &&
               HasRewardImageTrace(after, notComplete, installDirectory, pid.Value, started.DateTime, ended.DateTime)
            ? "本次助手的末次实训奖励核对匹配到 500 分图像，已按同一进程的写入记录确认。"
            : null;
    }

    /// <summary>The run explicitly reported that daily practice is not complete, or upstream reported an error.</summary>
    public static bool ProvesNotComplete(string output, March7thDailyTemplates templates)
    {
        var lines = Lines(output);
        var start = lines.FindLastIndex(l => l.Trim('|', ' ') == Start);
        return start >= 0 && lines.Skip(start + 1).Any(l => MessageIs(l, templates.NotCompleted));
    }

    private static string Describe(string output)
    {
        var skipped = Lines(output).Count(l => l.Contains("任务无法完成:", StringComparison.Ordinal));
        return "本次运行日志出现 500 分图像核验后的领奖完成通知" +
            (skipped > 0 ? $"；其间 {skipped} 项子任务上游未能直接执行，不影响达标判定。" : "。");
    }

    private static bool HasRewardImageTrace(List<string> lines, int lastNegative, string directory, int pid,
        DateTime started, DateTime ended)
    {
        // Daily.lookup also writes an empty task map. Use only a write inside Quest's final reward check.
        var open = lines.FindLastIndex(l => l.Trim('=', ' ') == "检测到每日实训奖励");
        if (open <= lastNegative) return false;
        var close = lines.FindIndex(open + 1, l => l.Trim('-', ' ') == "每日实训奖励完成");
        if (close < 0) return false;
        var guide = lines.FindLastIndex(close - 1, close - open - 1, l =>
            MessageIs(l, "切换到：星际和平指南-每日实训") || MessageIs(l, "当前界面：星际和平指南-每日实训"));
        if (guide < 0 || LogTime(lines[guide], ',') is not { } from || from < started) return false;
        var until = lines.Skip(close + 1).Select(l => LogTime(l, ',')).FirstOrDefault(t => t is not null) ?? ended.AddSeconds(1);
        if (until <= from || until > ended.AddSeconds(1)) return false;

        var worker = $" [DEBUG] [pid={pid.ToString(CultureInfo.InvariantCulture)}] [thread=MainThread] ";
        DateTime? imageMatched = null;
        foreach (var file in new[] { "config_diag.1.log", "config_diag.log" })
        {
            var path = Path.Combine(directory, "logs", file);
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > 6 * 1024 * 1024) continue;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                while (reader.ReadLine() is { } line)
                {
                    if (line.Length <= 23 || !line.AsSpan(23).StartsWith(worker, StringComparison.Ordinal) ||
                        LogTime(line, '.') is not { } at || at < from || at > until) continue;
                    var message = line.AsSpan(23 + worker.Length);
                    if (message.SequenceEqual("set_value: daily_tasks={}")) imageMatched = at;
                    else if (message.StartsWith("set_value:", StringComparison.Ordinal)) imageMatched = null;
                    else if (imageMatched is { } matched && at >= matched && at - matched <= TimeSpan.FromSeconds(3) &&
                        message.StartsWith("save_config 原子替换成功:", StringComparison.Ordinal)) return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
        return false;
    }

    private static DateTime? LogTime(string line, char separator) => line.Length > 23 && line[19] == separator &&
        DateTime.TryParseExact(line.AsSpan(0, 23), separator == '.' ? "yyyy-MM-dd HH:mm:ss.fff" : "yyyy-MM-dd HH:mm:ss,fff",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;

    private static bool MessageIs(string line, string message) => line == message ||
        line.EndsWith(" | " + message, StringComparison.Ordinal) || line.EndsWith("]" + message, StringComparison.Ordinal);

    [GeneratedRegex(@"\x1B\[[0-9;]*m", RegexOptions.CultureInvariant)]
    private static partial Regex ConsoleColors();

    private static List<string> Lines(string output) =>
        ConsoleColors().Replace(output, "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
}
