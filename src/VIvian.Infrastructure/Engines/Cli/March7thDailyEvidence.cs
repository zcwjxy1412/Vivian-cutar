using System.Text.RegularExpressions;

namespace VIvian.Infrastructure.Engines.Cli;

/// <summary>Only lines captured from this run's own launcher/worker output are considered.</summary>
public static partial class March7thDailyEvidence
{
    [GeneratedRegex(@"(?:当前累计分数|当前分数)[：:]\s*(?<score>\d+)\s*/\s*500", RegexOptions.CultureInvariant)]
    private static partial Regex Score();

    public static bool ProvesComplete(string output)
    {
        if (!output.Contains("开始日常任务", StringComparison.Ordinal)) return false;
        // Quest.run emits the configurable DailyPracticeCompleted notification only after finding
        // the 500-point image and actually collecting a reward. A score alone is partial evidence.
        if (!output.Contains("每日实训已完成", StringComparison.Ordinal)) return false;
        if (output.Contains("任务无法完成:", StringComparison.Ordinal) ||
            output.Contains("每日实训未完成", StringComparison.Ordinal) ||
            output.Contains("发生错误", StringComparison.Ordinal)) return false;
        if (output.Contains("实训分数已达标", StringComparison.Ordinal)) return true;
        return Score().Matches(output).Any(m => int.TryParse(m.Groups["score"].Value, out var score) && score >= 500);
    }
}
