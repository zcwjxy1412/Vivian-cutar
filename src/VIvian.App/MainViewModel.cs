using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using Serilog;
using VIvian.Core;
using VIvian.Core.Accounts;
using VIvian.Core.Engines;
using VIvian.Core.Execution;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Accounts;
using VIvian.Infrastructure.Assistant;
using VIvian.Infrastructure.Diagnostics;
using VIvian.Infrastructure.Engines;
using VIvian.Infrastructure.Engines.Cli;
using VIvian.Infrastructure.Engines.BetterGi;
using VIvian.Infrastructure.Engines.OneDragon;
using VIvian.Infrastructure.Processes;
namespace VIvian.App;

/// <summary>Where VIvian keeps its own files. Nothing outside these folders is written by the application.</summary>
public sealed record AppPaths(string DataDirectory, string? RewardAcceptanceScope = null, Guid? RewardAcceptanceAccount = null)
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

public sealed record RecentRunRow(string Game, string When, string Result, bool Succeeded, string Duration);

/// <summary>Late-bound game path: the engine card is discovered after the DI graph is built.</summary>
public sealed class GamePathProvider
{
    public Func<string>? Resolve { get; set; }
    public string Get() => Resolve?.Invoke() ?? "";
}

public sealed partial class MainViewModel : ObservableObject, IExecutionPlanService
{
    private readonly ISettingsStore _settings;
    private readonly IDiagnosticStore _history;
    private readonly IPreferenceStore _preferences;
    private readonly IRunHistory _runs;
    private readonly IUnifiedTaskControl _control;
    private readonly GenshinDailyBatch? _genshinDailyBatch;
    private readonly IProcessSupervisor _zenlessSupervisor;
    private readonly GenshinDailyRecovery? _genshinDailyRecovery;
    private GenshinHomeReward? _homeGenshinReward;
    private readonly MultiAccountOrchestrator _orchestrator;
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly SemaphoreSlim _sessionWrite = new(1, 1);
    private string _status = RealExecutionPolicy.IsReusableManualTest
        ? "手动实测版：先在“引擎管理”核对星铁助手安装与官方发行包，再到“真实任务测试”逐次确认执行。"
        : RealExecutionPolicy.BuildPermitsRealExecution
        ? "单次星铁 screen_test 测试包：请先核对官方包与安装路径，再逐项确认本次执行。"
        : "就绪。此开发版只做只读检查和模拟运行，不启动任何真实引擎或游戏。";
    public string Status { get => _status; private set => ShowStatus(value); }
    internal HomeStatusAction StatusAction { get; private set; }
    public bool HasStatusAction => StatusAction != HomeStatusAction.None;
    public string StatusActionText => StatusAction switch
    {
        HomeStatusAction.RecoverDesktop => "前往核对桌面交接",
        HomeStatusAction.RestoreConfiguration => "前往恢复临时配置",
        HomeStatusAction.ReviewRuns => "前往运行日志",
        HomeStatusAction.SelectAccounts or HomeStatusAction.SelectGenshinAccounts or HomeStatusAction.SelectStarRailAccounts or HomeStatusAction.SelectZenlessAccounts => "前往账号管理",
        HomeStatusAction.ConfigureBetterGi => "前往设置 BetterGI 路径",
        _ => "",
    };

    internal void ShowStatus(string message, HomeStatusAction action = HomeStatusAction.None)
    {
        Set(ref _status, message, nameof(Status));
        if (StatusAction == action) return;
        StatusAction = action;
        Raise(nameof(StatusActionText));
        Raise(nameof(HasStatusAction));
    }
    private string _stopKeyText = "运行中可按 F10 立即停止（游戏全屏时也有效）。";
    public string StopKeyText { get => _stopKeyText; set => Set(ref _stopKeyText, value); }
    private bool _busy;
    public bool IsIdle => !_busy;
    public bool CanOperateWorkspace => _paths.RewardAcceptanceScope is null;
    public bool CanEditWorkspace => CanOperateWorkspace && IsIdle;
    private void Busy(bool value)
    {
        _busy = value;
        Raise(nameof(IsIdle));
        Raise(nameof(CanEditWorkspace));
    }
    public bool LastCheckPassed { get; private set; }
    public string VersionText => $"furina {AppPaths.Version}（提交 {AppPaths.Commit}，构建 {AppPaths.BuildTime}）· "
        + (RealExecutionPolicy.IsUnifiedInstall ? "VIvian 合流版" : RealExecutionPolicy.IsM5DailyTest ? "M5 星铁账号批次"
            : RealExecutionPolicy.IsReusableManualTest ? "M4-F1 手动实测版（仅星铁 screen_test，每次需本人确认）"
            : RealExecutionPolicy.BuildPermitsRealExecution ? "星铁单次 screen_test 实机测试包" : "预桌面开发版（模拟引擎 + 只读引擎检查；真实任务未开放）");
    public string ExecutionBanner => _paths.RewardAcceptanceScope ?? (RealExecutionPolicy.IsM5DailyTest ? "M5 星铁账号批次 · 首页启动"
        : RealExecutionPolicy.IsReusableManualTest
        ? "手动实测版 · 星铁 screen_test · 每次需本人确认与 UAC · 无定时/无人值守执行"
        : RealExecutionPolicy.BuildPermitsRealExecution
        ? "星铁单次界面测试 · 需本人确认与 UAC" : "预桌面开发版 · 模拟引擎 · 真实任务未开放");
    public string DailyStartHint => CanOperateWorkspace
        ? "账号管理勾选并排序，游戏管理保存任务设置。完成打开程序时的管理员授权后，在这里启动一次。"
        : "隔离验收使用独立快照。日常入口和配置仅供查看，可在这里停止本轮验收或核对桌面交接。";
    public string DataDirectoryText => "数据目录：" + _paths.DataDirectory;
    /// <summary>Page title of the home page, e.g. "11月6日 星期五".</summary>
    public string TodayText => DateTime.Now.ToString("M月d日 dddd", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));
    public Visibility LegacyRealTestVisibility => RealExecutionPolicy.BuildPermitsRealExecution && !RealExecutionPolicy.IsM5DailyTest
        ? Visibility.Visible : Visibility.Collapsed;
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
    public ConfigurationViewModel Configuration { get; }
    public AccountViewModel Accounts { get; }
    private EngineCard? _selectedEngine;
    public EngineCard? SelectedEngine { get => _selectedEngine; set => Set(ref _selectedEngine, value); }

    public AsyncCommand SaveCommand { get; }
    public AsyncCommand AutoDiscoverCommand { get; }
    public AsyncCommand ReloadCommand { get; }
    public AsyncCommand CheckCommand { get; }
    public AsyncCommand InspectMarch7thCommand { get; }
    public AsyncCommand CheckEnginesCommand { get; }
    public AsyncCommand VerifyReleaseCommand { get; }
    public AsyncCommand SavePreferencesCommand { get; }
    public AsyncCommand ExportDiagnosticsCommand { get; }
    public AsyncCommand SaveFeedbackCommand { get; }
    public AsyncCommand StartMarch7thCommand { get; }

    private bool _batchRunning;
    public bool BatchRunning
    {
        get => _batchRunning;
        private set { Set(ref _batchRunning, value); Raise(nameof(CanStopAll)); Raise(nameof(RunStateText)); }
    }
    /// <summary>The home stop button and F10 also cancel a login verification.</summary>
    public bool CanStopAll => BatchRunning || Queue.IsRunning || GenshinConfiguration.IsVerifyingLogin;
    public RelayCommand StopAllCommand { get; private set; } = null!;

    /// <summary>Actual token of this process, shown on the home page instead of a fixed claim.</summary>
    public string PrivilegeText { get; } = VIvian.Infrastructure.Processes.WindowsProcessSupervisor.IsCurrentProcessElevated()
        ? "管理员 · 打开时已授权，批次内不再弹窗"
        : "普通用户 · UAC 由你确认";

    public void StopAll()
    {
        try { _persistentPlans?.StopAll(); }
        catch (Exception ex) { Log.Warning(ex, "Persistent plan stop callback failed; continuing local stop channels"); }
        GenshinConfiguration.CancelLogin();
        if (BatchRunning)
        {
            BatchStatus = "正在停止批次并等待清理…";
            Status = BatchStatus;
        }
        _batchCts?.Cancel();
        if (Queue.IsRunning) Queue.EmergencyStop();
    }
    private string _batchStatus = "尚未运行多账号批次。";
    private HomeStatusAction _batchStatusAction;
    public string BatchStatus { get => _batchStatus; private set => SetBatchStatus(value); }

    private void SetBatchStatus(string message, HomeStatusAction action = HomeStatusAction.None)
    {
        _batchStatusAction = action;
        Set(ref _batchStatus, message, nameof(BatchStatus));
    }

    private HomeStatusAction PendingRunAction => Queue.Unresolved.Any(run => run.Game == GameId.Genshin && !run.IsSimulation)
        ? HomeStatusAction.RecoverDesktop : HomeStatusAction.ReviewRuns;

    private bool ShowPendingBatchRecovery()
    {
        if (Queue.HasUnresolved)
        {
            SetBatchStatus("上次运行的清理待确认。", PendingRunAction);
            return true;
        }
        if (!_control.HasPendingConfigurationRestore) return false;
        if (PendingRestoreText.Length == 0) PendingRestoreText = "上次运行的临时配置待恢复。";
        SetBatchStatus(PendingRestoreText, HomeStatusAction.RestoreConfiguration);
        return true;
    }
    public IReadOnlyList<EngineTaskDefinition> March7thTasks { get; } = RealExecutionPolicy.BuildPermitsRealExecution
        ? EngineCatalog.March7th.Tasks.Where(t => RealExecutionPolicy.AllowsTask(t.Id)).ToList()
        : EngineCatalog.March7th.Tasks;
    private EngineTaskDefinition? _march7thTask = EngineCatalog.March7th.Tasks.FirstOrDefault(t => t.Id ==
        (RealExecutionPolicy.IsM5DailyTest ? RealExecutionPolicy.M5RealTask : RealExecutionPolicy.BuildPermitsRealExecution ? "screen_test" : "daily"));
    public EngineTaskDefinition? March7thTask
    {
        get => _march7thTask;
        set { Set(ref _march7thTask, value); March7thConfirmedThisRun = false; AllowTemporaryConfigurationThisRun = false; RefreshTemporaryConfigurationNeeds(); }
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
    private bool _allowTemporaryConfiguration;
    /// <summary>Per-run consent to the listed temporary upstream setting changes; never carried over.</summary>
    public bool AllowTemporaryConfigurationThisRun
    {
        get => _allowTemporaryConfiguration;
        set => Set(ref _allowTemporaryConfiguration, value);
    }
    private string _temporaryConfigurationText = "";
    public string TemporaryConfigurationText
    {
        get => _temporaryConfigurationText;
        private set { Set(ref _temporaryConfigurationText, value); Raise(nameof(TemporaryConfigurationVisibility)); }
    }
    public Visibility TemporaryConfigurationVisibility =>
        RealExecutionPolicy.IsM5DailyTest && TemporaryConfigurationText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    private string _pendingRestoreText = "";
    public string PendingRestoreText
    {
        get => _pendingRestoreText;
        private set { Set(ref _pendingRestoreText, value); Raise(nameof(PendingRestoreVisibility)); }
    }
    public Visibility PendingRestoreVisibility => PendingRestoreText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public AsyncCommand RestoreConfigurationCommand { get; }
    public AsyncCommand RecoverDesktopCommand { get; }

    /// <summary>Shows which settings the selected task would temporarily change. Read-only; never writes.</summary>
    private void RefreshTemporaryConfigurationNeeds()
    {
        if (!RealExecutionPolicy.IsM5DailyTest || March7thTask is null) { TemporaryConfigurationText = ""; return; }
        var path = Engines.Single(e => e.Id == GameId.StarRail).InstallPath;
        if (string.IsNullOrWhiteSpace(path)) { TemporaryConfigurationText = ""; return; }
        try
        {
            var snapshot = _control.ReadConfiguration(GameId.StarRail, path);
            var changes = TemporaryConfigurationManager.Plan(snapshot, March7thTaskRequirements.For(March7thTask.Id));
            TemporaryConfigurationText = changes.Count == 0 ? "" :
                "允许本次临时修改：" + TemporaryConfigurationManager.Describe(changes, snapshot) + "（结束后自动恢复并核对）";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        { TemporaryConfigurationText = ""; }
    }

    private void ShowRestore(RestoreResult result)
    {
        PendingRestoreText = result.State == RestoreState.Blocked
            ? "临时配置尚未恢复：" + result.Detail + " 关闭星铁助手后点击“恢复临时配置”。" : "";
        Log.Information("Temporary configuration restore: {State}", result.State);
    }

    private async Task RestoreConfigurationAsync()
    {
        if (!CanOperateWorkspace) return;
        using var desktop = DesktopOperationLease.Acquire(_paths.DataDirectory, recovery: true);
        var result = await Task.Run(_control.RestorePendingConfiguration);
        ShowRestore(result);
        Status = result.Detail;
        RefreshTemporaryConfigurationNeeds();
    }

    private async Task RecoverDesktopAsync()
    {
        if (CanStopAll)
        {
            Status = "请先停止任务并等待本轮清理结束。";
            return;
        }
        if (_genshinDailyRecovery is { } recoverGenshin)
        {
            Guid? recovered;
            var recordedInSnapshot = false;
            var missingAudit = false;
            try
            {
                recovered = await Task.Run(() => _homeGenshinReward is { } home
                    ? BetterGiAccountBatch.RecoverSavedDailyAsync(_paths.DataDirectory, CancellationToken.None, async (runId, ct) =>
                    {
                        recordedInSnapshot = await home.RecordRecoveryAsync(runId, ct).ConfigureAwait(false);
                        if (!recordedInSnapshot)
                        {
                            var runs = await Accounts.Database.ReadBoundRunsAsync(GameId.Genshin, null, 20, ct).ConfigureAwait(false);
                            if (runs.SingleOrDefault(run => run.RunId == runId && !run.IsSimulation) is not { } officialRun)
                            {
                                missingAudit = true;
                                Log.Warning("Genshin recovery {RunId} verified; matching SQLite run was not recorded", runId);
                                return;
                            }
                            if (officialRun.IsUnresolved)
                                await Accounts.Database.AcknowledgeAsync(runId, "VIvian 核对了原神桌面交接与 BetterGI 协议恢复。",
                                    DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
                        }
                    })
                    : recoverGenshin(_paths.DataDirectory, CancellationToken.None));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException
                or UnauthorizedAccessException or TimeoutException or JsonException or Microsoft.Data.Sqlite.SqliteException)
            {
                Status = "原神桌面交接待确认：" + ex.Message;
                return;
            }
            if (recovered is { } runId)
            {
                Status = $"已核对并恢复原神日常的桌面交接（RunId {runId:D}）。";
                if (missingAudit) Status += " 对应的 SQLite 运行记录缺失，已单独记录恢复证据编号。";
                if (recordedInSnapshot || missingAudit) await Queue.RefreshAsync();
                else if (!await Queue.AcknowledgeGenshinRecoveryAsync(runId))
                    Status += " 运行日志的清理确认尚未写入，请核对该条记录。";
                return;
            }
        }
        if (!CanOperateWorkspace)
        {
            Status = "本验收档案没有可核对的原神恢复记录。";
            return;
        }
        if (Queue.HasUnresolved)
        {
            ShowStatus("请在运行日志中核对待清理的进程。", HomeStatusAction.ReviewRuns);
            return;
        }
        using var desktop = DesktopOperationLease.Acquire(_paths.DataDirectory, recovery: true);
        if (!desktop.Recovering) { Status = "桌面无待交接操作。"; return; }
        if (MessageBox.Show("请核对上一次运行的游戏、引擎和输入宿主均已退出。确认后会尝试恢复该档案的临时设置。",
                "核对桌面清理", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        var restore = await Task.Run(_control.RestorePendingConfiguration);
        ShowRestore(restore);
        if (restore.State == RestoreState.Blocked || _control.HasPendingConfigurationRestore)
        {
            Status = restore.Detail;
            return;
        }
        desktop.ConfirmClean();
        Status = "已完成桌面交接。";
    }

    private bool _oneShotConsumed;
    private string OneShotMarker => Path.Combine(_paths.DataDirectory, "screen_test.attempted");
    public string March7thExecutionBlock => RealExecutionPolicy.IsM5DailyTest
        ? "仅开放“日常（清体力 + 每日实训 + 领奖）”；每次核验发行包、安装、配置及清理状态，并由本人确认。缺少完成证据时结果为未知。"
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
        ? "每日实训会操作真实游戏与账号。请先登录游戏并关闭星铁助手窗口，再由本人处理 UAC。VIvian 以非交互模式运行，无需修改“成功后暂停/失败后退出”。"
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
    public bool CanStartMarch7th => CanOperateWorkspace && Admission.Allowed;
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

    public MainViewModel(ISettingsStore settings, IDiagnosticStore history, IPreferenceStore preferences,
        IRunHistory runs, AppPaths paths, QueueViewModel queue, IProcessSupervisor statusSupervisor,
        AccountViewModel accounts, MultiAccountOrchestrator orchestrator, GamePathProvider gamePath,
        GenshinDailyBatch? genshinDailyBatch = null, GenshinDailyRecovery? genshinDailyRecovery = null)
    {
        _orchestrator = orchestrator;
        if (RealExecutionPolicy.IsM5DailyTest && RealExecutionPolicy.IsUnifiedInstall)
        {
            _genshinDailyRecovery = genshinDailyRecovery;
            _genshinDailyBatch = genshinDailyRecovery is not null ? genshinDailyBatch : null;
        }
        gamePath.Resolve = () => March7thGamePath.Read(Engines.Single(e => e.Id == GameId.StarRail).InstallPath);
        _settings = settings; _history = history; _preferences = preferences; _runs = runs; _paths = paths; Queue = queue;
        Accounts = accounts;
        _zenlessSupervisor = statusSupervisor;
        accounts.ConfigureZenless(() => Engines.Single(card => card.Id == GameId.ZenlessZoneZero).SavedInstallPath,
            paths.DataDirectory, SerializeGenshinOperationAsync);
        accounts.ExecutionSelectionChanged += () => Raise(nameof(SelectedAccountsText));
        queue.StarRailInstallPath = () => Engines.Single(e => e.Id == GameId.StarRail).InstallPath;
        _control = new UnifiedTaskControl(queue, runs, statusSupervisor, paths.DataDirectory);
        InitializePersistentPlans();
        AssistantInputGate = new AssistantInputGate(paths.DataDirectory,
            () => AssistantInputBlocked);
        Configuration = new ConfigurationViewModel(_control,
            game => Engines.Single(e => e.Id == game).InstallPath,
            () => !CanOperateWorkspace || CanStopAll, paths.DataDirectory, accounts.Database, () => accounts.SelectedStarRail);
        if (CanOperateWorkspace) accounts.SelectionChanged += () => { _ = Configuration.SelectAccountAsync(); };
        GenshinConfiguration = new GenshinConfigurationViewModel(accounts.Database,
            new GenshinAccountConfigurationProvider(accounts.Database),
            () => CanEditWorkspace && !BatchRunning && !Queue.IsRunning, paths.DataDirectory,
            () => CanEditWorkspace && !BatchRunning && !Queue.IsRunning && !Queue.HasUnresolved && !_control.HasPendingConfigurationRestore,
            () => Engines.Single(e => e.Id == GameId.Genshin).SavedInstallPath, SerializeGenshinOperationAsync, CanOperateWorkspace);
        GenshinConfiguration.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GenshinConfigurationViewModel.IsVerifyingLogin)) Raise(nameof(CanStopAll));
            if (e.PropertyName == nameof(GenshinConfigurationViewModel.SelectedForDaily)) Raise(nameof(SelectedAccountsText));
        };
        // Only the M4-D one-attempt package is consumed by a marker; the reusable manual-test build re-checks every run.
        _oneShotConsumed = RealExecutionPolicy.IsOneShotScreenTest && File.Exists(OneShotMarker);
        Queue.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(QueueViewModel.IsRunning) or nameof(QueueViewModel.IsIdle) or nameof(QueueViewModel.HasUnresolved))
            { RaiseAdmission(); RaiseHome(); }
        };
        SaveCommand = new(() => RunAsync(SaveAsync), HandleError);
        AutoDiscoverCommand = new(() => RunAsync(() => AutoDiscoverAsync(persist: true)), HandleError);
        ReloadCommand = new(() => RunAsync(async () => { await InitializeCoreAsync(); Status = "已重新加载保存的配置与记录。"; }), HandleError);
        CheckCommand = new(CheckFoundationAsync, HandleError);
        InspectMarch7thCommand = new(() => RunAsync(() => InspectAsync(Engines.Single(e => e.Id == GameId.StarRail))), HandleError);
        CheckEnginesCommand = new(() => RunAsync(CheckEnginesCoreAsync), HandleError);
        VerifyReleaseCommand = new(() => RunAsync(VerifyReleaseAsync), HandleError);
        SavePreferencesCommand = new(() => RunAsync(SavePreferencesAsync), HandleError);
        ExportDiagnosticsCommand = new(() => RunAsync(ExportDiagnosticsAsync), HandleError);
        SaveFeedbackCommand = new(() => RunAsync(SaveFeedbackAsync), HandleError);
        StartMarch7thCommand = new(() => RunAsync(StartMarch7thAsync), HandleError);
        Accounts.ExecuteScan = action => RunAsync(async () =>
        {
            if (!CanOperateWorkspace)
            { Status = "隔离验收档案仅供查看，账号扫描未启动。"; return; }
            if (BatchRunning || Queue.IsRunning || Queue.HasUnresolved || _control.HasPendingConfigurationRestore)
            { Status = "任务运行或清理未确认时不能扫描切换账号。"; return; }
            using var desktop = DesktopOperationLease.Acquire(_paths.DataDirectory);
            var touched = false;
            try
            {
                await action(() =>
                {
                    if (touched) return;
                    desktop.MarkDirty();
                    touched = true;
                });
            }
            finally
            {
                if (touched) Status = "扫描可能留下星铁登录界面。关闭本轮游戏后，请先核对桌面交接再执行任务。";
            }
        });
        StopAllCommand = new(StopAll);
        Queue.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(QueueViewModel.IsRunning)) Raise(nameof(CanStopAll)); };
        RestoreConfigurationCommand = new(() => RunAsync(RestoreConfigurationAsync), HandleError);
        RecoverDesktopCommand = new(() => RunAsync(RecoverDesktopAsync), HandleError);
        OneClickCommand = new(scope => OneClickAsync(scope is GameId game ? game : null), HandleError);
        PlayGameCardCommand = new(scope => PlayGameCardAsync(scope is GameId game ? game : null), HandleError);
        foreach (var card in Engines)
        {
            card.CanOperateWorkspace = CanOperateWorkspace;
            card.GenshinDailyWired = card.Id == GameId.Genshin && _genshinDailyBatch is not null;
            card.Banner = new ArtSlot(card.Id switch { GameId.Genshin => Theme.BannerGenshin, GameId.StarRail => Theme.BannerStarRail, _ => Theme.BannerZenless });
            card.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(EngineCard.Compatibility)) RaiseHome(); };
        }
        bool ConfigurationBlocked() => !CanOperateWorkspace || CanStopAll || Queue.HasUnresolved || _control.HasPendingConfigurationRestore;
        var starRailConfiguration = new VIvian.Infrastructure.Assistant.StarRailAccountConfigurationProvider(
            accounts.Database, _control, () => Engines.Single(e => e.Id == GameId.StarRail).InstallPath, ConfigurationBlocked,
            () => DesktopOperationLease.Acquire(paths.DataDirectory));
        Assistant = new AssistantViewModel(Theme, new VIvian.Infrastructure.Assistant.AssistantSettingsStore(paths.DataDirectory),
            new VIvian.Infrastructure.Assistant.AssistantTools(
                () => Engines.Select(e => new VIvian.Infrastructure.Assistant.EngineStatusView(e.Id, e.Name, e.Engine, e.Version,
                    e.Report?.Compatibility.ToString() ?? "not_checked", e.CanRunForReal)).ToList(),
                ct => _runs.ReadRecentRunsAsync(20, ct), accounts.Database, [starRailConfiguration], executionPlans: this),
            target => target.Game == GameId.StarRail && accounts.SelectedStarRail?.Id == target.AccountId
                && !ConfigurationBlocked() && Configuration.PendingCount == 0,
            paths.DataDirectory, Configuration.ReadAsync, SubmitAssistantPlanAsync, executionPlans: this);
        accounts.SelectionChanged += Assistant.InvalidateProposals;
        Configuration.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConfigurationViewModel.PendingCount)) RaiseHome();
            // Results of writing settings are shown in the tip bar too, wherever the user is.
            if (e.PropertyName == nameof(ConfigurationViewModel.Status) && Configuration.Status is { } cs &&
                (cs.StartsWith("已写入", StringComparison.Ordinal) || cs.StartsWith("没有写入", StringComparison.Ordinal)
                 || cs.Contains("项修改已排队", StringComparison.Ordinal) || cs.StartsWith("原软件也改了", StringComparison.Ordinal)
                 || cs.StartsWith("已从星铁助手同步", StringComparison.Ordinal) || cs.StartsWith("已打开星铁助手", StringComparison.Ordinal)))
                Status = cs;
            if (e.PropertyName == nameof(ConfigurationViewModel.Status))
            {
                RefreshTemporaryConfigurationNeeds();
                if (_sessionPath is not null) _ = WriteDiagnosticSessionSafelyAsync();
            }
        };
        SelectedEngine = Engines[0];
    }

    public Task InitializeAsync(bool autoPrepare = true, bool verifyRelease = false) => InitializeCoreAsync(autoPrepare, verifyRelease);

    private async Task InitializeCoreAsync(bool autoPrepare = false, bool verifyRelease = false)
    {
        var settings = await Task.Run(() => _settings.LoadSettingsAsync());
        foreach (var value in settings)
        {
            var card = Engines.Single(x => x.Id == value.Game);
            card.InstallPath = value.InstallPath; card.Revision = value.Revision; card.SavedInstallPath = value.InstallPath;
        }
        await LoadPreferencesAsync();
        await RefreshHistoryAsync();
        await Queue.InitializeAsync();
        await Accounts.ReloadAsync();
        await Accounts.ReloadZenlessAsync();
        await GenshinConfiguration.LoadAsync(_paths.RewardAcceptanceAccount);
        await RefreshLastRunsAsync();
        if (autoPrepare) await AutoDiscoverAsync(persist: true);
        await CheckEnginesCoreAsync();
        if ((autoPrepare || verifyRelease) && RealExecutionPolicy.IsM5DailyTest)
        {
            var archive = string.IsNullOrWhiteSpace(ReleaseArchivePath)
                ? @"E:\auto agent game\March7thAssistant_full-v2026.10.31.zip" : ReleaseArchivePath;
            if (File.Exists(archive))
            {
                ReleaseArchivePath = archive;
                await VerifyReleaseAsync();
            }
        }
        if (autoPrepare && RealExecutionPolicy.IsM5DailyTest)
        {
            // An interrupted earlier run may have left a temporary change: restore it before anything else.
            if (_control.HasPendingConfigurationRestore) await RestoreConfigurationAsync();
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
        RecentRuns.Clear();
        foreach (var r in recent.Where(r => !r.IsSimulation).Take(4))
            RecentRuns.Add(new RecentRunRow(Engines.Single(e => e.Id == r.Game).Name, r.UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm"),
                RealResultLabel(r), r.Outcome == RunOutcome.Success && r.Cleanup != CleanupState.Unconfirmed,
                Duration(r.UpdatedAt - r.CreatedAt)));
        var finished = recent.Where(r => !r.IsSimulation && r.Outcome == RunOutcome.Success).Take(5).ToList();
        EstimateText = finished.Count == 0 ? "—" : "≈ " + Math.Max(1, (int)Math.Round(finished.Average(r => (r.UpdatedAt - r.CreatedAt).TotalMinutes))) + " 分钟";
        RaiseHome();
        LastRealResultText = real is null ? "上次结果：本版本尚无真实运行记录。"
            : $"上次结果（{real.UpdatedAt.ToLocalTime():MM-dd HH:mm}，RunId {real.RunId:D}）：{RealResultLabel(real)}";
    }

    private static string Duration(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}时{t.Minutes:D2}分" : $"{t.Minutes}分{t.Seconds:D2}秒";

    // ---- Home page (overview). Every figure comes from real state; nothing is decorative data. ----
    public ThemeAssets Theme { get; } = new();
    public ArtSlot CharacterArt => new(Theme.Character);
    public ArtSlot AssistantArt => new(Theme.Assistant);
    public GenshinConfigurationViewModel GenshinConfiguration { get; }
    public AssistantViewModel Assistant { get; }
    public IAssistantInputGate AssistantInputGate { get; }
    public bool AssistantInputBlocked => !CanOperateWorkspace || CanStopAll || Queue.HasUnresolved || _control.HasPendingConfigurationRestore;
    public void ReportAssistantInputBlocked(string reason) => Status = reason;
    public ObservableCollection<RecentRunRow> RecentRuns { get; } = [];
    private string _estimate = "—";
    public string EstimateText { get => _estimate; private set => Set(ref _estimate, value); }
    public string RunnableText => $"{Engines.Count(e => e.CanRunForReal)} / {Engines.Count}";
    public string SelectedAccountsText => (Accounts.SelectedRows.Count + GenshinConfiguration.SelectedForDaily.Count + Accounts.SelectedZenlessForDaily.Count).ToString();
    public string InstalledText => $"已发现 {Engines.Count(e => !string.IsNullOrWhiteSpace(e.InstallPath))} / {Engines.Count}";
    public string RunStateText => BatchRunning || Queue.IsRunning ? "运行中" : Queue.HasUnresolved || _persistentPlanFault ? "待确认" : "就绪";
    public string ConfigSyncText => !CanOperateWorkspace ? "只读"
        : Configuration.PendingCount > 0 ? $"{Configuration.PendingCount} 项待写入" : "已同步";
    private void RaiseHome()
    {
        foreach (var name in new[] { nameof(RunnableText), nameof(SelectedAccountsText), nameof(InstalledText), nameof(RunStateText), nameof(ConfigSyncText) })
            Raise(name);
    }
    public AsyncCommand OneClickCommand { get; }
    public AsyncCommand PlayGameCardCommand { get; }
    internal Func<Func<Task<int?>>, Task<int?>>? RunWithHomeRewardStopKey { private get; set; }

    internal void ConfigureHomeGenshinReward(GenshinHomeReward home)
    {
        _homeGenshinReward = home;
        Engines.Single(card => card.Id == GameId.Genshin).HomeRewardWired = _genshinDailyBatch is null;
        Queue.HomeRewardHistory = home.ReadRecentRunsAsync;
    }

    internal Task PlayGameCardAsync(GameId? game)
    {
        if (game is null) return Task.CompletedTask;
        if (game != GameId.Genshin || _homeGenshinReward is not { } home || _genshinDailyBatch is not null)
            return OneClickAsync(game);
        return RunAsync(async () =>
        {
            if (!CanOperateWorkspace || CanStopAll)
            { Status = "请先结束当前操作。"; return; }
            if (ShowPendingBatchRecovery())
            { ShowStatus(BatchStatus, _batchStatusAction); return; }
            if (!Engines.Single(card => card.Id == GameId.Genshin).CanPlayFromHome)
            { ShowStatus("请先保存并核对固定版本的 BetterGI 安装目录。", HomeStatusAction.ConfigureBetterGi); return; }
            if (GenshinConfiguration.SelectedForDaily.FirstOrDefault() is not { } selected)
            { ShowStatus("请在账号管理勾选并排序已绑定登录标识的原神账号。", HomeStatusAction.SelectGenshinAccounts); return; }
            if (RunWithHomeRewardStopKey is not { } withStopKey)
            { Status = "主窗口的停止功能尚未就绪，原神日常未启动。"; return; }

            var request = home.CreateRequest(selected);
            Status = $"原神日常 · 从 {selected.Name} 开始 · 按保存的任务顺序执行";
            await withStopKey(async () =>
            {
                await RunAccountBatchAsync(GameId.Genshin, acceptance: request, homeReward: home);
                ShowStatus(BatchStatus, _batchStatusAction);
                return null;
            });
        });
    }

    internal async Task<int?> OneClickAsync(GameId? scope = null, GenshinRewardAcceptance? acceptance = null,
        GenshinDailyVerification? verify = null)
    {
        int? exitCode = null;
        await RunAsync(async () =>
        {
            if (!RealExecutionPolicy.IsM5DailyTest)
            { Status = "此版本未启用真实日常批次。"; return; }
            if ((!CanOperateWorkspace) != (acceptance is not null) ||
                (acceptance is not null && (scope != GameId.Genshin || acceptance.ReviewOnly || verify is null)))
            { Status = "原神验收仅接受本次启动指定的请求。"; return; }
            if (scope is not (null or GameId.StarRail or GameId.Genshin or GameId.ZenlessZoneZero))
            { Status = "所选游戏的真实日常入口尚未开放。"; return; }
            if (acceptance is null && scope is null or GameId.Genshin && _genshinDailyBatch is null &&
                _homeGenshinReward is { } home && GenshinConfiguration.SelectedForDaily.FirstOrDefault() is { } selected)
            {
                if (RunWithHomeRewardStopKey is not { } withStopKey)
                { Status = "主窗口的停止功能尚未就绪，日常未启动。"; return; }
                var request = home.CreateRequest(selected);
                Status = $"原神日常 · 从 {selected.Name} 开始 · 按保存的任务顺序执行";
                await withStopKey(() => RunAccountBatchAsync(scope, acceptance: request, homeReward: home));
            }
            else if (acceptance is null && (scope == GameId.ZenlessZoneZero
                         || scope is null && Accounts.SelectedZenlessForDaily.Count > 0)
                     && RunWithHomeRewardStopKey is { } stopKey)
                exitCode = await stopKey(() => RunAccountBatchAsync(scope));
            else
                exitCode = await RunAccountBatchAsync(scope, acceptance: acceptance, verify: verify);
            ShowStatus(BatchStatus, _batchStatusAction);
        });
        return exitCode;
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
        if (!CanOperateWorkspace) return;
        if (Accounts.SelectedStarRail is { Id: var selectedId } && selectedId != Guid.Parse("00000000-0000-4000-8000-000000000002"))
        { Status = "当前选择的是新增账号。其设置尚未加载到上游；为避免用错账号配置，本次没有启动。"; return; }
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
        using var desktop = DesktopOperationLease.Acquire(_paths.DataDirectory);
        var allowTemporary = AllowTemporaryConfigurationThisRun;
        // Edits made on the Star Rail page must be in the upstream file before the engine reads it.
        if (Configuration.PendingCount > 0 && !Configuration.TryApplyPending(interactive: true, desktop))
        { Status = $"还有 {Configuration.PendingCount} 项配置修改没写入星铁助手（{Configuration.Status}）。任务未启动。"; return; }
        if (RealExecutionPolicy.IsM5DailyTest)
        {
            if (_control.HasPendingConfigurationRestore)
            { Status = "上一次运行的临时配置尚未恢复；请先点击“恢复临时配置”。任务未启动。"; return; }
            IReadOnlyList<TemporaryChange> changes;
            try { changes = _control.RequiredTemporaryChanges(card.InstallPath, March7thTask.Id); }
            catch (Exception ex) { Status = "上游配置检查失败，任务未启动：" + ex.Message; return; }
            if (changes.Count > 0 && !allowTemporary)
            { RefreshTemporaryConfigurationNeeds(); Status = "本任务需要临时修改上游设置：请勾选“允许本次临时修改”后再开始。"; return; }
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
                card.Report?.Compatibility == CompatibilityClass.IdentityVerified, allowTemporary), desktop);
            if (result.Queue is { Runs.Count: > 0 } completed
                && completed.Runs.All(r => r.Cleanup is CleanupState.Confirmed or CleanupState.NotRequired)
                && !_control.HasPendingConfigurationRestore)
                desktop.ConfirmClean();
            Status = result.Queue is null ? "已有队列正在运行。" : "星铁本次任务已结束；结果与清理状态见下方。如有问题请点击“导出诊断报告”。";
            if (result.Restore is not null) { ShowRestore(result.Restore); Status += " " + result.Restore.Detail; }
        }
        finally
        {
            // Never carried over: the next attempt needs a new, explicit confirmation.
            March7thConfirmedThisRun = false;
            AllowTemporaryConfigurationThisRun = false;
            if (_control.HasPendingConfigurationRestore && PendingRestoreText.Length == 0)
                PendingRestoreText = "临时配置尚未恢复；关闭星铁助手后点击“恢复临时配置”。";
            RefreshTemporaryConfigurationNeeds();
            try { await RefreshLastRunsAsync(); } catch (Exception ex) { Log.Warning("Refresh failed: {ErrorType}", ex.GetType().Name); }
            await SaveFeedbackAsync();
            RaiseAdmission();
        }
    }

    private CancellationTokenSource? _batchCts;
    private async Task<string> SubmitAssistantPlanAsync(AssistantRunPlan plan)
    {
        var outcome = "调度器正在处理其他操作。";
        await RunAsync(async () =>
        {
            await RunAccountBatchAsync(GameId.StarRail, plan);
            outcome = BatchStatus;
        });
        return outcome;
    }

    private async Task<int?> RunAccountBatchAsync(GameId? scope, AssistantRunPlan? approved = null,
        GenshinRewardAcceptance? acceptance = null, GenshinDailyVerification? verify = null, GenshinHomeReward? homeReward = null)
    {
        if (!CanOperateWorkspace && acceptance is null)
        { BatchStatus = "隔离验收只执行本次启动指定的原神任务。"; return null; }
        if (CanStopAll)
        { BatchStatus = "请先结束当前任务。"; return null; }
        if (ShowPendingBatchRecovery()) return null;
        if (approved is not null && (!RealExecutionPolicy.IsM5DailyTest || !Assistant.EnableScheduleProposals
            || approved.Target.Game != GameId.StarRail || approved.Expires <= DateTimeOffset.UtcNow
            || Accounts.SelectedStarRail?.Id != approved.Target.AccountId))
        { BatchStatus = "运行计划已失效，或当前选中账号和计划不一致。"; return null; }
        var starRail = scope is GameId.Genshin or GameId.ZenlessZoneZero ? [] : Accounts.SelectedRows
            .Where(row => approved is null || row.Id == approved.Target.AccountId).Select(row => row.Account).ToArray();
        var genshin = scope is GameId.StarRail or GameId.ZenlessZoneZero || acceptance is not null ? [] : GenshinConfiguration.SelectedForDaily.ToArray();
        var zenless = (scope is null or GameId.ZenlessZoneZero) && RealExecutionPolicy.IsUnifiedInstall
            && (acceptance is null || homeReward is not null) ? Accounts.SelectedZenlessForDaily.ToArray() : [];
        if (starRail.Length == 0 && genshin.Length == 0 && zenless.Length == 0 && acceptance is null)
        {
            SetBatchStatus("请先在账号管理勾选要执行的账号。", scope switch
            {
                GameId.Genshin => HomeStatusAction.SelectGenshinAccounts,
                GameId.StarRail => HomeStatusAction.SelectStarRailAccounts,
                GameId.ZenlessZoneZero => HomeStatusAction.SelectZenlessAccounts,
                _ => HomeStatusAction.SelectAccounts,
            });
            return null;
        }
        if (!WindowsProcessSupervisor.IsCurrentProcessElevated())
        { BatchStatus = "请以管理员身份打开程序并完成启动授权，再开始批次。"; return null; }

        if (approved is null && acceptance is null && verify is null && homeReward is null)
            return await RunPersistentHomePlanAsync([.. starRail, .. genshin, .. zenless]);

        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        _batchCts = cancellation;
        GameId? activeGame = null;
        var results = new List<string>();
        MultiAccountOutcome? acceptanceOutcome = null;
        var acceptancePassed = false;

        Action<BatchAccountProgress> Track(GameId game, int total)
        {
            activeGame = game;
            var gameName = RunPresentation.GameName(game);
            var completed = new HashSet<Guid>();
            return progress =>
            {
                void Show()
                {
                    if (activeGame != game || token.IsCancellationRequested || !ReferenceEquals(_batchCts, cancellation)) return;
                    if (progress.State == BatchRunState.Completed) completed.Add(progress.AccountId);
                    BatchStatus = $"{gameName} · 已完成 {completed.Count}/{total} · {progress.AccountName} · " +
                        $"{BatchProgressText.Describe(progress.State)} · {progress.Detail}";
                }

                if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
                    _ = dispatcher.InvokeAsync(Show);
                else Show();
            };
        }

        try
        {
            Func<CancellationToken, Task<MultiAccountOutcome>>? runGenshin = null;
            if (genshin.Length > 0 || acceptance is not null)
            {
                var batch = _genshinDailyBatch;
                if (acceptance is null && batch is null)
                    throw new InvalidOperationException("当前构建尚未接入原神日常执行与恢复。");
                var card = Engines.Single(e => e.Id == GameId.Genshin);
                var root = card.SavedInstallPath;
                var ready = acceptance is null ? card.CanRunForReal
                    : card.InstallPath == root && card.Report?.Compatibility == CompatibilityClass.PendingValidation;
                if (string.IsNullOrWhiteSpace(root) || !ready)
                {
                    SetBatchStatus("请先保存并核对 BetterGI 安装目录。", HomeStatusAction.ConfigureBetterGi);
                    return null;
                }
                if (acceptance is not null)
                {
                    BatchRunning = true;
                    BatchStatus = homeReward is null ? "原神 · 正在核对本次日常验收" : "原神 · 正在准备已保存的日常计划";
                    Log.Information("Genshin reward acceptance {Attempt} starting", acceptance.Attempt);
                }
                const string archive = ""; // Kept in the legacy delegate signature; native runs use the installed engine.
                runGenshin = async ct =>
                {
                    var total = acceptance is null ? genshin.Length : acceptance.RewardOnly ? 1
                        : GenshinConfiguration.SelectedForDaily.Count;
                    var progress = Track(GameId.Genshin, total);
                    BatchRunning = true;
                    Queue.HomeRewardRunning = homeReward is not null;
                    BatchStatus = $"原神 · 已完成 0/{total} · 正在核对账号和原版 BetterGI";
                    MultiAccountOutcome outcome;
                    try
                    {
                        outcome = await Task.Run(() => acceptance is null
                            ? batch!(Accounts.Database, genshin, root, archive, _paths.DataDirectory, progress, ct)
                            : homeReward is not null
                                ? homeReward.RunAsync(acceptance, root, archive, progress, ct)
                                : acceptance.RunAsync(Accounts.Database, root, archive, _paths.DataDirectory, verify!, progress, ct), ct);
                    }
                    finally { await Queue.RefreshAsync(); }
                    return outcome;
                };
            }

            Func<CancellationToken, Task<MultiAccountOutcome>>? runZenless = null;
            if (zenless.Length > 0)
            {
                var card = Engines.Single(e => e.Id == GameId.ZenlessZoneZero);
                runZenless = async ct =>
                {
                    if (!card.CanRunForReal)
                        throw new InvalidOperationException("请在引擎与环境保存并核对 OneDragon v2.6.0 安装。");
                    var progress = Track(GameId.ZenlessZoneZero, zenless.Length);
                    BatchRunning = true;
                    BatchStatus = "绝区零 · 正在核对勾选账号和 OneDragon 源档案";
                    try
                    {
                        return await Task.Run(() => OneDragonAccountBatch.ExecuteAsync(Accounts.Database, zenless,
                            card.SavedInstallPath, _paths.DataDirectory, _zenlessSupervisor, progress, ct), ct);
                    }
                    finally { await Queue.RefreshAsync(); }
                };
            }

            await DailyBatchSequence.RunAsync(scope, starRail.Length == 0 ? null :
                ct => RunStarRailBatchAsync(starRail, approved, Track(GameId.StarRail, starRail.Length), ct),
                runGenshin, (game, outcome) =>
                {
                    activeGame = null;
                    var finished = outcome.Completed && outcome.GameCleanupConfirmed && outcome.HostCleanupConfirmed;
                    var label = finished ? "批次结束" : "批次已停止";
                    if (acceptance is not null && game == GameId.Genshin)
                    {
                        acceptanceOutcome = outcome;
                        acceptancePassed = finished && outcome.Succeeded == acceptance.ExpectedAccountCount && outcome.Failed == 0 && !token.IsCancellationRequested;
                        label = acceptancePassed ? "日常验收通过" : "日常验收未通过";
                        Log.Information("Genshin reward acceptance {Attempt} batch {Batch} passed {Passed}; game cleanup {GameCleanup}; host cleanup {HostCleanup}",
                            acceptance.Attempt, outcome.BatchId, acceptancePassed, outcome.GameCleanupConfirmed, outcome.HostCleanupConfirmed);
                    }
                    results.Add($"{RunPresentation.GameName(game)} · {label} · {outcome.Detail}");
                    SetBatchStatus(string.Join("  ", results),
                        !finished && Queue.HasUnresolved ? PendingRunAction : HomeStatusAction.None);
                }, token, runZenless);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { BatchStatus = string.Join("  ", results.Append("批次已由用户停止。")); }
        catch (Exception ex)
        {
            // Method names and error codes locate failures without logging account data or file contents.
            var failureFrames = new System.Diagnostics.StackTrace(ex, false).GetFrames()
                .Select(frame => frame.GetMethod())
                .Where(method => method?.DeclaringType?.FullName?.StartsWith("VIvian.", StringComparison.Ordinal) == true)
                .Select(method => method!.DeclaringType!.FullName + "." + method.Name);
            Log.Warning("Daily batch failed: {ErrorType}; code {ErrorCode}; location {FailureFrames}; operation {Operation}",
                ex.GetType().Name, ex.HResult, string.Join(" > ", failureFrames), ex.Data["Operation"]);
            var detail = ex is InvalidOperationException or InvalidDataException or FileNotFoundException or TimeoutException
                ? ex.Message : UserFacingError.Describe(ex);
            SetBatchStatus(string.Join("  ", results.Append("批次失败：" + detail)),
                ex is DesktopRecoveryRequiredException ? HomeStatusAction.RecoverDesktop : HomeStatusAction.None);
        }
        finally
        {
            var started = BatchRunning;
            _batchCts = null;
            BatchRunning = false;
            Queue.HomeRewardRunning = false;
            if (started)
            {
                try
                {
                    if (starRail.Length > 0) await Accounts.ReloadAsync();
                    if (genshin.Length > 0 || acceptance is not null) await GenshinConfiguration.LoadAsync();
                    if (zenless.Length > 0) await Accounts.ReloadZenlessAsync();
                    await RefreshLastRunsAsync();
                }
                catch (Exception ex) { Log.Warning("Refresh after daily batch failed: {ErrorType}", ex.GetType().Name); }
            }
        }
        if (acceptanceOutcome is null || homeReward is not null) return null;

        var pendingRecovery = Queue.HasUnresolved || _control.HasPendingConfigurationRestore;
        var exitCode = await Task.Run(() => GenshinRewardAcceptance.AutoExitCode(acceptanceOutcome, acceptancePassed, pendingRecovery, () =>
        {
            try
            {
                using var cleanDesktop = DesktopOperationLease.Acquire(_paths.DataDirectory);
                return true;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                Log.Warning("Genshin reward acceptance desktop handoff not available: {ErrorType}", ex.GetType().Name);
                return false;
            }
        }));
        if (exitCode is null)
            SetBatchStatus(BatchStatus + " 请先核对本轮清理与桌面交接。", HomeStatusAction.RecoverDesktop);
        return exitCode;
    }

    private async Task<MultiAccountOutcome> RunStarRailBatchAsync(GameAccount[] accounts, AssistantRunPlan? approved,
        Action<BatchAccountProgress> progress, CancellationToken token, ExecutionPlanItemReceipt? persistent = null,
        Func<Guid, CancellationToken, Task>? beforeRun = null)
    {
        var card = Engines.Single(e => e.Id == GameId.StarRail);
        var evidence = _march7thEvidence;
        if (evidence is null || card.Report?.Compatibility != CompatibilityClass.IdentityVerified)
            throw new InvalidOperationException("请先在引擎与环境核对星铁官方发行包与安装身份。");
        using var desktop = DesktopOperationLease.Acquire(_paths.DataDirectory);
        if (approved is null && persistent is null ? !Configuration.TryApplyPending(interactive: false, desktop) : Configuration.PendingCount > 0)
            throw new InvalidOperationException("请先在游戏管理保存尚未写入的星铁设置。");

        var installPath = card.InstallPath;
        IReadOnlyDictionary<string, string>? approvedSettings = null;
        if (persistent is not null)
        {
            await ValidatePersistentItemAsync(persistent.Item, token);
            var current = await new StarRailAccountConfigurationProvider(Accounts.Database, _control, () => installPath,
                () => Queue.IsRunning || Queue.HasUnresolved || _control.HasPendingConfigurationRestore)
                .ReadAsync(new(GameId.StarRail, persistent.Item.AccountId), token);
            approvedSettings = current.Values.Where(v => v.Schema.Access == ParameterAccess.VerifiedReadWrite && v.Value is not null)
                .ToDictionary(v => v.Schema.Path, v => v.Value!, StringComparer.Ordinal);
        }
        if (approved is not null)
        {
            var provider = new StarRailAccountConfigurationProvider(Accounts.Database, _control, () => installPath,
                () => BatchRunning || Queue.IsRunning || Queue.HasUnresolved || _control.HasPendingConfigurationRestore);
            var current = await provider.ReadAsync(approved.Target, token);
            var plan = await Accounts.Database.LoadAccountTaskPlanAsync(approved.Target.AccountId, token);
            if (current.WriteUnavailableReason is not null || current.Revision != approved.ConfigurationRevision
                || !plan.DailyEnabled || plan.Revision != approved.TaskPlanRevision
                || current.Values.SingleOrDefault(v => v.Schema.Path == "break_down_level_four_relicset")?.Value != "false"
                || Accounts.SelectedStarRail?.Id != approved.Target.AccountId || !Assistant.EnableScheduleProposals
                || approved.Expires <= DateTimeOffset.UtcNow || card.InstallPath != installPath)
                throw new InvalidOperationException("账号、任务或资源配置已变化，请重新生成运行计划。");
            approvedSettings = current.Values.Where(v => v.Schema.Access == ParameterAccess.VerifiedReadWrite && v.Value is not null)
                .ToDictionary(v => v.Schema.Path, v => v.Value!, StringComparer.Ordinal);
        }
        token.ThrowIfCancellationRequested();
        BatchRunning = true;
        BatchStatus = $"星铁 · 已完成 0/{accounts.Length} · 正在读取游戏设置";
        if (approved is null && persistent is null) await Accounts.SaveSelectionsAsync(token);
        var upstream = _control.ReadConfiguration(GameId.StarRail, installPath);
        var defaults = upstream.Values
            .Where(v => v.Schema.Access == ParameterAccess.VerifiedReadWrite && v.Value is not null)
            .ToDictionary(v => v.Schema.Path, v => v.Value!, StringComparer.Ordinal);
        if (defaults.Count == 0)
            throw new InvalidOperationException("星铁助手没有可读取的任务配置，请检查游戏管理。");

        var snapshots = new Dictionary<Guid, IReadOnlyDictionary<string, string>>();
        foreach (var account in accounts)
        {
            var plan = await Accounts.Database.LoadAccountTaskPlanAsync(account.Id, token);
            if (!plan.DailyEnabled)
                throw new InvalidOperationException($"「{account.Name}」的日常任务已关闭，请在游戏管理启用或在账号管理取消勾选。");
            var snapshot = new Dictionary<string, string>(approvedSettings ?? defaults, StringComparer.Ordinal);
            if (approvedSettings is null && account.Id != LegacyStarRail)
            {
                var saved = await Accounts.Database.LoadAccountSettingsAsync(account.Id, token);
                foreach (var (key, value) in saved.Values) snapshot[key] = value;
            }
            var desired = new Dictionary<string, string>(snapshot, StringComparer.Ordinal);
            foreach (var (key, value) in March7thTaskRequirements.For(RealExecutionPolicy.M5RealTask))
                desired[key] = value;
            _ = TemporaryConfigurationManager.Plan(upstream, desired);
            snapshots.Add(account.Id, snapshot);
        }

        var outcome = await _orchestrator.ExecuteAsync(GameId.StarRail, accounts, AccountConfigurationMode.Independent,
            (account, _) => Task.FromResult(snapshots[account.Id]),
            (_, settings, ct) => RunAccountTasksAsync(installPath, evidence, settings, desktop, ct, beforeRun),
            progress, token, persistent?.ChildBatchId ?? approved?.Id, desktop.MarkDirty, stopOnFailure: persistent is not null);
        if (Queue.HasUnresolved || _control.HasPendingConfigurationRestore)
            return outcome with { Completed = false, Detail = outcome.Detail + " 清理或配置恢复待确认，已阻断后续执行。" };
        if (outcome.GameCleanupConfirmed && outcome.HostCleanupConfirmed) desktop.ConfirmClean();
        return outcome;
    }

    private static readonly Guid LegacyStarRail = Guid.Parse("00000000-0000-4000-8000-000000000002");

    private async Task<BatchTaskResult> RunAccountTasksAsync(string installPath, March7thReleaseEvidence evidence,
        IReadOnlyDictionary<string, string> settings, DesktopOperationLease desktop, CancellationToken cancellationToken,
        Func<Guid, CancellationToken, Task>? beforeRun = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var taskPlan = _control.CreateTaskPlan(GameId.StarRail, [RealExecutionPolicy.M5RealTask]);
        var result = await _control.ExecuteTaskPlan(taskPlan,
            new RealTaskAuthorization(installPath, evidence, () => !cancellationToken.IsCancellationRequested,
                true, AllowTemporaryConfiguration: true, AccountSettings: settings,
                BeforeRun: beforeRun is null ? null : id => beforeRun(id, cancellationToken)), desktop);
        if (result.Restore is not null) ShowRestore(result.Restore);
        var run = result.Queue?.Runs.LastOrDefault();
        var cleaned = run?.Cleanup == CleanupState.Confirmed;
        var restored = result.Restore?.State != RestoreState.Blocked && !_control.HasPendingConfigurationRestore;
        var success = run?.Outcome == RunOutcome.Success && cleaned && restored;
        var detail = success ? "日常完成。" : RunPresentation.Reason(run?.ReasonCode) ?? "任务完成或清理未确认。";
        if (result.Restore is not null) detail += " " + result.Restore.Detail;
        return new(run?.RunId, success, cleaned, restored, detail, run?.Outcome ?? RunOutcome.Indeterminate);
    }
    public void ReportReconciled(int count)
    {
        if (count > 0)
            ShowStatus($"上次 VIvian 意外退出，{count} 条运行已标记为结果未知。", HomeStatusAction.ReviewRuns);
    }

    internal void ReportGenshinRewardActivation(string message) => Status = BatchStatus = message;

    private async Task RunAsync(Func<Task> action)
    {
        if (!await _operation.WaitAsync(0)) return;
        Busy(true);
        try { await action(); }
        finally { Busy(false); _operation.Release(); }
    }

    internal async Task SerializeGenshinOperationAsync(Func<Task> action)
    {
        if (!CanOperateWorkspace)
            throw new InvalidOperationException("隔离验收档案仅供查看，原神设置和其他执行入口已锁定。");
        // The main operation gate also owns saving and auto-discovering engine paths.
        if (!await _operation.WaitAsync(0))
            throw new InvalidOperationException("主程序正在保存设置或处理任务，请稍后重试。");
        try { await action(); }
        finally { _operation.Release(); }
    }

    internal async Task<bool> WaitForDesktopOperationShutdownAsync(TimeSpan timeout)
    {
        StopAll();
        if (!await _operation.WaitAsync(timeout)) return false;
        _operation.Release();
        return !CanStopAll;
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

    private static string BuildKind => RealExecutionPolicy.IsUnifiedInstall ? "VIvian 合流版" : RealExecutionPolicy.IsM5DailyTest ? "M5 星铁账号批次"
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
        await WriteDiagnosticSessionSafelyAsync();
    }

    private async Task StartDiagnosticSessionAsync()
    {
        string root;
        try
        {
            var feedbackRoot = FeedbackDirectory.DefaultRoot;
            foreach (var name in new[] { "待处理截图", "对应日志", "历史反馈", "测试报告" })
                Directory.CreateDirectory(Path.Combine(feedbackRoot, name));
            root = FeedbackDirectory.Resolve(_paths.ExportDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            root = _paths.ExportDirectory;
            Directory.CreateDirectory(root);
            Log.Warning("Feedback folder unavailable; session saved in app data: {ErrorType}", ex.GetType().Name);
        }
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
        ShowStatus(ex is DesktopRecoveryRequiredException ? ex.Message : UserFacingError.Describe(ex),
            ex is DesktopRecoveryRequiredException ? HomeStatusAction.RecoverDesktop : HomeStatusAction.None);
        Log.Warning("Operation failed: {ErrorType}", ex.GetType().Name);
    }
}

internal enum HomeStatusAction
{
    None, RecoverDesktop, RestoreConfiguration, ReviewRuns, SelectAccounts, SelectGenshinAccounts, SelectStarRailAccounts, SelectZenlessAccounts, ConfigureBetterGi,
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
