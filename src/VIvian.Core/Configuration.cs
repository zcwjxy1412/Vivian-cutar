using VIvian.Core.Engines;

namespace VIvian.Core;

/// <summary>One task in the user's plan for a game. A task can be selected while it is not executable.</summary>
public sealed record TaskPlanEntry(string TaskId, bool Enabled);

/// <summary>The user's ordered task selection for one game. <see cref="Revision"/> detects concurrent edits.</summary>
public sealed record TaskPlan(GameId Game, IReadOnlyList<TaskPlanEntry> Entries, long Revision);

public interface ITaskPlanStore
{
    Task<IReadOnlyList<TaskPlan>> LoadTaskPlansAsync(CancellationToken ct = default);
    /// <summary>Saves one plan if its revision is still current; otherwise throws <see cref="SettingsConflictException"/>.</summary>
    Task SaveTaskPlanAsync(TaskPlan plan, CancellationToken ct = default);
}

public static class TaskPlanRules
{
    public const int MaxEntries = 32;

    /// <summary>All candidate tasks of the pinned release, none selected.</summary>
    public static TaskPlan Default(GameId game, long revision = 0) =>
        new(game, EngineCatalog.For(game).Tasks.Select(t => new TaskPlanEntry(t.Id, false)).ToList(), revision);

    /// <summary>
    /// Keeps the user's order and choices for tasks the profile still knows, drops unknown ones, appends new ones
    /// (not selected). Used when a stored plan was written for another rule version.
    /// </summary>
    public static TaskPlan Normalize(TaskPlan plan)
    {
        var known = EngineCatalog.For(plan.Game).Tasks.Select(t => t.Id).ToList();
        var kept = plan.Entries.Where(e => known.Contains(e.TaskId, StringComparer.Ordinal))
            .DistinctBy(e => e.TaskId, StringComparer.Ordinal).ToList();
        kept.AddRange(known.Where(id => kept.All(e => e.TaskId != id)).Select(id => new TaskPlanEntry(id, false)));
        return plan with { Entries = kept };
    }

    public static void Validate(TaskPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!Enum.IsDefined(plan.Game) || plan.Revision < 0) throw new ArgumentException("任务计划的游戏或版本无效。");
        if (plan.Entries.Count > MaxEntries) throw new ArgumentException("任务数量超过上限。");
        var known = EngineCatalog.For(plan.Game).Tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        if (plan.Entries.Any(e => e.TaskId is null || !known.Contains(e.TaskId)))
            throw new ArgumentException("任务计划中有该引擎版本没有的任务。");
        if (plan.Entries.Select(e => e.TaskId).Distinct(StringComparer.Ordinal).Count() != plan.Entries.Count)
            throw new ArgumentException("任务计划中有重复的任务。");
    }

    /// <summary>Why the plan cannot run now. Always non-empty in this build: no real task is executable.</summary>
    public static IReadOnlyList<string> ExecutionBlockers(TaskPlan plan)
    {
        var profile = EngineCatalog.For(plan.Game);
        var blockers = new List<string>();
        if (!plan.Entries.Any(e => e.Enabled)) blockers.Add("没有选择任何任务。");
        foreach (var entry in plan.Entries.Where(e => e.Enabled))
        {
            var task = profile.Tasks.Single(t => t.Id == entry.TaskId);
            if (task.Status != CapabilityStatus.Verified) blockers.Add($"“{task.Title}”未经 {profile.PinnedVersion} 实测验证。");
        }
        if (profile.Capability(EngineCapabilityId.TaskExecution) is { EnabledInVivian: false })
            blockers.Add($"{profile.EngineName} 的真实任务执行在此开发版中未开放。");
        return blockers;
    }
}

/// <summary>Application preferences. Everything has a safe default.</summary>
public sealed record AppPreferences(int LogRetentionDays, bool DiagnosticsIncludeRunHistory, string ReleaseArchivePath, long Revision)
{
    public const int MinRetentionDays = 3;
    public const int MaxRetentionDays = 90;
    public static AppPreferences Default { get; } = new(14, true, "", 0);

    public void Validate()
    {
        if (LogRetentionDays is < MinRetentionDays or > MaxRetentionDays)
            throw new ArgumentException($"日志保留天数必须在 {MinRetentionDays}–{MaxRetentionDays} 之间。");
        if (ReleaseArchivePath.Length > 1024 || ReleaseArchivePath.Any(char.IsControl))
            throw new ArgumentException("路径过长或包含控制字符。");
        if (Revision < 0) throw new ArgumentException("配置版本无效。");
    }
}

public interface IPreferenceStore
{
    Task<AppPreferences> LoadPreferencesAsync(CancellationToken ct = default);
    Task SavePreferencesAsync(AppPreferences preferences, CancellationToken ct = default);
}
