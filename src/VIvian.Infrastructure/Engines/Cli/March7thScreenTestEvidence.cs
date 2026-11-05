using System.Text.RegularExpressions;

namespace VIvian.Infrastructure.Engines.Cli;

/// <summary>v2026.10.31 tasks/base/screen_test.py: every switch and final counts must be present in this run's output.</summary>
public static partial class March7thScreenTestEvidence
{
    [GeneratedRegex(@"共 (?<count>\d+) 个待测界面", RegexOptions.CultureInvariant)]
    private static partial Regex Start();
    [GeneratedRegex(@"\[(?<index>\d+)/(?<total>\d+)\] 切换到 .+ 成功！", RegexOptions.CultureInvariant)]
    private static partial Regex Step();
    [GeneratedRegex(@"测试完成！成功 (?<success>\d+) 个，失败 (?<failed>\d+) 个，共 (?<total>\d+) 个界面", RegexOptions.CultureInvariant)]
    private static partial Regex End();

    public static bool ProvesComplete(string output)
    {
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var begins = lines.Select(line => Start().Match(line)).Where(m => m.Success).ToArray();
        var ends = lines.Select(line => End().Match(line)).Where(m => m.Success).ToArray();
        if (begins.Length != 1 || ends.Length != 1) return false;
        if (!int.TryParse(begins[0].Groups["count"].Value, out var begin)
            || !int.TryParse(ends[0].Groups["total"].Value, out var total)
            || !int.TryParse(ends[0].Groups["success"].Value, out var success)
            || !int.TryParse(ends[0].Groups["failed"].Value, out var failed)) return false;
        if (begin <= 0 || begin > 500 || total != begin || success != total || failed != 0) return false;
        var beginAt = Array.FindIndex(lines, line => Start().IsMatch(line));
        var endAt = Array.FindIndex(lines, line => End().IsMatch(line));
        var steps = lines.Select((line, at) => (Match: Step().Match(line), At: at))
            .Where(pair => pair.Match.Success).ToArray();
        if (steps.Length != total) return false;
        for (var position = 0; position < steps.Length; position++)
        {
            var step = steps[position].Match;
            if (!int.TryParse(step.Groups["index"].Value, out var index)
                || !int.TryParse(step.Groups["total"].Value, out var count)) return false;
            if (count != total || index != position + 1 || steps[position].At <= beginAt
                || steps[position].At >= endAt) return false;
        }
        return true;
    }
}
