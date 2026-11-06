using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using Serilog;
using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Diagnostics;
using VIvian.Infrastructure.Engines;
using VIvian.Infrastructure.Engines.Cli;
using VIvian.Infrastructure.Engines.BetterGi;
using VIvian.Infrastructure.Engines.OneDragon;
namespace VIvian.App;

/// <summary>Where VIvian keeps its own files. Nothing outside these folders is written by the application.</summary>
public sealed record AppPaths(string DataDirectory)
{
    public string LogDirectory => Path.Combine(DataDirectory, "logs");
    public string ExportDirectory => Path.Combine(DataDirectory, "exports");
    private static readonly string Informational =
        typeof(AppPaths).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
    public static string Version { get; } = Informational.Split('+')[0];
    /// <summary>Source commit stamped by scripts/Build.ps1, or "本地构建" for an unstamped local build.</summary>
    public static string Commit { get; } = Informational.Split('+') is [_, var sha, ..] && sha.Length >= 7 ? sha[..Math.Min(12, sha.Length)] : "本地构建";
}

public sealed class MainViewModel : ObservableObject
{
    private readonly ISettingsStore _settings;
    private readonly IDiagnosticStore _history;
    private readonly ITaskPlanStore _plans;
    private readonly IPreferenceStore _preferences;
    private readonly IRunHistory _runs;
    private readonly IProcessSupervisor _statusSupervisor;
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private string _status = RealExecutionPolicy.BuildPermitsRealExecution
        ? "单次星铁 screen_test 测试包：请先核对官方包与安装路径，再逐项确认本次执行。"
        : "就绪。此开发版只做只读检查和模拟运行，不启动任何真实引擎或游戏。";
    public string Status { get => _status; private set => Set(ref _status, value); }
    private bool _busy;
    public bool IsIdle => !_busy;
    private void Busy(bool value) { _busy = value; Raise(nameof(IsIdle)); }
    public bool LastCheckPassed { get; private set; }
    public string VersionText => $"VIvian {AppPaths.Version}（提交 {AppPaths.Commit}）· "
        + (RealExecutionPolicy.BuildPermitsRealExecution ? "星铁单次 screen_test 实机测试包" : "预桌面开发版（模拟引擎 + 只读引擎检查；真实任务未开放）");
    public string ExecutionBanner => RealExecutionPolicy.BuildPermitsRealExecution
        ? "星铁单次界面测试 · 需本人确认与 UAC" : "预桌面开发版 · 模拟引擎 · 真实任务未开放";
    public string DataDirectoryText => "数据目录：" + _paths.DataDirectory;
    public ObservableCollection<EngineCard> Engines { get; } =
    [new(GameId.Genshin, "原神", "BetterGI", "01"), new(GameId.StarRail, "崩坏：星穹铁道", "March7thAssistant", "02"), new(GameId.ZenlessZoneZero, "绝区零", "OneDragon", "03")];
    public ObservableCollection<DiagnosticRecord> History { get; } = [];
    public ObservableCollection<TaskPlanViewModel> TaskPlans { get; } = [];
    private TaskPlanViewModel? _selectedPlan;
    public TaskPlanViewModel? SelectedPlan { get => _selectedPlan; set => Set(ref _selectedPlan, value); }
    private EngineCard? _selectedEngine;
    public EngineCard? SelectedEngine { get => _selectedEngine; set => Set(ref _selectedEngine, value); }

    public AsyncCommand SaveCommand { get; }
    public AsyncCommand AutoDiscoverCommand { get; }
    public AsyncCommand ReloadCommand { get; }
    public AsyncCommand CheckCommand { get; }
    public AsyncCommand InspectMarch7thCommand { get; }
    public AsyncCommand CheckEnginesCommand { get; }
    public AsyncCommand VerifyReleaseCommand { get; }
    public AsyncCommand SaveTasksCommand { get; }
    public AsyncCommand ReloadTasksCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public AsyncCommand SavePreferencesCommand { get; }
    public AsyncCommand ExportDiagnosticsCommand { get; }
    public AsyncCommand StartMarch7thCommand { get; }
    public IReadOnlyList<EngineTaskDefinition> March7thTasks { get; } = RealExecutionPolicy.BuildPermitsRealExecution
        ? EngineCatalog.March7th.Tasks.Where(t => RealExecutionPolicy.AllowsTask(t.Id)).ToList()
        : EngineCatalog.March7th.Tasks;
    private EngineTaskDefinition? _march7thTask = EngineCatalog.March7th.Tasks.FirstOrDefault(t => t.Id ==
        (RealExecutionPolicy.BuildPermitsRealExecution ? "screen_test" : "daily"));
    public EngineTaskDefinition? March7thTask
    {
        get => _march7thTask;
        set { Set(ref _march7thTask, value); March7thConfirmedThisRun = false; }
    }
    private bool _march7thConfirmedThisRun;
    private string? _confirmedInstallPath;
    private string? _confirmedArchivePath;
    private string? _confirmedTask;
    public bool March7thConfirmedThisRun
    {
        get => _march7thConfirmedThisRun;
        set
        {
            Set(ref _march7thConfirmedThisRun, value);
            var card = Engines.Single(e => e.Id == GameId.StarRail);
            _confirmedInstallPath = value ? card.InstallPath : null;
            _confirmedArchivePath = value ? ReleaseArchivePath : null;
            _confirmedTask = value ? March7thTask?.Id : null;
            Raise(nameof(CanStartMarch7th));
        }
    }
    private bool _oneShotConsumed;
    private string OneShotMarker => Path.Combine(_paths.DataDirectory, "screen_test.attempted");
    public string March7thExecutionBlock => !RealExecutionPolicy.BuildPermitsRealExecution
        ? "真实执行闸门关闭：跨 UAC 工作进程清理及任务完成证据尚未经实机验证。"
        : _oneShotConsumed ? "本包的单次 screen_test 许可已使用；不会自动重试。"
        : "本包仅允许一次 screen_test，需核对固定版本、状态与本次确认；UAC 由你亲自处理。";
    public string March7thStopStatus => RealExecutionPolicy.BuildPermitsRealExecution
        ? "无害 UAC 边界已核对；上游 Worker 的停止结果需以本次实测为准。"
        : "跨权限安全停止：待实机验证；当前不能运行真实任务。";
    public string March7thConsentNote => RealExecutionPolicy.BuildPermitsRealExecution
        ? "本包仅含已获单次授权的 screen_test。启动前核对路径及任务；UAC 由你亲自操作。"
        : "首次真实任务必须先完成 UAC 与 Worker 实测，并另行取得你的单次游戏操作授权。";
    public bool CanStartMarch7th => RealExecutionPolicy.AllowsTask(March7thTask?.Id ?? "") && !_oneShotConsumed
        && March7thConfirmedThisRun && _march7thEvidence is not null && Queue.IsIdle && !Queue.HasUnresolved
        && Engines.Single(e => e.Id == GameId.StarRail).Report?.Compatibility == CompatibilityClass.IdentityVerified;

    private string _releaseArchivePath = "";
    /// <summary>A March7thAssistant release archive the user downloaded from the official GitHub release.</summary>
    public string ReleaseArchivePath
    {
        get => _releaseArchivePath;
        set
        {
            if (_releaseArchivePath == value) return;
            Set(ref _releaseArchivePath, value);
            _march7thEvidence = null;
            March7thConfirmedThisRun = false;
            ReleaseStatus = "发行包路径已改变，请重新核对。";
        }
    }
    private string _releaseStatus = "尚未核对官方发行包。";
    public string ReleaseStatus { get => _releaseStatus; private set => Set(ref _releaseStatus, value); }
    // Session only: a verified official archive's m7a.exe hash. Not stored; verify again after restarting.
    private March7thReleaseEvidence? _march7thEvidence;

    private AppPreferences _prefs = AppPreferences.Default;
    private int _logRetentionDays = AppPreferences.Default.LogRetentionDays;
    public int LogRetentionDays { get => _logRetentionDays; set => Set(ref _logRetentionDays, value); }
    private bool _includeRunHistory = true;
    public bool IncludeRunHistory { get => _includeRunHistory; set => Set(ref _includeRunHistory, value); }
    private string _exportStatus = "诊断包只保存在本机，已脱敏；是否发送给他人由你决定。";
    public string ExportStatus { get => _exportStatus; private set => Set(ref _exportStatus, value); }
    public string? LastExportPath { get; private set; }

    public QueueViewModel Queue { get; }

    public MainViewModel(ISettingsStore settings, IDiagnosticStore history, ITaskPlanStore plans, IPreferenceStore preferences,
        IRunHistory runs, AppPaths paths, QueueViewModel queue, IProcessSupervisor statusSupervisor)
    {
        _settings = settings; _history = history; _plans = plans; _preferences = preferences; _runs = runs; _paths = paths; Queue = queue;
        _statusSupervisor = statusSupervisor;
        _oneShotConsumed = File.Exists(OneShotMarker);
        SaveCommand = new(() => RunAsync(SaveAsync), HandleError);
        AutoDiscoverCommand = new(() => RunAsync(AutoDiscoverAsync), HandleError);
        ReloadCommand = new(() => RunAsync(async () => { await InitializeCoreAsync(); Status = "已重新加载保存的配置与记录。"; }), HandleError);
        CheckCommand = new(CheckFoundationAsync, HandleError);
        InspectMarch7thCommand = new(() => RunAsync(() => InspectAsync(Engines.Single(e => e.Id == GameId.StarRail))), HandleError);
        CheckEnginesCommand = new(() => RunAsync(CheckEnginesCoreAsync), HandleError);
        VerifyReleaseCommand = new(() => RunAsync(VerifyReleaseAsync), HandleError);
        SaveTasksCommand = new(() => RunAsync(SaveTasksAsync), HandleError);
        ReloadTasksCommand = new(() => RunAsync(LoadTasksAsync), HandleError);
        MoveUpCommand = new(() => SelectedPlan?.Move(-1));
        MoveDownCommand = new(() => SelectedPlan?.Move(1));
        SavePreferencesCommand = new(() => RunAsync(SavePreferencesAsync), HandleError);
        ExportDiagnosticsCommand = new(() => RunAsync(ExportDiagnosticsAsync), HandleError);
        StartMarch7thCommand = new(() => RunAsync(StartMarch7thAsync), HandleError);
        SelectedEngine = Engines[0];
    }

    public Task InitializeAsync() => InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        var settings = await Task.Run(() => _settings.LoadSettingsAsync());
        foreach (var value in settings)
        {
            var card = Engines.Single(x => x.Id == value.Game);
            card.InstallPath = value.InstallPath; card.Revision = value.Revision;
        }
        await LoadPreferencesAsync();
        await LoadTasksAsync();
        await RefreshHistoryAsync();
        await Queue.InitializeAsync();
        await RefreshLastRunsAsync();
        await CheckEnginesCoreAsync();
    }

    /// <summary>Read-only check of all three engine folders. Never runs an engine or touches its configuration.</summary>
    public Task CheckEnginesAsync() => RunAsync(CheckEnginesCoreAsync);

    private async Task CheckEnginesCoreAsync()
    {
        foreach (var card in Engines) await InspectAsync(card);
        Status = "已只读检查三个引擎目录；没有运行任何程序，也没有修改任何文件。";
    }

    private async Task InspectAsync(EngineCard card)
    {
        var path = card.InstallPath;
        var evidence = card.Id == GameId.StarRail ? _march7thEvidence : null;
        // Off the UI thread: a network path or hashing a large file must not freeze the window.
        var report = await Task.Run(() => card.Id switch
        {
            GameId.Genshin => BetterGiInstallationInspector.InspectAsync(path),
            GameId.ZenlessZoneZero => OneDragonInstallationInspector.InspectAsync(path),
            _ => EngineReadinessInspector.InspectAsync(card.Id, path, evidence, TimeProvider.System),
        });
        card.Show(report);
        if (card.Id == GameId.StarRail) Raise(nameof(CanStartMarch7th));
        Log.Information("Engine readiness {Game}: {Compatibility} ({Error})", card.Id, report.Compatibility, report.PrimaryError);
    }

    private async Task RefreshLastRunsAsync()
    {
        var recent = await Task.Run(() => _runs.ReadRecentRunsAsync(50));
        foreach (var card in Engines) card.ShowLastRun(recent.FirstOrDefault(r => r.Game == card.Id));
    }

    private async Task VerifyReleaseAsync()
    {
        var path = ReleaseArchivePath.Trim().Trim('"');
        // Archive evidence belongs to one exact user selection. A failed recheck must
        // never leave a previously verified executable authorised in this session.
        _march7thEvidence = null;
        ReleaseStatus = "正在计算 SHA-256（大文件需要一些时间）…";
        var result = await Task.Run(() => March7thReleaseVerifier.VerifyAsync(path));
        if (result.Check == ReleaseCheck.Verified) _march7thEvidence = result.Evidence;
        Raise(nameof(CanStartMarch7th));
        ReleaseStatus = result.Check switch
        {
            ReleaseCheck.Verified => "已验证：" + result.Detail,
            ReleaseCheck.OfficialContentsUnavailable => "是官方文件，但无法用于核对安装：" + result.Detail,
            ReleaseCheck.NotOfficial => "不是官方发行包：" + result.Detail,
            ReleaseCheck.UnexpectedLayout => "内容与官方发行版不符：" + result.Detail,
            _ => "无法核对：" + result.Detail,
        };
        Log.Information("Release archive check: {Check}", result.Check);
        if (path.Length > 0 && path != _prefs.ReleaseArchivePath && result.Check != ReleaseCheck.Unavailable)
        {
            // Remembering the archive location is a convenience; failing to store it never affects the check.
            try
            {
                await Task.Run(() => _preferences.SavePreferencesAsync(_prefs with { ReleaseArchivePath = path }));
                await LoadPreferencesAsync();
            }
            catch (Exception ex) when (ex is SettingsConflictException or ArgumentException) { }
        }
        await InspectAsync(Engines.Single(e => e.Id == GameId.StarRail));
    }

    private async Task StartMarch7thAsync()
    {
        if (!RealExecutionPolicy.AllowsTask(March7thTask?.Id ?? "") || _oneShotConsumed)
        { Status = March7thExecutionBlock; return; }
        var card = Engines.Single(e => e.Id == GameId.StarRail);
        if (!March7thConfirmedThisRun || _confirmedInstallPath != card.InstallPath
            || _confirmedArchivePath != ReleaseArchivePath || _confirmedTask != March7thTask?.Id)
        { Status = "安装目录、发行包或任务选择已改变；请重新确认本次真实执行。"; return; }
        if (_march7thEvidence is null || March7thTask is null)
        { Status = "请先核对固定版本的官方发行包并选择任务。"; return; }
        if (card.Report?.Compatibility != CompatibilityClass.IdentityVerified || Queue.HasUnresolved)
        { Status = "安装身份未核对，或有上次运行尚未确认清理；任务未启动。"; return; }
        if (!Queue.IsIdle) { Status = "已有队列正在运行。"; return; }
        March7thTaskContract.StartArguments(March7thTask.Id); // whitelist, even for a programmatic command call
        // CreateNew is atomic: a second window or restart cannot silently repeat this approved attempt.
        try
        {
            await using var marker = new FileStream(OneShotMarker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await using var writer = new StreamWriter(marker);
            await writer.WriteAsync($"{DateTimeOffset.UtcNow:O} screen_test 单次尝试已使用。\n");
            _oneShotConsumed = true;
            Raise(nameof(CanStartMarch7th)); Raise(nameof(March7thExecutionBlock));
        }
        catch (IOException) { _oneShotConsumed = true; Status = "单次许可标记已存在或无法写入；任务未启动。"; return; }
        var adapter = new March7thAdapter(card.InstallPath, _march7thEvidence, March7thTask.Id,
            () => March7thConfirmedThisRun && _confirmedInstallPath == card.InstallPath, _statusSupervisor);
        try
        {
            var result = await Queue.RunMarch7thAsync(adapter);
            Status = result is null ? "已有队列正在运行。" : "星铁本次任务已结束；请在“调度与监控”查看结果与清理状态。";
        }
        finally { March7thConfirmedThisRun = false; }
    }

    public void ReportReconciled(int count)
    {
        if (count > 0) Status = $"上次 VIvian 意外退出，{count} 条运行已标记为结果未知，请在“调度与监控”中查看并确认。";
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (!await _operation.WaitAsync(0)) return;
        Busy(true);
        try { await action(); }
        finally { Busy(false); _operation.Release(); }
    }

    private async Task SaveAsync()
    {
        var snapshot = Engines.Select(x => new EngineSetting(x.Id, x.InstallPath.Trim().Trim('"'), x.Revision)).ToList();
        await Task.Run(() => _settings.SaveSettingsAsync(snapshot));
        var reloaded = await Task.Run(() => _settings.LoadSettingsAsync());
        foreach (var value in reloaded)
        {
            var card = Engines.Single(x => x.Id == value.Game);
            card.InstallPath = value.InstallPath; card.Revision = value.Revision;
        }
        await CheckEnginesCoreAsync();
        Status = "路径已保存并完成只读检查；未运行引擎，也未改动上游配置。";
        Log.Information("Local settings saved for {Count} games", snapshot.Count);
    }

    private async Task AutoDiscoverAsync()
    {
        var candidates = new Dictionary<GameId, (string Root, string Program)[]>
        {
            [GameId.Genshin] = [("C:\\Program Files\\BetterGI", "BetterGI.exe"),
                ("E:\\auto agent game\\BetterGI", "BetterGI.exe")],
            [GameId.StarRail] = [("E:\\auto agent game\\March7thAssistant-official-v2026.10.31\\March7thAssistant_full", "m7a.exe")],
            [GameId.ZenlessZoneZero] = [("E:\\VIvianEngines\\OneDragon-v2.6.0", "OneDragon-RuntimeLauncher.exe"),
                ("E:\\auto agent game\\OneDragon", "OneDragon-RuntimeLauncher.exe"),
                ("E:\\ZenlessZoneZero-OneDragon", "OneDragon-RuntimeLauncher.exe"),
                ("C:\\Program Files\\OneDragon", "OneDragon-RuntimeLauncher.exe")],
        };
        var count = 0;
        foreach (var card in Engines.Where(e => string.IsNullOrWhiteSpace(e.InstallPath)))
        {
            var match = candidates[card.Id].FirstOrDefault(c => File.Exists(Path.Combine(c.Root, c.Program)));
            if (match.Root is null) continue;
            card.InstallPath = match.Root;
            await InspectAsync(card);
            count++;
        }
        Status = count == 0 ? "未发现可自动填入的新安装目录；可以手动选择。"
            : $"已自动填入 {count} 个安装目录并完成只读检查；请核对后点击“保存路径并检查”。";
    }

    private async Task LoadTasksAsync()
    {
        var plans = await Task.Run(() => _plans.LoadTaskPlansAsync());
        var selected = SelectedPlan?.Game;
        TaskPlans.Clear();
        foreach (var plan in plans) TaskPlans.Add(new TaskPlanViewModel(plan));
        SelectedPlan = TaskPlans.FirstOrDefault(p => p.Game == selected) ?? TaskPlans.FirstOrDefault();
    }

    /// <summary>Saves the task plans (used by the smoke test; the button uses the same path).</summary>
    public Task SaveTasksCommandAsync() => RunAsync(SaveTasksAsync);

    private async Task SaveTasksAsync()
    {
        foreach (var plan in TaskPlans.Where(p => p.IsDirty).ToList())
            await Task.Run(() => _plans.SaveTaskPlanAsync(plan.ToPlan()));
        await LoadTasksAsync();
        Status = "任务选择已保存。这里的计划不会触发真实执行；星铁单次测试只从专用页手动启动。";
        Log.Information("Task plans saved");
    }

    private async Task LoadPreferencesAsync()
    {
        _prefs = await Task.Run(() => _preferences.LoadPreferencesAsync());
        LogRetentionDays = _prefs.LogRetentionDays;
        IncludeRunHistory = _prefs.DiagnosticsIncludeRunHistory;
        if (ReleaseArchivePath.Length == 0) ReleaseArchivePath = _prefs.ReleaseArchivePath;
    }

    private async Task SavePreferencesAsync()
    {
        var next = _prefs with { LogRetentionDays = LogRetentionDays, DiagnosticsIncludeRunHistory = IncludeRunHistory };
        await Task.Run(() => _preferences.SavePreferencesAsync(next));
        await LoadPreferencesAsync();
        var removed = await Task.Run(() => LogMaintenance.Prune(_paths.LogDirectory, _prefs.LogRetentionDays, 200L * 1024 * 1024, DateTime.UtcNow));
        Status = $"设置已保存。已按新的保留期清理 {removed.Count} 个旧日志文件。";
    }

    public async Task ExportDiagnosticsAsync()
    {
        ExportStatus = "正在生成诊断包…";
        var reports = Engines.Select(e => e.Report).OfType<EngineReadinessReport>().ToList();
        var runs = IncludeRunHistory ? await Task.Run(() => _runs.ReadRecentRunsAsync(200)) : [];
        var checks = await Task.Run(() => _history.ReadRecentAsync(50));
        var input = new DiagnosticBundleInput(AppPaths.Version, _paths.DataDirectory, reports, runs, checks, _paths.LogDirectory,
            IncludeRunHistory, DateTimeOffset.UtcNow);
        var result = await Task.Run(() => DiagnosticBundle.CreateAsync(input, _paths.ExportDirectory, Redactor.ForCurrentUser()));
        LastExportPath = result.Path;
        ExportStatus = result.Success ? result.Detail + " 位置：" + result.Path : result.Detail;
        Log.Information("Diagnostic export: {Success}", result.Success);
    }

    private async Task RefreshHistoryAsync()
    {
        var items = await Task.Run(() => _history.ReadRecentAsync(50));
        History.Clear(); foreach (var item in items) History.Add(item);
    }

    public async Task CheckFoundationAsync()
    {
        LastCheckPassed = false;
        await RunAsync(async () =>
        {
            Status = "正在检查本地配置读取与数据库写入…";
            await Task.Run(() => _settings.LoadSettingsAsync());
            var record = new DiagnosticRecord(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
                "基础自检通过：配置读取、SQLite 写入及回读正常。未启动游戏。 ");
            await Task.Run(() => _history.AppendAsync(record));
            await RefreshHistoryAsync();
            if (!History.Any(x => x.Id == record.Id)) throw new InvalidOperationException("Read-back mismatch");
            LastCheckPassed = true;
            Status = record.Summary;
            Log.Information("Foundation diagnostic passed: {DiagnosticId}", record.Id);
        });
    }

    /// <summary>
    /// Path of one of VIvian's own folders (created if missing). VIvian does not launch Explorer: every process start is
    /// confined to the Job Object supervisor, so the user opens the copied path themselves.
    /// </summary>
    public string? FolderPath(string which)
    {
        var path = which == "exports" ? _paths.ExportDirectory : _paths.LogDirectory;
        try { Directory.CreateDirectory(path); return path; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "无法访问文件夹：" + path;
            return null;
        }
    }

    public void ReportCopied(string path) => Status = "已复制路径：" + path + "（可粘贴到资源管理器地址栏）";

    private void HandleError(Exception ex)
    {
        Status = UserFacingError.Describe(ex);
        Log.Warning("Operation failed: {ErrorType}", ex.GetType().Name);
    }
}

/// <summary>Turns exceptions into actionable Chinese messages. Never shows stack traces or file contents.</summary>
public static class UserFacingError
{
    public static string Describe(Exception ex) => ex switch
    {
        SettingsConflictException => ex.Message,
        ArgumentException a when a.Message.Length is > 0 and < 120 && !a.Message.Contains("Parameter", StringComparison.Ordinal)
            => "配置无效：" + a.Message,
        ArgumentException => "配置无效：路径过长或包含控制字符。",
        Microsoft.Data.Sqlite.SqliteException => "本地数据库暂时不可用（可能被占用或磁盘已满）。数据没有被修改；请稍后重试，必要时导出诊断包。",
        UnauthorizedAccessException => "没有访问权限。VIvian 不需要管理员权限；请确认数据目录可写。",
        IOException => "文件读写失败。请检查磁盘空间与数据目录权限后重试。",
        _ => "操作失败。请查看日志或导出诊断包。",
    };
}
