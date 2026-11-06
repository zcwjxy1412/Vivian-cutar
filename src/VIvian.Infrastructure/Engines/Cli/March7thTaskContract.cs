using System.Text.Json;
using System.Text.RegularExpressions;
using VIvian.Core.Execution;

namespace VIvian.Infrastructure.Engines.Cli;

/// <summary>
/// v2026.10.31 的 m7a.exe 协议。来源：该 tag 的 utils/cli_entry.py；
/// 命令构造与结果解析本身不会启动程序，也不授权跨 UAC 进程执行。
/// </summary>
public static partial class March7thTaskContract
{
    private static readonly HashSet<string> Tasks =
        ["main", "daily", "power", "routine", "divergent", "currencywars", "fight", "universe", "screen_test"];

    [GeneratedRegex(@"^\d{8}-\d{6}-[0-9a-f]{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex RunIdPattern();

    [GeneratedRegex(@"\[m7a\].*运行编号 (?<id>\d{8}-\d{6}-[0-9a-f]{4})(?:\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex LaunchLine();

    /// <summary>只能从本次启动的标准输出中取得编号；调用方须拒绝多个不同编号。</summary>
    public static string? CaptureRunId(IEnumerable<string> launchOutput)
    {
        var ids = launchOutput.Select(line => LaunchLine().Match(line)).Where(match => match.Success)
            .Select(match => match.Groups["id"].Value).Distinct(StringComparer.Ordinal).Take(2).ToList();
        return ids.Count == 1 ? ids[0] : null;
    }

    public static IReadOnlyList<string> StartArguments(string task)
    {
        if (!Tasks.Contains(task)) throw new ArgumentException("任务不在已核对的白名单内。", nameof(task));
        // 禁止从上游配置继承关机、睡眠或脚本等完成后动作；末尾 JSON 是运行记录。
        // --non-interactive（main.py / utils/console.py should_skip_pause）：任何情况下都不等待按回车，
        // 因此 pause_after_success / exit_after_failure 不再影响本次运行，且无需改动用户配置。
        return [task, "--after-finish", "None", "--non-interactive", "--json"];
    }

    public static IReadOnlyList<string> StopArguments(string upstreamRunId)
    {
        if (!RunIdPattern().IsMatch(upstreamRunId)) throw new ArgumentException("上游运行编号格式无效。", nameof(upstreamRunId));
        // 必须指定本次上游 RunId，绝不能用 current/latest 误停别人的任务。
        return ["--control", upstreamRunId, "stop", "--json"];
    }

    public static March7thTaskResult ParseFinalRecord(string output, string task, int processExitCode, string expectedUpstreamRunId,
        March7thDailyTemplates? dailyTemplates = null)
    {
        if (!Tasks.Contains(task)) throw new ArgumentException("任务不在已核对的白名单内。", nameof(task));
        if (!RunIdPattern().IsMatch(expectedUpstreamRunId)) throw new ArgumentException("缺少本次启动时捕获的上游运行编号。", nameof(expectedUpstreamRunId));
        // m7a 会先打印日志，再在最后一行打印 JSON；不从自由文本中猜测完成状态。
        var last = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).LastOrDefault(x => x.StartsWith('{'));
        if (last is null) return Unknown("没有运行记录 JSON。");
        try
        {
            using var doc = JsonDocument.Parse(last, new JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Unknown("运行记录不是对象。");
            string? String(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
            int? Int(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var n) ? n : null;
            var id = String("run_id");
            var status = String("effective_status") ?? String("status");
            var recordedTask = String("task");
            var recordedExit = Int("exit_code");
            // The pinned launcher emits this short record when Windows refuses elevation, before
            // a Worker record exists. It proves the request was cancelled, not that cleanup is confirmed.
            if (id == expectedUpstreamRunId && status == "not_started" && recordedExit == 5
                && processExitCode == 5 && String("error")?.Contains("WinError 1223", StringComparison.Ordinal) == true)
                return new(id, RunOutcome.Indeterminate,
                    "上游报告 UAC 已取消，任务未运行；缺少 Worker 终态证据，清理仍需核对。");
            if (Int("schema") != 1 || string.IsNullOrWhiteSpace(String("ended_at")))
                return Unknown("运行记录协议或结束时间缺失。");
            if (id != expectedUpstreamRunId || recordedTask != task)
                return Unknown("运行编号或任务标识未能匹配本次请求。");
            if (String("lock") != "acquired")
                return new(id, RunOutcome.Indeterminate, "无法确认上游运行锁归属。");
            if (recordedExit is null || recordedExit != processExitCode)
                return new(id, RunOutcome.Indeterminate, "上游记录与入口退出码不一致。");
            if (!string.IsNullOrEmpty(String("error")))
                return new(id, RunOutcome.Failed, "上游运行记录包含错误。");
            return (status, recordedExit) switch
            {
                // Pinned upstream utils/runs.py derives `success` directly from exit code 0.
                // This record proves an exit, not that the requested game work was completed.
                ("success", 0) when task == "screen_test" && March7thScreenTestEvidence.ProvesComplete(output)
                    => new(id, RunOutcome.Success, "本次界面测试的启动数、逐项切换记录和完成计数一致。"),
                // Same exit-code-derived `success`, but this run's own summary proves the screens were not switched.
                ("success", 0) when task == "screen_test" && March7thScreenTestEvidence.ProvesFailure(output) is { } failure
                    => new(id, RunOutcome.Failed, failure.Describe()),
                ("success", 0) when task is "daily" or "routine" && March7thDailyEvidence.ProvesComplete(output, dailyTemplates ?? March7thDailyTemplates.Default)
                    => new(id, RunOutcome.Success, March7thDailyEvidence.Describe(output)),
                // Exit 0 but this run's own quest check found the 500-point image missing.
                ("success", 0) when task is "daily" or "routine" && March7thDailyEvidence.ProvesNotComplete(output, dailyTemplates ?? March7thDailyTemplates.Default)
                    => new(id, RunOutcome.Failed, "上游本次核对每日实训未达 500 分（每日实训未完成）。"),
                ("success", 0) when task is "daily" or "routine"
                    => new(id, RunOutcome.Indeterminate, "退出码为 0，但本次没有新的领奖完成通知：可能今日已领过、领取奖励被关闭或未进入日常任务。"),
                ("success", 0) => new(id, RunOutcome.Indeterminate, "上游 success 由退出码 0 推导；缺少独立任务完成证据。"),
                ("failed", _) => new(id, RunOutcome.Failed, "上游运行记录报告失败。"),
                ("blocked", _) => new(id, RunOutcome.Failed, "上游运行记录报告任务未执行。"),
                ("stopped", _) => new(id, RunOutcome.Cancelled, "上游运行记录报告停止。"),
                _ => new(id, RunOutcome.Indeterminate, "上游状态不是已验证的终态。"),
            };
        }
        catch (JsonException) { return Unknown("末尾运行记录 JSON 无法解析。"); }

        static March7thTaskResult Unknown(string reason) => new(null, RunOutcome.Indeterminate, reason);
    }
}

public sealed record March7thTaskResult(string? UpstreamRunId, RunOutcome Outcome, string Detail);
