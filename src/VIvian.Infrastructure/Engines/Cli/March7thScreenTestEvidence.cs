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

    [GeneratedRegex(@"\[(?<index>\d+)/(?<total>\d+)\] 切换到 .+ 失败：", RegexOptions.CultureInvariant)]
    private static partial Regex FailedStep();
    // tasks/base/screen_test.py: emitted when the game cannot return to the main screen; the loop then stops.
    private const string Terminated = "无法回到主界面，测试终止";
    // tasks/game/__init__.py raises PermissionError("账号登录过期"); the launcher logs it as an error.
    private const string LoginExpired = "账号登录过期";

    /// <summary>
    /// True only when this run's output contains exactly one start line and exactly one final summary for the same
    /// number of screens, and the summary itself reports at least one failure (or the explicit termination line
    /// appears between them). Anything missing, duplicated or contradictory returns null: the caller keeps
    /// Indeterminate rather than guessing.
    /// </summary>
    public static ScreenTestFailure? ProvesFailure(string output)
    {
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var begins = lines.Select((line, at) => (Match: Start().Match(line), At: at)).Where(p => p.Match.Success).ToArray();
        var ends = lines.Select((line, at) => (Match: End().Match(line), At: at)).Where(p => p.Match.Success).ToArray();
        if (begins.Length != 1 || ends.Length != 1 || ends[0].At <= begins[0].At) return null;
        if (!int.TryParse(begins[0].Match.Groups["count"].Value, out var begin)
            || !int.TryParse(ends[0].Match.Groups["total"].Value, out var total)
            || !int.TryParse(ends[0].Match.Groups["success"].Value, out var success)
            || !int.TryParse(ends[0].Match.Groups["failed"].Value, out var failed)) return null;
        if (begin <= 0 || begin > 500 || total != begin || success < 0 || failed < 0 || success + failed > total) return null;
        var between = lines[(begins[0].At + 1)..ends[0].At];
        // Counted successes and failures must agree with the per-step lines; otherwise the record is contradictory.
        if (between.Count(line => Step().IsMatch(line)) != success
            || between.Count(line => FailedStep().IsMatch(line)) != failed) return null;
        var terminated = between.Any(line => line.Contains(Terminated, StringComparison.Ordinal));
        if (failed == 0 && !terminated) return null;
        if (failed == 0 && success == total) return null; // complete success cannot also be a termination
        var loginExpired = lines.Any(line => line.Contains(LoginExpired, StringComparison.Ordinal));
        return new(success, failed, total, terminated, loginExpired);
    }

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

public sealed record ScreenTestFailure(int Succeeded, int Failed, int Total, bool Terminated, bool LoginExpired)
{
    public string Describe() =>
        $"界面测试失败：成功 {Succeeded} 个，失败 {Failed} 个，共 {Total} 个界面"
        + (Terminated ? "；上游报告“无法回到主界面，测试终止”" : "")
        + (LoginExpired ? "；上游报告“账号登录过期”，请先手动登录游戏账号" : "") + "。";
}
