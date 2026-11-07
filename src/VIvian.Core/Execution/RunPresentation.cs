namespace VIvian.Core.Execution;

/// <summary>
/// User-facing wording for run state. Every simulated result is labelled as such, so a simulation can never be
/// read as a real game result. Raw reason codes are never shown.
/// </summary>
public static class RunPresentation
{
    public const string SimulationLabel = "【模拟】";

    public static string GameName(GameId game) => game switch
    {
        GameId.Genshin => "原神",
        GameId.StarRail => "崩坏：星穹铁道",
        GameId.ZenlessZoneZero => "绝区零",
        _ => "未知游戏",
    };

    public static string Phase(RunPhase phase) => phase switch
    {
        RunPhase.Pending => "排队中",
        RunPhase.Preflight => "预检中",
        RunPhase.Starting => "启动中",
        RunPhase.Running => "运行中",
        RunPhase.Stopping => "正在停止",
        RunPhase.Reconciling => "确认清理中",
        RunPhase.Finished => "已结束",
        RunPhase.Blocked => "已阻断",
        _ => "未知阶段",
    };

    public static string Outcome(RunOutcome? outcome, bool isSimulation) => (isSimulation ? SimulationLabel : "") + outcome switch
    {
        null => "尚无结果",
        RunOutcome.Success => "成功",
        RunOutcome.Failed => "失败",
        RunOutcome.Cancelled => "已取消",
        RunOutcome.Indeterminate => "结果未知",
        _ => "未知",
    };

    public static string Cleanup(CleanupState cleanup) => cleanup switch
    {
        CleanupState.NotRequired => "无需清理",
        CleanupState.Pending => "待清理",
        CleanupState.Confirmed => "已确认清理",
        CleanupState.Unconfirmed => "清理未确认",
        _ => "未知",
    };

    public static string Reason(string? reasonCode) => reasonCode switch
    {
        null => "",
        ReasonCodes.Completed => "有明确的完成证据",
        ReasonCodes.ReportedFailure => "引擎明确报告失败",
        ReasonCodes.ResolutionCircuitOpen => "游戏分辨率连续获取失败，已停止当前任务；请检查游戏窗口、显示模式和上游日志",
        ReasonCodes.ReportedCancel => "引擎报告已取消",
        ReasonCodes.NoCompletionEvidence => "没有完成证据，不判为成功",
        ReasonCodes.ConflictingEvidence => "证据相互矛盾",
        ReasonCodes.StartFailed => "启动失败",
        ReasonCodes.AdapterError => "适配器出错",
        ReasonCodes.PreflightError => "预检出错",
        ReasonCodes.TaskTimeout => "超过最长运行时间",
        ReasonCodes.Stalled => "长时间无输出，判为卡死",
        ReasonCodes.UserStop => "用户停止",
        ReasonCodes.CleanupUnconfirmed => "无法确认进程已清理",
        ReasonCodes.InterruptedByCrash => "VIvian 意外退出时中断",
        ReasonCodes.ForeignProcess => "发现不属于本次运行的同名进程",
        ReasonCodes.NotConfigured => "未配置",
        ReasonCodes.SuccessWithoutEvidence => "适配器声称成功但没有证据",
        ReasonCodes.JournalUnavailable => "无法写入运行记录",
        ReasonCodes.UnresolvedRuns => "有待确认的未清理运行",
        ReasonCodes.InternalError => "VIvian 内部错误，已保守处理",
        ReasonCodes.AdapterUnresponsive => "适配器操作未在时限内结束，可能仍在运行",
        ReasonCodes.RealAdapterUnverified => "真实适配器的进程归属尚未审核，已阻断执行",
        _ => "其他原因",
    };

    public static string QueueStatusText(QueueResult result) => result.Status switch
    {
        QueueStatus.Completed => "队列已执行完毕",
        QueueStatus.Cancelled => "队列已停止",
        QueueStatus.Blocked => "队列已阻断：" + Reason(result.BlockReason),
        _ => "队列状态未知",
    };

    /// <summary>One-line summary, e.g. "【模拟】原神 · 成功 · 已确认清理 · 有明确的完成证据".</summary>
    public static string Summary(GameId game, RunPhase phase, RunOutcome? outcome, CleanupState cleanup, string? reason, bool isSimulation)
    {
        var parts = new List<string> { (isSimulation ? SimulationLabel : "") + GameName(game) };
        // A completed task can still leave processes behind; the visible row must lead with the block.
        parts.Add(phase == RunPhase.Blocked && cleanup == CleanupState.Unconfirmed
            ? Phase(phase) : outcome is null ? Phase(phase) : Outcome(outcome, isSimulation: false));
        if (cleanup is not CleanupState.NotRequired) parts.Add(Cleanup(cleanup));
        if (Reason(reason) is { Length: > 0 } text) parts.Add(text);
        return string.Join(" · ", parts);
    }

    public static string Summary(RunUpdate u) => Summary(u.Game, u.Phase, u.Outcome, u.Cleanup, u.ReasonCode, u.IsSimulation);
    public static string Summary(RunRecord r) => Summary(r.Game, r.Phase, r.Outcome, r.Cleanup, r.ReasonCode, r.IsSimulation);
}
