using System.Globalization;
using VIvian.Core.Execution;

namespace VIvian.Infrastructure.Engines.OneDragon;

internal sealed record OneDragonHandoff(int GamePid, int Instance, IReadOnlyList<string> Apps, string MaskedAccount)
{
    internal IReadOnlyDictionary<string, string> Environment(Guid runId) => new Dictionary<string, string>
    {
        ["VIVIAN_ZZZ_RUN"] = runId.ToString("D"),
        ["VIVIAN_ZZZ_GAME_PID"] = GamePid.ToString(CultureInfo.InvariantCulture),
        ["VIVIAN_ZZZ_APPS"] = string.Join(',', Apps),
        ["VIVIAN_ZZZ_MASK"] = MaskedAccount,
    };
}

internal sealed class OneDragonHandoffEvidence(Guid runId, int pid, OneDragonHandoff handoff)
{
    private readonly Dictionary<string, string> _results = new(StringComparer.Ordinal);
    private readonly string _prefix = $"VIVIAN-ZZZ|{runId:D}|";
    private string? _end;
    private bool _entered;
    private bool _invalid;
    private volatile bool _started;
    public bool Started => _started;

    internal string? Read(string line)
    {
        if (!line.StartsWith(_prefix, StringComparison.Ordinal)) return null;
        var data = line[_prefix.Length..].Split('|');
        if (_end is not null) { _invalid = true; return null; }
        switch (data)
        {
            case ["start", var process, var instance] when !Started
                && process == pid.ToString(CultureInfo.InvariantCulture)
                && instance == handoff.Instance.ToString(CultureInfo.InvariantCulture):
                _started = true;
                return "OneDragon 已接管本轮绝区零。";
            case ["entered"] when Started && !_entered:
                _entered = true;
                return "OneDragon 已完成游戏入场。";
            case ["task", var app, var result] when _entered && handoff.Apps.Contains(app, StringComparer.Ordinal)
                && result is "success" or "failed" && _results.TryAdd(app, result):
                return $"OneDragon · {app} · {(result == "success" ? "完成" : "失败")}";
            case ["detail", var message] when Started:
                return message switch
                {
                    "login_changed" => "OneDragon 再次核对登录卡片时，脱敏账号与本轮目标不同。",
                    "confirmation_missing" => "OneDragon 未识别到登录确认按钮。",
                    "entry_failed" => "OneDragon 的入场流程未完成。",
                    _ => "OneDragon 报告了入场异常。",
                };
            case ["end", var result] when Started && result is "success" or "failed" or "incomplete":
                _end = result;
                return "OneDragon 已结束本轮执行。";
            default:
                _invalid = true;
                return null;
        }
    }

    internal RunVerdict Verdict(int? exitCode, bool outputComplete, DateTimeOffset at)
    {
        if (_invalid || !Started || !outputComplete || _end is null || exitCode is null)
            return new(RunOutcome.Indeterminate, ReasonCodes.NoCompletionEvidence, []);
        if (_end == "failed" || _results.Values.Contains("failed"))
            return new(RunOutcome.Failed, ReasonCodes.ReportedFailure,
                [new(runId, EvidenceKind.FailureReported, "OneDragon 本轮任务报告", at)]);
        if (_end == "success" && _entered && exitCode == 0 && _results.Count == handoff.Apps.Count)
            return new(RunOutcome.Success, ReasonCodes.Completed,
                [new(runId, EvidenceKind.CompletionReported, "OneDragon 已报告全部任务完成", at)]);
        return new(RunOutcome.Indeterminate, ReasonCodes.NoCompletionEvidence, []);
    }
}
