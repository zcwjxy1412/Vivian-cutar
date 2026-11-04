using System.Text.Json;
using Microsoft.Data.Sqlite;
using VIvian.Core;
using VIvian.Core.Engines;

namespace VIvian.Infrastructure;

public sealed partial class LocalDatabase : ITaskPlanStore, IPreferenceStore
{
    private sealed record StoredEntry(string Id, bool Enabled);

    /// <summary>
    /// Plans for all three games. A missing or unreadable plan falls back to the default (nothing selected), so a
    /// damaged row can never select tasks the user did not choose.
    /// </summary>
    public async Task<IReadOnlyList<TaskPlan>> LoadTaskPlansAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT game,plan_json,revision FROM task_plans;";
        var stored = new Dictionary<GameId, TaskPlan>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var game = (GameId)r.GetInt32(0);
                var revision = r.GetInt64(2);
                try
                {
                    var entries = JsonSerializer.Deserialize<List<StoredEntry>>(r.GetString(1)) ?? [];
                    stored[game] = TaskPlanRules.Normalize(new(game, entries.Select(e => new TaskPlanEntry(e.Id, e.Enabled)).ToList(), revision));
                }
                catch (JsonException)
                {
                    stored[game] = TaskPlanRules.Default(game, revision);
                }
            }
        }
        return Enum.GetValues<GameId>().Select(g => stored.TryGetValue(g, out var plan) ? plan : TaskPlanRules.Default(g)).ToList();
    }

    public async Task SaveTaskPlanAsync(TaskPlan plan, CancellationToken ct = default)
    {
        TaskPlanRules.Validate(plan);
        var json = JsonSerializer.Serialize(plan.Entries.Select(e => new StoredEntry(e.TaskId, e.Enabled)).ToList());
        await using var c = await OpenAsync(ct);
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("$game", (int)plan.Game);
        cmd.Parameters.AddWithValue("$json", json);
        cmd.Parameters.AddWithValue("$rule", EngineCatalog.For(plan.Game).RuleVersion);
        cmd.Parameters.AddWithValue("$revision", plan.Revision);
        // Revision 0 means "never saved": only an insert may succeed. Otherwise the stored revision must match.
        cmd.CommandText = plan.Revision == 0
            ? "INSERT OR IGNORE INTO task_plans(game,plan_json,rule_version,revision) VALUES($game,$json,$rule,1);"
            : "UPDATE task_plans SET plan_json=$json,rule_version=$rule,revision=revision+1 WHERE game=$game AND revision=$revision;";
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new SettingsConflictException();
        tx.Commit();
    }

    public async Task<AppPreferences> LoadPreferencesAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT log_retention_days,include_run_history,release_archive_path,revision FROM app_preferences WHERE id=1;";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return AppPreferences.Default;
        var result = new AppPreferences(r.GetInt32(0), r.GetInt32(1) == 1, r.GetString(2), r.GetInt64(3));
        try { result.Validate(); return result; }
        catch (ArgumentException) { return AppPreferences.Default with { Revision = result.Revision }; }
    }

    public async Task SavePreferencesAsync(AppPreferences preferences, CancellationToken ct = default)
    {
        preferences.Validate();
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE app_preferences SET log_retention_days=$days,include_run_history=$history,
                release_archive_path=$archive,revision=revision+1
            WHERE id=1 AND revision=$revision;
            """;
        cmd.Parameters.AddWithValue("$days", preferences.LogRetentionDays);
        cmd.Parameters.AddWithValue("$history", preferences.DiagnosticsIncludeRunHistory ? 1 : 0);
        cmd.Parameters.AddWithValue("$archive", preferences.ReleaseArchivePath);
        cmd.Parameters.AddWithValue("$revision", preferences.Revision);
        try
        {
            if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new SettingsConflictException();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // constraint: never store an invalid value
        {
            throw new ArgumentException("配置值不符合约束。", ex);
        }
    }
}
