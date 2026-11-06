using System.Text;
using System.Text.Json;
using VIvian.Core;
using VIvian.Core.Engines;

namespace VIvian.Infrastructure.Engines;

/// <summary>
/// Settings a pinned March7thAssistant task needs in order to produce completion evidence.
/// Source: v2026.10.31 main.py sub_tasks["daily"] = Daily.run() + reward.start(); Daily.run collects the
/// 500-point reward through reward.start_specific("quest"), which returns early when reward_enable is false
/// (tasks/reward/__init__.py). Without it the run can never prove daily completion.
/// pause_after_success / exit_after_failure are deliberately absent: VIvian always passes --non-interactive
/// (utils/console.py should_skip_pause) and the pinned m7a.exe also adds it to the elevated worker.
/// </summary>
public static class March7thTaskRequirements
{
    public static IReadOnlyDictionary<string, string> For(string task) => task switch
    {
        "daily" => new Dictionary<string, string> { ["reward_enable"] = "true" },
        // routine: Daily.prepare_daily gates stamina on power_enable and daily practice on daily_enable.
        "routine" => new Dictionary<string, string> { ["reward_enable"] = "true", ["power_enable"] = "true", ["daily_enable"] = "true" },
        _ => new Dictionary<string, string>(),
    };
}

public sealed record TemporaryChange(string Path, string Original, string Temporary);

public sealed record TemporaryConfigurationRecord(int Schema, GameId Game, string InstallDirectory, string ConfigPath,
    IReadOnlyList<TemporaryChange> Changes, DateTimeOffset CreatedAt, string Task);

public enum RestoreState
{
    /// <summary>Every temporary value was written back to the user's original value and read back.</summary>
    Restored,
    /// <summary>The file already holds the original values (write never happened, or the user reverted it).</summary>
    NothingToRestore,
    /// <summary>Restoration could not be done safely now; the recovery record is kept.</summary>
    Blocked,
}

public sealed record RestoreResult(RestoreState State, string Detail);

/// <summary>
/// Field-level, recoverable temporary configuration for one real run. A recovery record is written before the
/// upstream file is touched and removed only after the original values were read back from the real file.
/// Restoring never rewrites the whole file: upstream legitimately updates timestamps and task lists during a run.
/// </summary>
public sealed class TemporaryConfigurationManager(IConfigurationControl control, string recoveryDirectory, string auditDirectory)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string RecordPath => System.IO.Path.Combine(recoveryDirectory, "temporary-config-recovery.json");

    public bool HasPending => File.Exists(RecordPath);

    public TemporaryConfigurationRecord? ReadPending()
    {
        if (!File.Exists(RecordPath)) return null;
        var record = JsonSerializer.Deserialize<TemporaryConfigurationRecord>(File.ReadAllText(RecordPath, Utf8), Json);
        if (record is null || record.Schema != 1 || record.Changes.Count == 0)
            throw new InvalidDataException("临时配置恢复记录无法识别；已保留原文件，请把诊断包发给开发者。");
        return record;
    }

    /// <summary>Changes needed so that <paramref name="snapshot"/> satisfies <paramref name="required"/>.</summary>
    public static IReadOnlyList<TemporaryChange> Plan(ConfigurationSnapshot snapshot, IReadOnlyDictionary<string, string> required)
    {
        var changes = new List<TemporaryChange>();
        foreach (var (path, value) in required)
        {
            var current = snapshot.Values.SingleOrDefault(v => v.Schema.Path == path)
                ?? throw new InvalidOperationException("任务所需参数不在固定版本登记表中：" + path);
            if (current.Value is null || current.Schema.Access != ParameterAccess.VerifiedReadWrite)
                throw new InvalidOperationException($"任务需要“{current.Schema.Title}”为 {value}，但该参数无法安全读取或写入（{current.State}）。");
            if (current.Value != value) changes.Add(new(path, current.Value, value));
        }
        return changes;
    }

    public static string Describe(IReadOnlyList<TemporaryChange> changes, ConfigurationSnapshot snapshot) =>
        string.Join("、", changes.Select(c =>
            $"“{snapshot.Values.Single(v => v.Schema.Path == c.Path).Schema.Title}” {c.Original} → {c.Temporary}"));

    /// <summary>Writes the recovery record, then applies the change with backup and read-back verification.</summary>
    public ConfigurationApplyResult Apply(ConfigurationSnapshot snapshot, IReadOnlyList<TemporaryChange> changes,
        string installDirectory, string task)
    {
        if (changes.Count == 0) throw new ArgumentException("没有需要临时修改的参数。", nameof(changes));
        if (HasPending) throw new InvalidOperationException("上一次的临时配置尚未恢复；请先处理恢复记录。");
        var record = new TemporaryConfigurationRecord(1, snapshot.Game, System.IO.Path.GetFullPath(installDirectory),
            snapshot.Path, changes, DateTimeOffset.UtcNow, task);
        Directory.CreateDirectory(recoveryDirectory);
        var temporary = RecordPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(record, Json), Utf8);
        File.Move(temporary, RecordPath, overwrite: false);
        try
        {
            var proposal = control.ProposeChanges(snapshot, changes.ToDictionary(c => c.Path, c => c.Temporary));
            return control.ApplyChanges(proposal, auditDirectory, engineRunning: false);
        }
        catch
        {
            // The write may not have happened at all; Restore decides from the real file and keeps the record if unsure.
            Restore();
            throw;
        }
    }

    /// <summary>Puts back only the fields VIvian changed, and only where they still hold VIvian's temporary value.</summary>
    public RestoreResult Restore()
    {
        TemporaryConfigurationRecord? record;
        try { record = ReadPending(); }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        { return new(RestoreState.Blocked, "恢复记录无法读取：" + ex.Message); }
        if (record is null) return new(RestoreState.NothingToRestore, "没有待恢复的临时配置。");
        ConfigurationSnapshot snapshot;
        try { snapshot = control.ReadConfiguration(record.Game, record.InstallDirectory); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        { return new(RestoreState.Blocked, "无法读取上游配置，暂不恢复：" + ex.Message); }
        if (!string.Equals(System.IO.Path.GetFullPath(snapshot.Path), System.IO.Path.GetFullPath(record.ConfigPath), StringComparison.OrdinalIgnoreCase))
            return new(RestoreState.Blocked, "配置文件位置与恢复记录不一致，暂不恢复。");
        var revert = new Dictionary<string, string>();
        foreach (var change in record.Changes)
        {
            var current = snapshot.Values.SingleOrDefault(v => v.Schema.Path == change.Path)?.Value;
            if (current is null) return new(RestoreState.Blocked, $"字段 {change.Path} 当前无法识别，暂不恢复。");
            if (current == change.Temporary) revert[change.Path] = change.Original;
            // current == Original: already back (user or upstream reverted it). Nothing else is possible for booleans.
        }
        if (revert.Count == 0)
        {
            File.Delete(RecordPath);
            return new(RestoreState.NothingToRestore, "配置已是原值，无需恢复。");
        }
        try
        {
            var proposal = control.ProposeChanges(snapshot, revert);
            var validation = control.ValidateChanges(proposal, engineRunning: false);
            if (!validation.Valid) return new(RestoreState.Blocked, "暂不恢复：" + validation.Reason);
            var applied = control.ApplyChanges(proposal, auditDirectory, engineRunning: false);
            if (revert.Any(r => applied.Snapshot.Values.Single(v => v.Schema.Path == r.Key).Value != r.Value))
                return new(RestoreState.Blocked, "恢复后读取的值与原值不符；记录已保留。");
            File.Delete(RecordPath);
            return new(RestoreState.Restored, "已恢复原配置并读回核对：" + string.Join("、", revert.Select(r => $"{r.Key}={r.Value}")));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        { return new(RestoreState.Blocked, "恢复未完成，记录已保留：" + ex.Message); }
    }
}
