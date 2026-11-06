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
/// <para>Quest.run prints DailyPracticeCompleted only after it found the in-game 500-point image AND collected a
/// reward in this run; it prints DailyPracticeNotCompleted when the image is absent. That notification is the proof.
/// "任务无法完成: X" lines are normal: upstream logs them for sub-tasks it does not implement (e.g. 累计消耗120点开拓力)
/// and keeps trying others, so they do not contradict a later verified 500 points.</para>
/// </summary>
public static partial class March7thDailyEvidence
{
    private const string Start = "开始日常任务";

    public static bool ProvesComplete(string output) => ProvesComplete(output, March7thDailyTemplates.Default);

    public static bool ProvesComplete(string output, March7thDailyTemplates templates)
    {
        var lines = Lines(output);
        var start = lines.FindIndex(l => l.Contains(Start, StringComparison.Ordinal));
        if (start < 0) return false;
        var after = lines.Skip(start + 1).ToList();
        if (after.Any(l => Contradicts(l, templates))) return false;
        return after.Any(l => l == templates.Completed || l.EndsWith(" " + templates.Completed, StringComparison.Ordinal)
            || l.EndsWith("| " + templates.Completed, StringComparison.Ordinal) || l.EndsWith("]" + templates.Completed, StringComparison.Ordinal));
    }

    /// <summary>The run explicitly reported that daily practice is not complete, or upstream reported an error.</summary>
    public static bool ProvesNotComplete(string output, March7thDailyTemplates templates)
    {
        var lines = Lines(output);
        var start = lines.FindIndex(l => l.Contains(Start, StringComparison.Ordinal));
        return start >= 0 && lines.Skip(start + 1).Any(l => l.Contains(templates.NotCompleted, StringComparison.Ordinal));
    }

    public static string Describe(string output)
    {
        var skipped = Lines(output).Count(l => l.Contains("任务无法完成:", StringComparison.Ordinal));
        return "本次运行日志出现 500 分图像核验后的领奖完成通知" +
            (skipped > 0 ? $"；其间 {skipped} 项子任务上游未能直接执行，不影响达标判定。" : "。");
    }

    private static bool Contradicts(string line, March7thDailyTemplates templates) =>
        line.Contains(templates.NotCompleted, StringComparison.Ordinal) ||
        line.Contains("发生错误", StringComparison.Ordinal) ||
        line.StartsWith("Traceback (most recent call last)", StringComparison.Ordinal);

    private static List<string> Lines(string output) =>
        output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
}
