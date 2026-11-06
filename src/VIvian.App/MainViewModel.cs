using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
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
    public static string BuildTime { get; } = ReadBuildTime();
    private static string ReadBuildTime()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "BUILD-INFO.txt");
        if (!File.Exists(path)) return "本地构建时间未记录";
        return File.ReadLines(path).FirstOrDefault(x => x.StartsWith("构建时间(UTC):", StringComparison.Ordinal))?
            .Replace("构建时间(UTC):", "", StringComparison.Ordinal).Trim() ?? "本地构建时间未记录";
    }
}

public sealed class MainViewModel : ObservableObject
{
    private readonly ISettingsStore _settings;
    private readonly IDiagnosticStore _history;
    private readonly ITaskPlanStore _plans;
    private readonly IPreferenceStore _preferences;
    private readonly IRunHistory _runs;
    private readonly IUnifiedTaskControl _control;
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly SemaphoreSlim _sessionWrite = new(1, 1);
    private string _status = RealExecutionPolicy.IsReusableManualTest
        ? "手动实测版：先在“引擎管理”核对星铁助手安装与官方发行包，再到“真实任务测试”逐次确认执行。"
        : RealExecutionPolicy.BuildPermitsRealExecution
        ? "单次星铁 screen_test 测试包：请先核对官方包与安装路径，再逐项确认本次执行。"
        : "就绪。此开发版只做只读检查和模拟运行，不启动任何真实引擎或游戏。";
    public string Status { get => _status; private set => Set(ref _status, value); }
    private bool _busy;
    public bool IsIdle => !_busy;
    private void Busy(bool value) { _busy = value; Raise(nameof(IsIdle)); }
    public bool LastCheckPassed { get; private set; }
    public string VersionText => $"VIvian {AppPaths.Version}（提交 {AppPaths.Commit}，构建 {AppPaths.BuildTime}）· "
        + (RealExecutionPolicy.IsM5DailyTest ? "M5 每日实训手动实测版（逐次确认）"
            : RealExecutionPolicy.IsReusableManualTest ? "M4-F1 手动实测版（仅星铁 screen_test，每次需本人确认）"
            : RealExecutionPolicy.BuildPermitsRealExecution ? "星铁单次 screen_test 实机测试包" : "预桌面开发版（模拟引擎 + 只读引擎检查；真实任务未开放）");
    public string ExecutionBanner => RealExecutionPolicy.IsM5DailyTest ? "M5 每日实训 · 每次需本人确认与 UAC · 无定时执行"
        : RealExecutionPolicy.IsReusableManualTest
        ? "手动实测版 · 星铁 screen_test · 每次需本人确认与 UAC · 无定时/无人值守执行"
        : RealExecutionPolicy.BuildPermitsRealExecution
        ? "星铁单次界面测试 · 需本人确认与 UAC" : "预桌面开发版 · 模拟引擎 · 真实任务未开放";
    public string DataDirectoryText => "数据目录：" + _paths.DataDirectory;
    public Visibility M5Visibility => RealExecutionPolicy.IsM5DailyTest ? Visibility.Visible : Visibility.Collapsed;
    public string DiagnosticSessionText => "诊断会话：" + _diagnosticSessionId;
    private readonly string _diagnosticSessionId = Guid.NewGuid().ToString("N")[..12];
    private string? _sessionPath;
    public string RealTestTabHeader => RealExecutionPolicy.IsReusableManualTest ? "真实任务测试" : "星铁实测";
    public string RealTestTitle => RealExecutionPolicy.IsM5DailyTest ? "星铁 · 每日实训真实任务"
        : RealExecutionPolicy.IsReusableManualTest
        ? "真实任务测试 · 星铁 March7thAssistant screen_test" : "March7thAssistant · 单次真实任务";
    public string StartButtonText => RealExecutionPolicy.IsReusableManualTest ? "开始本次真实任务测试" : "启动本次星铁任务";
    public ObservableCollection<EngineCard> Engines { get; } =
    [new(GameId.Genshin, "原神", "BetterGI", "01"), new(GameId.StarRail, "崩坏：星穹铁道", "March7thAssistant", "02"), new(GameId.ZenlessZoneZero, "绝区零", "OneDragon", "03")];
    public ObservableCollection<DiagnosticRecord> History { get; } = [];
    public ObservableCollection<TaskPlanViewModel> TaskPlans { get; } = [];
    private TaskPlanViewModel? _selectedPlan;
    public TaskPlanViewModel? SelectedPlan { get => _selectedPlan; set { Set(ref _selectedPlan, value); if (value is not null) Configuration?.SelectGame(value.Game); } }
    public ConfigurationViewModel Configuration { get; }
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
    public AsyncCommand SaveFeedbackCommand { get; }
    public AsyncCommand StartMarch7thCommand { get; }
    public IReadOnlyList<EngineTaskDefinition> March7thTasks { get; } = RealExecutionPolicy.BuildPermitsRealExecution
        ? EngineCatalog.March7th.Tasks.Where(t => RealExecutionPolicy.AllowsTask(t.Id)).ToList()
        : EngineCatalog.March7th.Tasks;
    private EngineTaskDefinition? _march7thTask = EngineCatalog.March7th.Tasks.FirstOrDefault(t => t.Id ==
        (RealExecutionPolicy.IsM5DailyTest ? "daily" : RealExecutionPolicy.BuildPermitsRealExecution ? "screen_test" : "daily"));
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
            RaiseAdmission();
        }
    }
    private bool _oneShotConsumed;
    private string OneShotMarker => Path.Combine(_paths.DataDirectory, "screen_test.attempted");
    public string March7thExecutionBlock => RealExecutionPolicy.IsM5DailyTest
        ? "仅开放 daily；每次核验发行包、安装、配置及清理状态，并由本人确认。缺少完成证据时结果为未知。"
        : !RealExecutionPolicy.BuildPermitsRealExecution
        ? "真实执行闸门关闭：跨 UAC 工作进程清理及任务完成证据尚未经实机验证。"
        : RealExecutionPolicy.IsReusableManualTest
        ? "每次运行都要：核对安装、即时状态检查、上一次运行清理已确认、本人勾选确认。失败不会自动重试。"
        : _oneShotConsumed ? "本包的单次 screen_test 许可已使用；不会自动重试。"
        : "本包仅允许一次 screen_test，需核对固定版本、状态与本次确认；UAC 由你亲自处理。";
    public string March7thStopStatus => RealExecutionPolicy.BuildPermitsRealExecution
        ? "无害 UAC 边界已核对；上游 Worker 的停止结果需以本次实测为准。"
        : "跨权限安全停止：待实机验证；当前不能运行真实任务。";
    public string March7thConsentNote => RealExecutionPolicy.IsM5DailyTest
        ? "每日实训会操作真实游戏与账号。请先确认登录状态、原软件配置与本次任务，再由本人处理 UAC。"
        : RealExecutionPolicy.IsReusableManualTest
        ? "开始前请先手动打开星铁并确认账号已登录（登录过期会导致测试失败）。UAC 弹窗由你本人核对 m7a.exe 路径后处理；VIvian 不会自动提权。"
        : RealExecutionPolicy.BuildPermitsRealExecution
        ? "本包只支持单次 screen_test；实际游戏操作需单独授权。启动前核对路径及任务；UAC 由你亲自操作。"
        : "首次真实任务必须先完成 UAC 与 Worker 实测，并另行取得你的单次游戏操作授权。";
    private ManualTestAdmission Admission
    {
        get
        {
            var card = Engines.Single(e => e.Id == GameId.StarRail);
            return ManualTestPolicy.Evaluate(new ManualTestInputs(March7thTask?.Id, March7thConfirmedThisRun,
                _confirmedInstallPath == card.InstallPath && _confirmedArchivePath == ReleaseArchivePath
                    && _confirmedTask == March7thTask?.Id,
                _march7thEvidence is not null, card.Report?.Compatibility == CompatibilityClass.IdentityVerified,
                Queue.IsIdle, Queue.IsRunning, Queue.HasUnresolved, _oneShotConsumed));
        }
    }
    public bool CanStartMarch7th => Admission.Allowed;
    /// <summary>Current state in plain Chinese (未通过安装核验 / 清理未确认 / 正在执行 / 可以进行手动实测 …).</summary>
    public string ManualTestStateText => "当前状态：" + Admission.Text;
    private string _lastRealResult = "上次结果：本版本尚无真实运行记录。";
    public string LastRealResultText { get => _lastRealResult; private set => Set(ref _lastRealResult, value); }
    private void RaiseAdmission() { Raise(nameof(CanStartMarch7th)); Raise(nameof(ManualTestStateText)); }

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
        _control = new UnifiedTaskControl(queue, runs, statusSupervisor);
        Configuration = new ConfigurationViewModel(_control,
            game => Engines.Single(e => e.Id == game).InstallPath,
            () => Queue.IsRunning, Path.Combine(paths.DataDirectory, "configuration-audit"));
        // Only the M4-D one-attempt package is consumed by a marker; the reusable manual-test build re-checks every run.
        _oneShotConsumed = RealExecutionPolicy.IsOneShotScreenTest && File.Exists(OneShotMarker);
        Queue.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(QueueViewModel.IsRunning) or nameof(QueueViewModel.IsIdle) or nameof(QueueViewModel.HasUnresolved))
                RaiseAdmission();
        };
        SaveCommand = new(() => RunAsync(SaveAsync), HandleError);
        AutoDiscoverCommand = new(() => RunAsync(() => AutoDiscoverAsync(persist: true)), HandleError);
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
        SaveFeedbackCommand = new(() => RunAsync(SaveFeedbackAsync), HandleError);
        StartMarch7thCommand = new(() => RunAsync(StartMarch7thAsync), HandleError);
        Configuration.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConfigurationViewModel.Status) && _sessionPath is not null)
                _ = WriteDiagnosticSessionSafelyAsync();
        };
        SelectedEngine = Engines[0];
    }

    public Task InitializeAsync(bool autoPrepare = true) => InitializeCoreAsync(autoPrepare);

    private async Task InitializeCoreAsync(bool autoPrepare = false)
    {
        var settings = await Task.Run(() => _settings.LoadSettingsAsync());
        foreach (var value in settings)
        {
            var card = Engines.Single(x => x.Id == value.Game);
            card.InstallPath = value.InstallPath; card.Revision = value.Revision; card.SavedInstallPath = value.InstallPath;
        }
        await LoadPreferencesAsync();
        await LoadTasksAsync();
        if (RealExecutionPolicy.IsM5DailyTest && autoPrepare)
            SelectedPlan = TaskPlans.FirstOrDefault(p => p.Game == GameId.StarRail) ?? SelectedPlan;
        await RefreshHistoryAsync();
        await Queue.InitializeAsync();
        await RefreshLastRunsAsync();
        if (autoPrepare) await AutoDiscoverAsync(persist: true);
        await CheckEnginesCoreAsync();
        if (autoPrepare && RealExecutionPolicy.IsM5DailyTest)
        {
            var archive = @"E:\auto agent game\March7thAssistant_full-v2026.10.31.zip";
            if (File.Exists(archive))
            {
                ReleaseArchivePath = archive;
                await VerifyReleaseAsync();
            }
            if (!string.IsNullOrWhiteSpace(Engines.Single(e => e.Id == GameId.StarRail).InstallPath))
                try { await Configuration.ReadAsync(); }
                catch (Exception ex) { Status = "已发现星铁，但配置读取失败：" + UserFacingError.Describe(ex); }
        }
        if (autoPrepare) await StartDiagnosticSessionAsync();
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
        if (card.Id == GameId.StarRail) RaiseAdmission();
        Log.Information("Engine readiness {Game}: {Compatibility} ({Error})", card.Id, report.Compatibility, report.PrimaryError);
    }

    private async Task RefreshLastRunsAsync()
    {
        var recent = await Task.Run(() => _runs.ReadRecentRunsAsync(50));
        foreach (var card in Engines) card.ShowLastRun(recent.FirstOrDefault(r => r.Game == card.Id));
        var real = recent.FirstOrDefault(r => r.Game == GameId.StarRail && !r.IsSimulation);
        LastRealResultText = real is null ? "上次结果：本版本尚无真实运行记录。"
            : $"上次结果（{real.UpdatedAt.ToLocalTime():MM-dd HH:mm}，RunId {real.RunId:D}）：{RealResultLabel(real)}";
    }

    /// <summary>The fixed Chinese labels for a real run's outcome. Never "成功" without completion evidence.</summary>
    public static string RealResultLabel(RunRecord r) =>
        r.Cleanup == CleanupState.Unconfirmed ? "清理未确认（结果未知，队列已阻断）"
        : r.Phase == RunPhase.Blocked && r.Outcome is null ? "安全检查未通过（" + RunPresentation.Reason(r.ReasonCode) + "）"
        : r.Outcome switch
        {
            RunOutcome.Success => "执行完成（已核对完成证据与清理）",
            RunOutcome.Failed => "执行失败（" + RunPresentation.Reason(r.ReasonCode) + "）",
            RunOutcome.Cancelled => "已停止",
            _ => "结果未知（" + RunPresentation.Reason(r.ReasonCode) + "）",
        };

    private async Task VerifyReleaseAsync()
    {
        var path = ReleaseArchivePath.Trim().Trim('"');
        // Archive evidence belongs to one exact user selection. A failed recheck must
        // never leave a previously verified executable authorised in this session.
        _march7thEvidence = null;
        ReleaseStatus = "正在计算 SHA-256（大文件需要一些时间）…";
        var result = await Task.Run(() => March7thReleaseVerifier.VerifyAsync(path));
        if (result.Check == ReleaseCheck.Verified) _march7thEvidence = result.Evidence;
        RaiseAdmission();
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
        var starRail = Engines.Single(e => e.Id == GameId.StarRail);
        await InspectAsync(starRail);
        // M4-F1 defect C: a path the user actually verified is remembered even without pressing "保存路径".
        if (starRail.Report?.Compatibility == CompatibilityClass.IdentityVerified) await PersistInstallPathAsync(starRail);
    }

    /// <summary>Stores one engine's path if it differs from the saved value. Never touches the other engines' rows.</summary>
    private async Task PersistInstallPathAsync(EngineCard card)
    {
        var path = card.InstallPath.Trim().Trim('"');
        if (path == card.SavedInstallPath) return;
        try
        {
            var saved = await Task.Run(() => _settings.SaveSettingAsync(new EngineSetting(card.Id, path, card.Revision)));
            card.Revision = saved.Revision; card.SavedInstallPath = saved.InstallPath;
            Log.Information("Install path saved for {Game} (revision {Revision})", card.Id, saved.Revision);
        }
        catch (Exception ex) when (ex is SettingsConflictException or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
        {
            // Remembering the path is a convenience: the run itself is still decided by the live checks.
            Log.Warning("Install path not saved for {Game}: {ErrorType}", card.Id, ex.GetType().Name);
        }
    }

    private async Task StartMarch7thAsync()
    {
        var admission = Admission;
        if (!admission.Allowed && admission.State is ManualTestState.NotAvailable or ManualTestState.Consumed)
        { Status = admission.Text; return; }
        var card = Engines.Single(e => e.Id == GameId.StarRail);
        if (!March7thConfirmedThisRun || _confirmedInstallPath != card.InstallPath
            || _confirmedArchivePath != ReleaseArchivePath || _confirmedTask != March7thTask?.Id)
        { Status = "安装目录、发行包或任务选择已改变；请重新确认本次真实执行。"; return; }
        if (_march7thEvidence is null || March7thTask is null)
        { Status = "请先核对固定版本的官方发行包并选择任务。"; return; }
        if (card.Report?.Compatibility != CompatibilityClass.IdentityVerified || Queue.HasUnresolved)
        { Status = "安装身份未核对，或有上次运行尚未确认清理；任务未启动。"; return; }
        if (!Queue.IsIdle) { Status = "已有队列正在运行。"; return; }
        if (!admission.Allowed) { Status = admission.Text; return; }
        if (RealExecutionPolicy.IsM5DailyTest)
        {
            ConfigurationSnapshot config;
            try { config = _control.ReadConfiguration(GameId.StarRail, card.InstallPath); }
            catch (Exception ex) { Status = "上游配置检查失败，任务未启动：" + ex.Message; return; }
            if (config.Values.Single(v => v.Schema.Path == "pause_after_success").Value != "false" ||
                config.Values.Single(v => v.Schema.Path == "exit_after_failure").Value != "true")
            { Status = "请在原软件中关闭“成功后暂停”并开启“失败后退出”，重新读取配置后再执行。"; return; }
        }
        await PersistInstallPathAsync(card);
        if (RealExecutionPolicy.IsReusableManualTest)
        {
            // Reusable build: no package-wide marker, but every confirmed attempt is appended to a local journal
            // before anything starts. Failing to write it refuses the run (no silent, unrecorded attempts).
            try
            {
                Directory.CreateDirectory(_paths.DataDirectory);
                await File.AppendAllTextAsync(Path.Combine(_paths.DataDirectory, "manual-test-attempts.log"),
                    $"{DateTimeOffset.UtcNow:O} 用户确认一次 {March7thTask.Id}（提交 {AppPaths.Commit}）\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Status = "无法写入本地实测记录，任务未启动。请检查数据目录权限。"; return; }
        }
        else try
        {
            await using var marker = new FileStream(OneShotMarker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await using var writer = new StreamWriter(marker);
            await writer.WriteAsync($"{DateTimeOffset.UtcNow:O} screen_test 单次尝试已使用。\n");
            _oneShotConsumed = true;
            Raise(nameof(CanStartMarch7th)); Raise(nameof(March7thExecutionBlock));
        }
        catch (IOException) { _oneShotConsumed = true; Status = "单次许可标记已存在或无法写入；任务未启动。"; return; }
        try
        {
            var plan = _control.CreateTaskPlan(GameId.StarRail, [March7thTask.Id]);
            var result = await _control.ExecuteTaskPlan(plan, new RealTaskAuthorization(card.InstallPath,
                _march7thEvidence, () => March7thConfirmedThisRun && _confirmedInstallPath == card.InstallPath
                    && _confirmedArchivePath == ReleaseArchivePath && _confirmedTask == March7thTask?.Id,
                card.Report?.Compatibility == CompatibilityClass.IdentityVerified));
            Status = result is null ? "已有队列正在运行。" : "星铁本次任务已结束；结果与清理状态见下方。如有问题请点击“导出诊断报告”。";
        }
        finally
        {
            // Never carried over: the next attempt needs a new, explicit confirmation.
            March7thConfirmedThisRun = false;
            try { await RefreshLastRunsAsync(); } catch (Exception ex) { Log.Warning("Refresh failed: {ErrorType}", ex.GetType().Name); }
            await SaveFeedbackAsync();
            RaiseAdmission();
        }
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
        // Only rows the user changed are written, one by one, so an unchanged engine never gets a stale overwrite.
        var changed = Engines.Where(x => x.InstallPath.Trim().Trim('"') != x.SavedInstallPath).ToList();
        foreach (var card in changed)
        {
            var saved = await Task.Run(() => _settings.SaveSettingAsync(
                new EngineSetting(card.Id, card.InstallPath.Trim().Trim('"'), card.Revision)));
            card.Revision = saved.Revision; card.SavedInstallPath = saved.InstallPath;
        }
        var reloaded = await Task.Run(() => _settings.LoadSettingsAsync());
        foreach (var value in reloaded)
        {
            var card = Engines.Single(x => x.Id == value.Game);
            card.InstallPath = value.InstallPath; card.Revision = value.Revision; card.SavedInstallPath = value.InstallPath;
        }
        await CheckEnginesCoreAsync();
        Status = "路径已保存并完成只读检查；未运行引擎，也未改动上游配置。";
        Log.Information("Local settings saved for {Count} games", changed.Count);
    }

    private async Task AutoDiscoverAsync(bool persist = false)
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
        foreach (var card in Engines)
        {
            var program = card.Id switch
            {
                GameId.Genshin => "BetterGI.exe", GameId.StarRail => "m7a.exe", _ => "OneDragon-RuntimeLauncher.exe",
            };
            if (!string.IsNullOrWhiteSpace(card.InstallPath) && File.Exists(Path.Combine(card.InstallPath, program))) continue;
            var match = candidates[card.Id].FirstOrDefault(c => File.Exists(Path.Combine(c.Root, c.Program)));
            if (match.Root is null)
            {
                if (!string.IsNullOrWhiteSpace(card.InstallPath))
                {
                    card.InstallPath = "";
                    if (persist) await PersistInstallPathAsync(card);
                }
                continue;
            }
            card.InstallPath = match.Root;
            await InspectAsync(card);
            if (persist) await PersistInstallPathAsync(card);
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

    private static string BuildKind => RealExecutionPolicy.IsM5DailyTest ? "M5 每日实训手动实测版"
        : RealExecutionPolicy.IsReusableManualTest ? "M4-F1 手动实测版"
        : RealExecutionPolicy.BuildPermitsRealExecution ? "M4-D 单次测试包" : "开发版（模拟）";

    public async Task ExportDiagnosticsAsync()
    {
        try { await ExportDiagnosticsCoreAsync(); }
        catch (Exception ex)
        {
            // A diagnostic export must never take the application down.
            LastExportPath = null;
            ExportStatus = "导出诊断报告失败：" + UserFacingError.Describe(ex);
            Log.Warning("Diagnostic export failed: {ErrorType}", ex.GetType().Name);
        }
    }

    public async Task SaveFeedbackAsync()
    {
        await ExportDiagnosticsAsync();
        await WriteDiagnosticSessionAsync();
    }

    private async Task StartDiagnosticSessionAsync()
    {
        var feedbackRoot = FeedbackDirectory.DefaultRoot;
        foreach (var name in new[] { "待处理截图", "对应日志", "历史反馈", "测试报告" })
            Directory.CreateDirectory(Path.Combine(feedbackRoot, name));
        var root = FeedbackDirectory.Resolve(_paths.ExportDirectory);
        _sessionPath = Path.Combine(root, $"会话-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{_diagnosticSessionId}.json");
        await WriteDiagnosticSessionSafelyAsync();
        Log.Information("Diagnostic session {SessionId} started", _diagnosticSessionId);
    }

    private async Task WriteDiagnosticSessionSafelyAsync()
    {
        try { await WriteDiagnosticSessionAsync(); }
        catch (Exception ex) { Log.Warning("Diagnostic session write failed: {ErrorType}", ex.GetType().Name); }
    }

    private async Task WriteDiagnosticSessionAsync()
    {
        if (_sessionPath is null) return;
        await _sessionWrite.WaitAsync();
        try
        {
        var redactor = Redactor.ForCurrentUser();
        var recent = await _runs.ReadRecentRunsAsync(20);
        var report = new
        {
            sessionId = _diagnosticSessionId,
            version = AppPaths.Version,
            commit = AppPaths.Commit,
            buildTime = AppPaths.BuildTime,
            updatedAt = DateTimeOffset.UtcNow,
            engines = Engines.Select(e => new { game = e.Id.ToString(), state = e.Compatibility,
                version = e.Report?.DetectedVersion, reason = redactor.Redact(e.Evidence) }),
            configuration = redactor.Redact(Configuration.Status),
            runs = recent.Where(x => !x.IsSimulation).Select(x => new { x.RunId, x.Phase, x.Outcome,
                x.Cleanup, x.ReasonCode, x.UpdatedAt }),
            feedbackBundle = LastExportPath is null ? null : redactor.Redact(LastExportPath),
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        var temp = _sessionPath + ".tmp";
        await File.WriteAllTextAsync(temp, redactor.Redact(json));
        File.Move(temp, _sessionPath, overwrite: true);
        }
        finally { _sessionWrite.Release(); }
    }

    private async Task ExportDiagnosticsCoreAsync()
    {
        ExportStatus = "正在生成诊断包…";
        var reports = Engines.Select(e => e.Report).OfType<EngineReadinessReport>().ToList();
        var runs = IncludeRunHistory ? await Task.Run(() => _runs.ReadRecentRunsAsync(200)) : [];
        var checks = await Task.Run(() => _history.ReadRecentAsync(50));
        var real = new List<RunDiagnostic>();
        foreach (var run in runs.Where(r => !r.IsSimulation).Take(5))
            real.Add(new RunDiagnostic(run, await Task.Run(() => _runs.ReadEventsAsync(run.RunId, 2000))));
        var input = new DiagnosticBundleInput(AppPaths.Version, _paths.DataDirectory, reports, runs, checks, _paths.LogDirectory,
            IncludeRunHistory, DateTimeOffset.UtcNow, AppPaths.Commit, BuildKind, real);
        var destination = FeedbackDirectory.Resolve(_paths.ExportDirectory);
        var result = await Task.Run(() => DiagnosticBundle.CreateAsync(input, destination, Redactor.ForCurrentUser()));
        if (!result.Success && destination != _paths.ExportDirectory)
            result = await Task.Run(() => DiagnosticBundle.CreateAsync(input, _paths.ExportDirectory, Redactor.ForCurrentUser()));
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

/// <summary>
/// Where "导出诊断报告" writes: the user's screenshot-feedback folder when it exists (so screenshots and reports sit
/// together), otherwise VIvian's own export folder. VIVIAN_FEEDBACK_DIR overrides the feedback root.
/// </summary>
public static class FeedbackDirectory
{
    public const string DefaultRoot = @"E:\auto agent game\截图反馈";

    public static string Resolve(string fallback)
    {
        var root = Environment.GetEnvironmentVariable("VIVIAN_FEEDBACK_DIR");
        if (string.IsNullOrWhiteSpace(root)) root = DefaultRoot;
        try { return Directory.Exists(root) ? Path.Combine(root, "对应日志") : fallback; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return fallback; }
    }
}
