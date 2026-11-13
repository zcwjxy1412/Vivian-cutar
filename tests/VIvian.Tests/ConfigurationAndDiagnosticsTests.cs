using System.IO.Compression;
using Microsoft.Data.Sqlite;
using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Core.Execution;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Diagnostics;
using VIvian.Infrastructure.Engines;
using Xunit;

namespace VIvian.Tests;

public sealed class ConfigurationStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "vivian-cfg-" + Guid.NewGuid().ToString("N"))).FullName;
    private string DbPath => Path.Combine(_dir, "vivian.db");
    public void Dispose() { SqliteConnection.ClearAllPools(); TestDirectories.DeleteWithRetry(_dir); }

    private async Task<LocalDatabase> CreateAsync()
    {
        var db = new LocalDatabase(DbPath);
        await db.InitializeAsync();
        return db;
    }

    [Fact]
    public async Task DefaultsAreSafeNothingSelectedAndExecutionBlocked()
    {
        var db = await CreateAsync();
        var plans = await db.LoadTaskPlansAsync();
        Assert.Equal(3, plans.Count);
        Assert.All(plans, p => Assert.All(p.Entries, e => Assert.False(e.Enabled)));
        Assert.Equal(EngineCatalog.March7th.Tasks.Select(t => t.Id), plans.Single(p => p.Game == GameId.StarRail).Entries.Select(e => e.TaskId));
        var prefs = await db.LoadPreferencesAsync();
        Assert.Equal(AppPreferences.Default with { Revision = 0 }, prefs);
        Assert.All(plans, p => Assert.NotEmpty(TaskPlanRules.ExecutionBlockers(p)));
    }

    [Fact]
    public async Task TaskPlanRoundTripsKeepsOrderAndDetectsConflicts()
    {
        var db = await CreateAsync();
        var plan = (await db.LoadTaskPlansAsync()).Single(p => p.Game == GameId.StarRail);
        var reordered = plan with { Entries = [new("power", true), new("daily", true), .. plan.Entries.Where(e => e.TaskId is not ("power" or "daily"))] };
        await db.SaveTaskPlanAsync(reordered);
        var loaded = (await db.LoadTaskPlansAsync()).Single(p => p.Game == GameId.StarRail);
        Assert.Equal(1, loaded.Revision);
        Assert.Equal(reordered.Entries, loaded.Entries);
        // Still blocked: selected tasks are candidates only.
        Assert.Contains(TaskPlanRules.ExecutionBlockers(loaded), b => b.Contains("未开放", StringComparison.Ordinal));

        await Assert.ThrowsAsync<SettingsConflictException>(() => db.SaveTaskPlanAsync(reordered)); // stale revision 0
        await db.SaveTaskPlanAsync(loaded with { Entries = [.. loaded.Entries.Select(e => e with { Enabled = false })] });
        await Assert.ThrowsAsync<SettingsConflictException>(() => db.SaveTaskPlanAsync(loaded));
    }

    [Fact]
    public async Task InvalidPlansAreRejectedBeforeSaving()
    {
        var db = await CreateAsync();
        var plan = TaskPlanRules.Default(GameId.Genshin);
        await Assert.ThrowsAsync<ArgumentException>(() => db.SaveTaskPlanAsync(plan with { Entries = [new("daily", true)] })); // M7 task on BetterGI
        await Assert.ThrowsAsync<ArgumentException>(() => db.SaveTaskPlanAsync(plan with { Entries = [plan.Entries[0], plan.Entries[0]] }));
        Assert.All((await db.LoadTaskPlansAsync()).Single(p => p.Game == GameId.Genshin).Entries, e => Assert.False(e.Enabled));
    }

    [Fact]
    public async Task DamagedOrOutdatedStoredPlanFallsBackSafely()
    {
        var db = await CreateAsync();
        await using (var c = new SqliteConnection("Pooling=False;Data Source=" + DbPath))
        {
            await c.OpenAsync();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO task_plans VALUES(1,'[{"Id":"retired-task","Enabled":true},{"Id":"daily","Enabled":true}]','old',4);
                INSERT INTO task_plans VALUES(2,'not json','old',2);
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        var plans = await db.LoadTaskPlansAsync();
        var m7 = plans.Single(p => p.Game == GameId.StarRail);
        Assert.Equal("daily", m7.Entries[0].TaskId);
        Assert.True(m7.Entries[0].Enabled);
        Assert.DoesNotContain(m7.Entries, e => e.TaskId == "retired-task");
        Assert.Equal(EngineCatalog.March7th.Tasks.Count, m7.Entries.Count);
        var od = plans.Single(p => p.Game == GameId.ZenlessZoneZero);
        Assert.Equal(2, od.Revision);
        Assert.All(od.Entries, e => Assert.False(e.Enabled));
    }

    [Fact]
    public async Task PreferencesValidateAndDetectConflicts()
    {
        var db = await CreateAsync();
        var prefs = await db.LoadPreferencesAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => db.SavePreferencesAsync(prefs with { LogRetentionDays = 1 }));
        await Assert.ThrowsAsync<ArgumentException>(() => db.SavePreferencesAsync(prefs with { ReleaseArchivePath = "a\u0000b" }));
        await db.SavePreferencesAsync(prefs with { LogRetentionDays = 30, DiagnosticsIncludeRunHistory = false, ReleaseArchivePath = @"D:\dl\March7thAssistant_full.zip" });
        var saved = await db.LoadPreferencesAsync();
        Assert.Equal(30, saved.LogRetentionDays);
        Assert.False(saved.DiagnosticsIncludeRunHistory);
        Assert.Equal(1, saved.Revision);
        await Assert.ThrowsAsync<SettingsConflictException>(() => db.SavePreferencesAsync(prefs));
    }

    [Fact]
    public async Task V2DatabaseUpgradesToV3KeepingRuns()
    {
        // Create a current DB, then roll it back to the exact v2 shape and upgrade again.
        var db = await CreateAsync();
        await db.PublishAsync(new RunUpdate(Guid.NewGuid(), Guid.NewGuid(), GameId.Genshin, "r", true, RunPhase.Pending, null,
            CleanupState.NotRequired, null, DateTimeOffset.UtcNow), default);
        await using (var c = new SqliteConnection("Pooling=False;Data Source=" + DbPath))
        {
            await c.OpenAsync();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DROP TABLE execution_plan_runs; DROP TABLE execution_plans; DROP TABLE account_configuration_audit; DROP TABLE account_scan_sessions; DROP TABLE account_login_profiles; DROP TABLE batch_execution_records; DROP TABLE account_switch_history; DROP TABLE account_execution_history; DROP TABLE account_task_plans; DROP TABLE account_settings; DROP TABLE game_accounts; DROP TABLE task_plans; DROP TABLE app_preferences; PRAGMA user_version=2;";
            await cmd.ExecuteNonQueryAsync();
        }
        var upgraded = await CreateAsync();
        Assert.Single(await upgraded.ReadRecentRunsAsync(10));
        Assert.Equal(14, (await upgraded.LoadPreferencesAsync()).LogRetentionDays);
        Assert.True(File.Exists(DbPath + ".pre-m5.1.bak"));
        await using (var backup = new SqliteConnection("Pooling=False;Mode=ReadOnly;Data Source=" + DbPath + ".pre-m5.1.bak"))
        {
            await backup.OpenAsync();
            using var check = backup.CreateCommand();
            check.CommandText = "PRAGMA integrity_check;";
            Assert.Equal("ok", await check.ExecuteScalarAsync());
        }
    }

    [Fact]
    public async Task PersistenceErrorsSurfaceAsExceptionsAndDoNotCorrupt()
    {
        var db = await CreateAsync();
        // A read-only database cannot be written; the error is reported, nothing is half-saved.
        await using (var c = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly;Pooling=False"))
        {
            await c.OpenAsync();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE app_preferences SET log_retention_days=20 WHERE id=1;";
            await Assert.ThrowsAsync<SqliteException>(() => cmd.ExecuteNonQueryAsync());
        }
        Assert.Equal(14, (await db.LoadPreferencesAsync()).LogRetentionDays);
    }
}

public sealed class DiagnosticsExportTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "vivian-diag-" + Guid.NewGuid().ToString("N"))).FullName;
    public void Dispose() => TestDirectories.DeleteWithRetry(_dir);

    [Fact]
    public void LogPruningDeletesOnlyOldVivianLogsAndKeepsTheNewest()
    {
        var logs = Directory.CreateDirectory(Path.Combine(_dir, "logs")).FullName;
        var now = new DateTime(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc);
        void Make(string name, int daysAgo, int bytes = 10)
        {
            var path = Path.Combine(logs, name);
            File.WriteAllBytes(path, new byte[bytes]);
            File.SetLastWriteTimeUtc(path, now.AddDays(-daysAgo));
        }
        Make("vivian-20261104.log", 0);
        Make("vivian-20261020.log", 15);
        Make("vivian-20261020_001.log", 15);
        Make("vivian-20261101.log", 3);
        Make("other.log", 100);
        Make("vivian-notes.log", 100);

        var deleted = LogMaintenance.Prune(logs, retentionDays: 14, maxTotalBytes: long.MaxValue, now);
        Assert.Equal(["vivian-20261020.log", "vivian-20261020_001.log"], deleted.Order(StringComparer.Ordinal));
        Assert.True(File.Exists(Path.Combine(logs, "other.log")));
        Assert.True(File.Exists(Path.Combine(logs, "vivian-notes.log")));

        var bySize = LogMaintenance.Prune(logs, 90, maxTotalBytes: 15, now);
        Assert.Equal(["vivian-20261101.log"], bySize);
        Assert.True(File.Exists(Path.Combine(logs, "vivian-20261104.log")));
        Assert.Empty(LogMaintenance.Prune(Path.Combine(_dir, "missing"), 14, 1, now));
    }

    [Fact]
    public async Task BundleIsRedactedAndExcludesDatabaseAndConfiguration()
    {
        var data = Directory.CreateDirectory(Path.Combine(_dir, "data")).FullName;
        var logs = Directory.CreateDirectory(Path.Combine(data, "logs")).FullName;
        File.WriteAllText(Path.Combine(logs, "vivian-20261104.log"), "2026 started for user Alice at C:\\Users\\Alice\\AppData token=SECRET123\n");
        File.WriteAllText(Path.Combine(data, "vivian.db"), "database");
        var report = await EngineReadinessInspector.InspectAsync(GameId.StarRail, @"C:\Users\Alice\M7");
        var run = new RunRecord(Guid.NewGuid(), Guid.NewGuid(), GameId.Genshin, "demo", true, RunPhase.Finished, RunOutcome.Success,
            CleanupState.NotRequired, "completed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "checked by Alice");
        var input = new DiagnosticBundleInput("0.4.0-dev", data, [report], [run],
            [new("d1", DateTimeOffset.UtcNow, "ok")], logs, true, DateTimeOffset.UtcNow);
        var redactor = new Redactor(@"C:\Users\Alice", "Alice", "ALICE-PC");

        var result = await DiagnosticBundle.CreateAsync(input, Path.Combine(_dir, "out"), redactor);

        Assert.True(result.Success, result.Detail);
        using var zip = ZipFile.OpenRead(result.Path!);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("manifest.json", names);
        Assert.Contains("engines.json", names);
        Assert.Contains("runs.json", names);
        Assert.Contains("logs/vivian-20261104.log", names);
        Assert.DoesNotContain(names, n => n.EndsWith(".db", StringComparison.Ordinal) || n.Contains("config", StringComparison.OrdinalIgnoreCase));
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            var text = await reader.ReadToEndAsync();
            Assert.DoesNotContain("Alice", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SECRET123", text);
        }
        Assert.False(File.Exists(result.Path + ".partial"));

        var withoutRuns = await DiagnosticBundle.CreateAsync(input with { IncludeRunHistory = false }, Path.Combine(_dir, "out"), redactor);
        using var zip2 = ZipFile.OpenRead(withoutRuns.Path!);
        Assert.DoesNotContain(zip2.Entries, e => e.FullName == "runs.json");
    }

    [Fact]
    public async Task RealRunDetailsAreExportedRedactedWithVersionAndUpstreamRunId()
    {
        var runId = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 11, 5, 23, 47, 1, TimeSpan.FromHours(-5));
        var run = new RunRecord(runId, Guid.NewGuid(), GameId.StarRail, "m7a-v2026.10.31-task-1", false, RunPhase.Finished,
            RunOutcome.Failed, CleanupState.Confirmed, ReasonCodes.ReportedFailure, at, at.AddMinutes(11), null, null);
        IReadOnlyList<RunEvent> events =
        [
            new(runId, at, RunEventKind.Output, @"[m7a] 已启动 screen_test，运行编号 20261105-234701-c953"),
            new(runId, at.AddMinutes(6), RunEventKind.Output, @"ERROR | 尝试启动游戏时发生错误：账号登录过期 C:\Users\胡桃\AppData password=hunter2"),
            new(runId, at.AddMinutes(11), RunEventKind.Warning, "[VIvian 判定] 界面测试失败：成功 0 个，失败 1 个，共 39 个界面。"),
        ];
        var input = new DiagnosticBundleInput("0.4.0-dev", _dir, [], [run], [], null, true, DateTimeOffset.UtcNow,
            "abc1234def56", "M4-F1 手动实测版", [new RunDiagnostic(run, events)]);
        var result = await DiagnosticBundle.CreateAsync(input, Path.Combine(_dir, "对应日志"), new Redactor(@"C:\Users\胡桃", "胡桃", "DESKTOP-X"));

        Assert.True(result.Success, result.Detail);
        using var zip = ZipFile.OpenRead(result.Path!);
        string Read(string name) { using var r = new StreamReader(zip.GetEntry(name)!.Open()); return r.ReadToEnd(); }
        Assert.Contains("abc1234def56", Read("manifest.json"));
        Assert.Contains("M4-F1 手动实测版", Read("README.txt"));
        var summary = Read("real-runs.json");
        Assert.Contains(runId.ToString(), summary);
        Assert.Contains("20261105-234701-c953", summary);
        Assert.Contains("\"Failed\"", summary);
        Assert.Contains("\"Confirmed\"", summary);
        var log = Read(zip.Entries.Single(e => e.FullName.StartsWith("runs/", StringComparison.Ordinal)).FullName);
        Assert.Contains("账号登录过期", log);
        Assert.Contains("[VIvian 判定]", log);
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            var text = await reader.ReadToEndAsync();
            Assert.DoesNotContain("胡桃", text);
            Assert.DoesNotContain("hunter2", text);
        }
    }

    [Fact]
    public async Task ExportIntoAFileInsteadOfAFolderFailsWithoutThrowing()
    {
        var blocker = Path.Combine(_dir, "not-a-folder");
        await File.WriteAllTextAsync(blocker, "x");
        var input = new DiagnosticBundleInput("x", _dir, [], [], [], null, false, DateTimeOffset.UtcNow);
        var result = await DiagnosticBundle.CreateAsync(input, blocker, Redactor.ForCurrentUser());
        Assert.False(result.Success);
        Assert.Null(result.Path);
    }

    [Fact]
    public async Task InvalidExportDirectoryIsReported()
    {
        var input = new DiagnosticBundleInput("x", _dir, [], [], [], null, false, DateTimeOffset.UtcNow);
        Assert.False((await DiagnosticBundle.CreateAsync(input, "relative", Redactor.ForCurrentUser())).Success);
    }
}
