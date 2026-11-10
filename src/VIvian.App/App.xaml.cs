using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Core.Execution;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Simulation;
using VIvian.Infrastructure.Processes;
using VIvian.Infrastructure.Diagnostics;
using VIvian.Infrastructure.Accounts;
using VIvian.Infrastructure.Engines.BetterGi;

namespace VIvian.App;

public partial class App : Application
{
    private IHost? _host;
    private SingleInstanceGuard? _instance;
    private WindowActivation? _activation;
    private bool _smoke;
    private bool _startupCheck;
    private string _stage = "初始化";
    private string? _dataRoot;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var rewardCommand = GenshinRewardAcceptance.HasCommand(e.Args);
        var rewardCandidate = GenshinRewardAcceptance.IsCandidateBuild;
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "vivian-host-boot.log"), $"{DateTimeOffset.Now:O} OnStartup args={(rewardCommand ? "Genshin reward acceptance" : string.Join('|', e.Args))}\n"); } catch { }
        // The elevated input host is a hidden second instance of this same executable. It must be handled before
        // single-instance, database, logging or any window exists, and it owns no user data.
        var hostIndex = Array.IndexOf(e.Args, InputHostProtocol.Argument);
        if (hostIndex >= 0 && !rewardCommand && !rewardCandidate)
        {
            var pipeName = hostIndex + 1 < e.Args.Length ? e.Args[hostIndex + 1] : string.Empty;
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "vivian-host-boot.log"), $"{DateTimeOffset.Now:O} entering host pipe={pipeName}\n"); } catch { }
            var exitCode = InputHostServer.Run(pipeName);
            Shutdown(exitCode);
            return;
        }
        _smoke = e.Args.Contains("--smoke-test");
        _startupCheck = e.Args.Contains("--startup-check");
        var preparingProfile = e.Args.FirstOrDefault() == "--prepare-unified-profile";
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Fatal("Unhandled UI error: {ErrorType}; {Stack}", args.Exception.GetType().Name,
                Redactor.ForCurrentUser().Redact(args.Exception.ToString()));
            args.Handled = true;
            var crash = StartupDiagnostics.Write(args.Exception, _dataRoot, "运行中未处理错误（" + _stage + "）");
            if (!_smoke) MessageBox.Show("程序遇到未处理错误，将退出。\n\n" + StartupDiagnostics.Explain(args.Exception) +
                (crash is null ? "" : "\n\n诊断报告：" + crash), "VIvian");
            Shutdown(1);
        };
        try
        {
            // Smoke tests use a disposable directory, never the user's profile.
            var screenTest = File.Exists(Path.Combine(AppContext.BaseDirectory, "VIvian-M4A-ScreenTest.marker"));
            var manualTest = File.Exists(Path.Combine(AppContext.BaseDirectory, "VIvian-M4-F1-ManualTest.marker"));
            var m5Daily = File.Exists(Path.Combine(AppContext.BaseDirectory, "VIvian-M5-DailyTest.marker"));
            var unified = File.Exists(Path.Combine(AppContext.BaseDirectory, "VIvian-UnifiedInstall.marker"));
            var reward = GenshinRewardAcceptance.Parse(e.Args, rewardCandidate,
                File.Exists(Path.Combine(AppContext.BaseDirectory, GenshinRewardAcceptance.Marker)),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            if (unified != RealExecutionPolicy.IsUnifiedInstall)
                throw new InvalidOperationException("程序与合流安装标记不匹配，拒绝启动。");
            // Each real-execution build must carry its own marker (and only its own): its data stays isolated.
            if (RealExecutionPolicy.IsOneShotScreenTest && (!screenTest || manualTest))
                throw new InvalidOperationException("单次测试包缺少隔离数据标记，拒绝启动。");
            if (RealExecutionPolicy.IsReusableManualTest && !RealExecutionPolicy.IsM5DailyTest && (!manualTest || screenTest || m5Daily))
                throw new InvalidOperationException("手动实测版缺少隔离数据标记，拒绝启动。");
            if (RealExecutionPolicy.IsM5DailyTest && (m5Daily == unified || manualTest || screenTest))
                throw new InvalidOperationException("M5 每日实训包缺少独立标记，拒绝启动。");
            if (!RealExecutionPolicy.BuildPermitsRealExecution && manualTest)
                throw new InvalidOperationException("普通构建不能使用手动实测版的数据目录。");
            var threeEnginePreview = File.Exists(Path.Combine(AppContext.BaseDirectory, "VIvian-M4-ThreeEngine-Preview.marker"));
            if (threeEnginePreview && RealExecutionPolicy.BuildPermitsRealExecution)
                throw new InvalidOperationException("三引擎预览包不允许真实执行许可。");
            if (reward is not null)
            {
                _stage = "核对原神领奖验收权限";
                if (!unified || !RealExecutionPolicy.IsM5DailyTest || !Environment.UserInteractive ||
                    !WindowsProcessSupervisor.IsCurrentProcessElevated())
                    throw new InvalidOperationException("原神领奖验收要求交互式 Windows 会话中的管理员合流候选程序。");
                if (!reward.ReviewOnly)
                {
                    _stage = "准备原神领奖验收隔离快照";
                    await Task.Run(() => UnifiedProfilePreparation.CreateGenshinRewardAcceptanceSnapshotAsync(
                        reward.ProfileDirectory, CancellationToken.None));
                }
            }
            if (preparingProfile)
            {
                _stage = "准备正式合流库";
                if (!unified) throw new InvalidOperationException("请使用合流安装候选程序准备正式数据。");
                if (_smoke)
                {
                    var fixture = SmokeDataDirectory(e.Args);
                    await Task.Run(() => PrepareSmokeProfileAsync(fixture));
                }
                else
                {
                    if (e.Args.Length != 2 || !Path.IsPathFullyQualified(e.Args[1]))
                        throw new ArgumentException("请指定尚不存在的绝对暂存目录。");
                    await Task.Run(() => UnifiedProfilePreparation.PrepareFromExistingProfilesAsync(e.Args[1], CancellationToken.None));
                }
                Shutdown(0);
                return;
            }
            var preview = e.Args.Contains("--m4-preview")
                || File.Exists(Path.Combine(AppContext.BaseDirectory, "VIvian-M4A-Preview.marker"));
            var root = reward is not null ? reward.ProfileDirectory : _smoke
                ? SmokeDataDirectory(e.Args)
                : unified ? UnifiedProfilePreparation.InstalledDirectory
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    RealExecutionPolicy.IsM5DailyTest ? "VIvian-M6-StarRailTest"
                    : RealExecutionPolicy.IsReusableManualTest ? "VIvian-M6-ManualTest"
                    : screenTest ? "VIvian-M6-ScreenTest" : threeEnginePreview ? "VIvian-M6-ThreeEngine-Preview"
                    : preview ? "VIvian-M6-Preview" : "VIvian-M6-Development");
            var usePreparedProfile = unified && (!_smoke || _startupCheck);
            _stage = "创建数据目录";
            if (!usePreparedProfile) Directory.CreateDirectory(root);
            // Smoke runs must never write into the user's screenshot-feedback folder.
            if (_smoke) Environment.SetEnvironmentVariable("VIVIAN_FEEDBACK_DIR", Path.Combine(root, "no-feedback-folder"));
            // One instance per data directory, decided before the database or logs are touched.
            _stage = "单实例检查";
            _instance = SingleInstanceGuard.Acquire(root);
            if (!_instance.IsOwner)
            {
                // A second launch brings the open window to the front instead of only saying it is open. If the lock
                // holder never shows a window (hung, windowless, or left over from a test), say so instead of
                // exiting silently, which looks exactly like "VIvian does not open".
                var signal = reward is not null || _smoke || _instance.Result == InstanceLockResult.AccessDenied
                    ? WindowActivation.SignalResult.NoListener
                    : WindowActivation.SignalAndWait(root, TimeSpan.FromSeconds(4));
                if (signal == WindowActivation.SignalResult.Shown)
                {
                    Shutdown(2);
                    return;
                }
                if (!_smoke && reward is null)
                    MessageBox.Show(_instance.Result == InstanceLockResult.AccessDenied
                        ? "另一个 VIvian 进程正在使用同一数据目录，而且它以管理员或其他用户身份运行（例如之前测试遗留的进程）。\n" +
                          "为避免冲突，本窗口不会启动，也不会替你结束那个进程。\n\n" +
                          "处理方法：打开任务管理器 →“详细信息”→ 结束名为 VIvian.exe 的进程（或重启电脑），然后再打开 VIvian。"
                        : signal == WindowActivation.SignalResult.NoResponse
                        ? "另一个 VIvian 进程占用了数据目录，但没有显示窗口也没有响应（可能已卡住或是遗留进程）。\n" +
                          "本窗口不会启动，也不会替你结束它。\n\n" +
                          "处理方法：打开任务管理器 →“详细信息”→ 结束名为 VIvian.exe 的进程，然后再打开 VIvian。"
                        : "VIvian 已经在运行（可能还在启动中），请稍等片刻后切换到已打开的窗口。", "VIvian");
                Shutdown(_instance.Result == InstanceLockResult.AccessDenied ? 3 : 2);
                return;
            }
            if (usePreparedProfile)
            {
                _stage = "校验正式合流库";
                await Task.Run(() => UnifiedProfilePreparation.VerifyPrepared(root));
            }
            _dataRoot = root;
            Log.Logger = new LoggerConfiguration().MinimumLevel.Information()
                .WriteTo.File(Path.Combine(root, "logs", "vivian-.log"), rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14, fileSizeLimitBytes: 5_000_000, rollOnFileSizeLimit: true).CreateLogger();
            if (_instance.Result == InstanceLockResult.AcquiredAfterAbandoned)
                Log.Warning("Previous instance exited without releasing the instance lock");
            _stage = "打开数据库";
            var database = new LocalDatabase(Path.Combine(root, "vivian.db"), requirePreparedProfile: usePreparedProfile);
            _host = Host.CreateDefaultBuilder().UseSerilog().ConfigureServices(services =>
            {
                services.AddSingleton(database);
                services.AddSingleton<ISettingsStore>(database);
                services.AddSingleton<IDiagnosticStore>(database);
                services.AddSingleton<IRunJournal>(database);
                services.AddSingleton<IRunHistory>(database);
                services.AddSingleton<ITaskPlanStore>(database);
                services.AddSingleton<IPreferenceStore>(database);
                services.AddSingleton(new AppPaths(root, reward?.WorkspaceScope, reward?.AccountId));
                // The current build still injects the refusing supervisor. A separately reviewed
                // real-execution build would use the existing Windows Job supervisor.
                services.AddSingleton<IProcessSupervisor>(_ => RealExecutionPolicy.BuildPermitsRealExecution
                    ? new WindowsProcessSupervisor() : new RefusingProcessSupervisor());
                services.AddSingleton(sp => new QueueScheduler(sp.GetRequiredService<IRunJournal>(),
                    sp.GetRequiredService<IProcessSupervisor>(),
                    options: RealExecutionPolicy.BuildPermitsRealExecution ? SchedulerOptions.Default with
                    { EmergencyStopTimeout = TimeSpan.FromSeconds(45), ReconcileTimeout = TimeSpan.FromSeconds(95) }
                    : SchedulerOptions.Default,
                    admitReal: adapter =>
                        reward is null && RealExecutionPolicy.BuildPermitsRealExecution &&
                        adapter is VIvian.Infrastructure.Engines.March7thAdapter));
                services.AddSingleton<QueueViewModel>();
                services.AddSingleton<LoginScreenReader>();
                services.AddSingleton<AccountRecognitionService>();
                // One host object serves input and the per-batch UAC session, so a batch prompts at most once.
                services.AddSingleton<ElevatedInputHost>();
                services.AddSingleton<IInputHost>(sp => sp.GetRequiredService<ElevatedInputHost>());
                services.AddSingleton<IInputHostSession>(sp => sp.GetRequiredService<ElevatedInputHost>());
                services.AddSingleton<IGameSessionVerifier, StarRailSessionVerifier>();
                services.AddSingleton<GamePathProvider>();
                services.AddSingleton<ILoginSwitchDriver>(sp => new StarRailLoginDriver(
                    sp.GetRequiredService<LoginScreenReader>(), sp.GetRequiredService<IGameSessionVerifier>(),
                    sp.GetRequiredService<IInputHost>(), sp.GetRequiredService<GamePathProvider>().Get,
                    message => Log.Information("Login driver: {Message}", message),
                    prepareLaunch: () => StarRailResolution.Apply(message => Log.Information("Login driver: {Message}", message)),
                    saveEvidence: async (view, name) =>
                    {
                        // Kept on this computer only (the frame can show a masked phone number or UID); 30 newest.
                        var dir = Path.Combine(root, "login-evidence");
                        Directory.CreateDirectory(dir);
                        var path = await LoginScreenReader.SaveEvidenceAsync(view, dir,
                            $"{DateTime.Now:yyyyMMdd-HHmmss}-{name}", CancellationToken.None);
                        Log.Warning("Login flow stopped; screen saved to {Path}", path);
                        foreach (var old in new DirectoryInfo(dir).GetFiles("*.json").OrderByDescending(f => f.CreationTimeUtc).Skip(30))
                            old.Delete();
                    }));
                services.AddSingleton(sp => new MultiAccountOrchestrator(sp.GetRequiredService<LocalDatabase>(),
                    sp.GetRequiredService<ILoginSwitchDriver>(), sp.GetRequiredService<IInputHostSession>()));
                services.AddSingleton<IScanWindowShield, MainWindowScanShield>();
                services.AddSingleton<AccountViewModel>();
                if (RealExecutionPolicy.IsM5DailyTest && RealExecutionPolicy.IsUnifiedInstall)
                {
                    services.AddSingleton<GenshinDailyRecovery>(BetterGiAccountBatch.RecoverSavedDailyAsync);
                    if (reward is null && BetterGiContract.LaunchContractVerified && BetterGiContract.CompletionSignalsVerified)
                        services.AddSingleton<GenshinDailyBatch>(BetterGiAccountBatch.ExecuteSavedDailyAsync);
                }
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
            }).Build();
            await _host.StartAsync();
            await Task.Run(() => database.InitializeAsync());
            if (_smoke && !_startupCheck)
            {
                var recognized = await database.AddAccountAsync(GameId.Genshin, "原神识别样例", "", null);
                await database.SaveLoginProfileAsync(recognized.Id, "130******00", "130****00", 0, .9, null, "测试数据");
            }
            await Task.Run(() => database.ReconcileInterruptedBatchesAsync());
            // Log retention follows the user's preference; only VIvian's own log files are considered.
            var prefs = await Task.Run(() => database.LoadPreferencesAsync());
            var pruned = await Task.Run(() => VIvian.Infrastructure.Diagnostics.LogMaintenance.Prune(
                Path.Combine(root, "logs"), prefs.LogRetentionDays, 200L * 1024 * 1024, DateTime.UtcNow));
            if (pruned.Count > 0) Log.Information("Removed {Count} old log files", pruned.Count);
            // Safe only while holding the instance lock: no other VIvian can have a run in progress.
            var reconciled = await Task.Run(() => database.ReconcileInterruptedRunsAsync(DateTimeOffset.UtcNow));
            if (reconciled.Count > 0)
                Log.Warning("Reconciled {Count} runs interrupted by an unexpected exit", reconciled.Count);
            _stage = "加载界面数据";
            var vm = _host.Services.GetRequiredService<MainViewModel>();
            if (unified && !_smoke && reward is null && _instance is { IsOwner: true } installedInstance)
                vm.ConfigureHomeGenshinReward(new GenshinHomeReward(root,
                    (destination, ct) => UnifiedProfilePreparation.CreateGenshinHomeRewardSnapshotAsync(destination, installedInstance, ct),
                    BetterGiAccountBatch.VerifySavedDailyAsync));
            try
            {
                await vm.InitializeAsync(autoPrepare: !_smoke && reward is null);
            }
            catch (Exception ex) when (!_smoke && reward is null && !DatabaseVersionTooNew.TryRead(ex, out _, out _))
            {
                // Engine discovery or a damaged upstream config must not keep VIvian from opening.
                Log.Error("Initial preparation failed; continuing: {ErrorType}; {Stack}", ex.GetType().Name,
                    Redactor.ForCurrentUser().Redact(ex.ToString()));
                StartupDiagnostics.Write(ex, root, "加载界面数据（已跳过，继续启动）");
            }
            vm.ReportReconciled(reconciled.Count);
            if (_startupCheck)
            {
                await File.WriteAllTextAsync(Path.Combine(root, "startup.ok"),
                    $"{DateTimeOffset.UtcNow:O} {AppPaths.Version} {AppPaths.Commit} auto discovery and diagnostics completed");
                Shutdown(0);
                return;
            }
            _stage = "创建主窗口";
            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            if (reward is { ReviewOnly: false })
            {
                async void RunRewardAcceptance(object? sender, EventArgs args)
                {
                    window.ContentRendered -= RunRewardAcceptance;
                    var exitCode = await window.RunWithRewardAcceptanceStopKeyAsync(
                        () => vm.OneClickAsync(GameId.Genshin, reward, BetterGiAccountBatch.VerifySavedDailyAsync));
                    if (exitCode is { } code && !Dispatcher.HasShutdownStarted) Shutdown(code);
                }
                window.ContentRendered += RunRewardAcceptance;
            }
            if (_smoke)
            {
                window.ContentRendered += async (_, _) =>
                {
                    try
                    {
                        await vm.CheckFoundationAsync();
                        if (!vm.LastCheckPassed) throw new InvalidOperationException("Foundation smoke failed");
                        if (RealExecutionPolicy.BuildPermitsRealExecution &&
                            (vm.March7thTasks.Count != 1 || vm.March7thTasks[0].Id != (RealExecutionPolicy.IsM5DailyTest ? RealExecutionPolicy.M5RealTask : "screen_test") || vm.CanStartMarch7th))
                            throw new InvalidOperationException("Real-test admission is not constrained");
                        if (RealExecutionPolicy.IsReusableManualTest && !vm.ManualTestStateText.Contains("未通过安装核验", StringComparison.Ordinal))
                            throw new InvalidOperationException("Manual-test page does not report the unverified installation");
                        using (var second = SingleInstanceGuard.Acquire(root))
                            if (second.IsOwner) throw new InvalidOperationException("Second instance was not refused");
                        var queueReport = await SmokeAssistantIsolationAsync(window, vm, database);
                        var engineReport = await SmokeEngineDiscoveryAsync(vm, root);
                        var tasksReport = await SmokeAccountWorkflowAsync(window, vm, database, root);
                        var contrastReport = SaveTabScreenshots(window, root);
                        await File.WriteAllTextAsync(Path.Combine(root, "smoke.ok"),
                            "WPF content rendered; SQLite round-trip passed; second instance refused; " + queueReport + " " + engineReport + " " + tasksReport + " " + contrastReport);
                        Shutdown(0);
                    }
                    catch (Exception ex)
                    {
                        // Smoke-only: the messages are VIvian's own check descriptions (no user data).
                        Log.Error("Smoke failed: {ErrorType}: {Message}", ex.GetType().Name, ex.Message);
                        Shutdown(1);
                    }
                };
            }
            _stage = "显示主窗口";
            window.Show();
            _stage = "运行中";
            if (!_smoke && reward is null) _activation = WindowActivation.Listen(root, window);
            // M4-F1 defect B: say what this build actually allows instead of a fixed "not enabled" line.
            Log.Information("VIvian {Version} ({Commit}) started. {Mode}", AppPaths.Version, AppPaths.Commit,
                reward is not null
                    ? "Genshin single-account reward acceptance profile."
                    : RealExecutionPolicy.IsUnifiedInstall
                    ? "Unified install with prepared profile. Genshin and Zenless real execution remain gated."
                    : RealExecutionPolicy.IsM5DailyTest
                    ? "M5 daily manual-test build: per-run consent, pinned identity and cleanup admission."
                    : RealExecutionPolicy.IsReusableManualTest
                    ? "Manual-test build: March7thAssistant screen_test only, each run confirmed by the user; no scheduled or unattended real runs."
                    : RealExecutionPolicy.BuildPermitsRealExecution
                    ? "One-attempt screen_test package: a single user-confirmed March7thAssistant screen_test."
                    : "Real engines are not enabled in this build.");
        }
        catch (Exception ex)
        {
            Log.Error("Startup failed at {Stage}: {ErrorType}; {Stack}", _stage, ex.GetType().Name,
                Redactor.ForCurrentUser().Redact(ex.ToString()));
            var report = StartupDiagnostics.Write(ex, _dataRoot, _stage);
            if (!_smoke && !_startupCheck && !preparingProfile && !rewardCommand && !rewardCandidate)
                MessageBox.Show($"VIvian 启动失败（阶段：{_stage}）。\n\n{StartupDiagnostics.Explain(ex)}\n\n" +
                    (report is null ? "无法写入诊断报告。" : $"诊断报告：{report}") + "\n原数据不会被删除或修改。", "VIvian");
            Shutdown(1);
        }
    }
    /// <summary>
    /// Smoke check for M2: runs a simulation queue through the real view model, scheduler and SQLite journal while a
    /// background probe measures how long the UI thread takes to answer; then checks the emergency stop.
    /// </summary>
    private static string SmokeDataDirectory(string[] args)
    {
        // UAC can start the real-test package with a fresh environment.
        var index = Array.IndexOf(args, "--smoke-data");
        var requested = index < 0 ? Environment.GetEnvironmentVariable("VIVIAN_SMOKE_DATA")
            : index + 1 < args.Length ? args[index + 1] : null;
        if (string.IsNullOrWhiteSpace(requested)) throw new InvalidOperationException("Missing smoke directory");
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requested));
        var relative = Path.GetRelativePath(Path.GetTempPath(), directory);
        if (!Path.GetFileName(directory).StartsWith("vivian-smoke-", StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(directory) || Path.IsPathRooted(relative) || relative is "." or ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid isolated smoke directory");
        for (var folder = new DirectoryInfo(directory); folder is not null; folder = folder.Parent)
            if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Smoke directory contains a link");
        return directory;
    }

    private static async Task PrepareSmokeProfileAsync(string fixture)
    {
        var oldPath = Path.Combine(fixture, "v6.db");
        var importedPath = Path.Combine(fixture, "v7.db");
        if (!File.Exists(oldPath) && !File.Exists(importedPath))
        {
            var old = new LocalDatabase(oldPath);
            await old.InitializeAsync();
            await old.AddAccountAsync(GameId.StarRail, "崩铁切换样例", "", null);
            using (var database = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = oldPath, Pooling = false }.ToString()))
            {
                database.Open();
                using var command = database.CreateCommand();
                command.CommandText = "DROP TABLE account_configuration_audit; PRAGMA user_version=6;";
                command.ExecuteNonQuery();
            }
            var imported = new LocalDatabase(importedPath);
            await imported.InitializeAsync();
            await imported.AddAccountAsync(GameId.Genshin, "原神切换样例", "", null);
        }
        await UnifiedProfilePreparation.PrepareAsync(oldPath, importedPath, Path.Combine(fixture, "vivian-smoke-prepared"),
            CancellationToken.None);
    }

    private static async Task<string> SmokeAssistantIsolationAsync(Window owner, MainViewModel vm, LocalDatabase database)
    {
        var assistant = vm.Assistant;
        if (assistant.StatusLine != "未配置")
            throw new InvalidOperationException("Corrupt assistant settings should leave AI unconfigured.");

        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        assistant.BaseUrl = $"http://127.0.0.1:{((System.Net.IPEndPoint)listener.LocalEndpoint).Port}/v1";
        assistant.Model = "smoke-only";
        assistant.NewApiKey = "smoke-only";
        if (assistant.StatusLine != "未测试") throw new InvalidOperationException("New settings were not marked untested.");

        using (var authDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            assistant.TestConnectionCommand.Execute(null);
            using var denied = await listener.AcceptTcpClientAsync(authDeadline.Token);
            await denied.GetStream().WriteAsync("HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), authDeadline.Token);
            while (assistant.IsBusy) await Task.Delay(20, authDeadline.Token);
            if (assistant.StatusLine != "测试失败" || !assistant.SettingsStatus.Contains("401", StringComparison.Ordinal))
                throw new InvalidOperationException("AI authentication failure was not isolated.");
        }

        var floating = new AssistantWindow(assistant) { Owner = owner };
        floating.Show();
        try
        {
            using var acceptDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            assistant.TestConnectionCommand.Execute(null);
            using var stalled = await listener.AcceptTcpClientAsync(acceptDeadline.Token);
            var queue = await SmokeQueueAsync(vm.Queue, database);
            if (!assistant.IsBusy) throw new InvalidOperationException("The AI request did not overlap the queue smoke test.");
            using var cancelDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            floating.Hide();
            while (assistant.IsBusy) await Task.Delay(20, cancelDeadline.Token);
            if (assistant.StatusLine != "未测试" || !assistant.SettingsStatus.Contains("取消", StringComparison.Ordinal))
                throw new InvalidOperationException("Hiding the assistant did not cancel its request.");
            return queue + " corrupt AI settings, 401, and closing a stalled AI request isolated.";
        }
        finally { floating.Close(); }
    }

    private static async Task<string> SmokeQueueAsync(QueueViewModel queue, LocalDatabase database)
    {
        queue.Step = TimeSpan.FromMilliseconds(60);
        var dispatcher = Current.Dispatcher;
        using var probeStop = new CancellationTokenSource();
        var probe = Task.Run(() =>
        {
            var worst = TimeSpan.Zero;
            while (!probeStop.IsCancellationRequested)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Input);
                if (clock.Elapsed > worst) worst = clock.Elapsed;
                Thread.Sleep(20);
            }
            return worst;
        });

        var plan = new[] { (GameId.Genshin, DemoScenario.Success), (GameId.StarRail, DemoScenario.Success), (GameId.ZenlessZoneZero, DemoScenario.Success) };
        var completed = await queue.RunDemoAsync(plan) ?? throw new InvalidOperationException("Queue did not start");
        if (completed.Status != QueueStatus.Completed || completed.Runs.Count != 3
            || completed.Runs.Any(r => r.Outcome != RunOutcome.Success || !r.IsSimulation))
            throw new InvalidOperationException("Simulation queue did not complete as expected");
        var stored = await database.ReadRecentRunsAsync(10);
        if (stored.Count(r => r.IsSimulation && r.Outcome == RunOutcome.Success) != 3)
            throw new InvalidOperationException("Simulation runs were not persisted");
        if (queue.Runs.Any(r => !r.Summary.StartsWith(RunPresentation.SimulationLabel, StringComparison.Ordinal)))
            throw new InvalidOperationException("A simulation result was displayed without the simulation label");

        var stalled = queue.RunDemoAsync([(GameId.Genshin, DemoScenario.Stall), (GameId.StarRail, DemoScenario.Success)]);
        while (!queue.LogLines.Any(l => l.Contains("模拟卡死", StringComparison.Ordinal))) await Task.Delay(20);
        var stopClock = System.Diagnostics.Stopwatch.StartNew();
        queue.EmergencyStop();
        var stopped = await stalled ?? throw new InvalidOperationException("Stop queue did not start");
        var stopLatency = stopClock.Elapsed;
        if (stopped.Status != QueueStatus.Cancelled || stopped.Runs.Single().Outcome != RunOutcome.Cancelled)
            throw new InvalidOperationException("Emergency stop did not cancel the queue");
        if (stopLatency > TimeSpan.FromSeconds(3)) throw new InvalidOperationException("Emergency stop too slow: " + stopLatency);

        // Shutdown coordination: closing while a queue runs stops it and waits for its final state.
        var closing = queue.RunDemoAsync([(GameId.Genshin, DemoScenario.Stall)]);
        while (!queue.LogLines.Any(l => l.Contains("模拟卡死", StringComparison.Ordinal))) await Task.Delay(20);
        if (!await queue.StopForShutdownAsync(TimeSpan.FromSeconds(10)))
            throw new InvalidOperationException("Shutdown stop did not end the queue");
        var closed = await closing ?? throw new InvalidOperationException("Shutdown queue did not start");
        if (closed.Status != QueueStatus.Cancelled) throw new InvalidOperationException("Shutdown stop did not cancel the queue");

        probeStop.Cancel();
        var worstUi = await probe;
        if (worstUi > TimeSpan.FromSeconds(1)) throw new InvalidOperationException("UI thread unresponsive: " + worstUi);
        return $"simulation queue 3/3 success persisted; emergency stop {stopLatency.TotalMilliseconds:F0} ms; shutdown stop ok; worst UI response {worstUi.TotalMilliseconds:F0} ms.";
    }

    /// <summary>
    /// Smoke check for M3: the read-only engine check shows understandable states for harmless fixture folders.
    /// The fixtures are plain text files named like the engine programs; nothing is executed.
    /// </summary>
    private static async Task<string> SmokeEngineDiscoveryAsync(MainViewModel vm, string root)
    {
        var fixtures = Path.Combine(root, "engine-fixtures");
        var bgi = Directory.CreateDirectory(Path.Combine(fixtures, "BetterGI")).FullName;
        await File.WriteAllTextAsync(Path.Combine(bgi, "BetterGI.exe"), "harmless fixture, never executed");
        foreach (var folder in VIvian.Infrastructure.Engines.BetterGi.BetterGiContract.RequiredFolders)
            Directory.CreateDirectory(Path.Combine(bgi, folder));
        var m7a = Directory.CreateDirectory(Path.Combine(fixtures, "March7thAssistant")).FullName;
        await File.WriteAllTextAsync(Path.Combine(m7a, "m7a.exe"), "harmless fixture, never executed");
        Directory.CreateDirectory(Path.Combine(m7a, "assets", "config"));
        await File.WriteAllTextAsync(Path.Combine(m7a, "assets", "config", "version.txt"), "v2026.10.31\n");
        vm.Engines.Single(e => e.Id == GameId.Genshin).InstallPath = bgi;
        vm.Engines.Single(e => e.Id == GameId.StarRail).InstallPath = m7a;
        vm.Engines.Single(e => e.Id == GameId.ZenlessZoneZero).InstallPath = "";
        await vm.CheckEnginesAsync();
        var states = vm.Engines.ToDictionary(e => e.Id, e => e.Compatibility);
        if (states[GameId.Genshin] != "版本未知" || states[GameId.StarRail] != "待桌面验证" || states[GameId.ZenlessZoneZero] != "未设置路径")
            throw new InvalidOperationException("Engine discovery states unexpected: " + string.Join(", ", states.Select(kv => kv.Key + "=" + kv.Value)));
        if (vm.Engines.Any(e => !e.Execution.Contains("未开放", StringComparison.Ordinal)))
            throw new InvalidOperationException("An engine card does not state that real execution is unavailable");
        if (vm.Engines.Any(e => e.Report is null || e.Report.RealExecutionAllowed))
            throw new InvalidOperationException("An engine report is missing or allows real execution");
        return "engine readiness: 原神=版本未知, 星铁=待桌面验证, 绝区零=未设置 (read-only fixtures).";
    }

    private static async Task<string> SmokeAccountWorkflowAsync(Window window, MainViewModel vm, LocalDatabase database, string root)
    {
        var tabs = FindTabControl(window) ?? throw new InvalidOperationException("TabControl not found");
        var headers = tabs.Items.OfType<System.Windows.Controls.TabItem>().Select(t => t.Header as string).ToArray();
        if (!headers.Contains("账号管理") || !headers.Contains("游戏管理") ||
            headers.Contains("日常任务") || headers.Contains("任务计划"))
            throw new InvalidOperationException("Account workflow pages do not match the home-start layout");

        if (RealExecutionPolicy.IsM5DailyTest)
        {
            await vm.OneClickAsync();
            if (!vm.BatchStatus.Contains("勾选", StringComparison.Ordinal) || vm.BatchRunning)
                throw new InvalidOperationException("Home start did not require selected accounts");
        }

        var row = vm.Accounts.ConfigurationRow ?? throw new InvalidOperationException("Missing Star Rail configuration account");
        row.DailyEnabled = false;
        await vm.Accounts.SaveDailyPlanAsync();
        if ((await database.LoadAccountTaskPlanAsync(row.Id)).DailyEnabled)
            throw new InvalidOperationException("Game task setting was not saved");
        if (window is not MainWindow main) throw new InvalidOperationException("Missing main window");
        var zenless = await database.AddAccountAsync(GameId.ZenlessZoneZero, "绝区零冒烟档案", "", null);
        await vm.Accounts.ReloadZenlessAsync();
        tabs.SelectedIndex = Array.IndexOf(headers, "账号管理");
        window.UpdateLayout();
        var gameHeaders = main.AccountTabs.Items.OfType<System.Windows.Controls.TabItem>().Select(t => t.Header as string);
        if (!gameHeaders.SequenceEqual(new[] { "崩坏：星穹铁道", "原神", "绝区零" })
            || vm.Accounts.Rows.Any(a => a.Account.Game != GameId.StarRail))
            throw new InvalidOperationException("Account tabs or Star Rail filtering are incorrect");
        main.AccountTabs.SelectedIndex = 1;
        window.UpdateLayout();
        if (!ReferenceEquals(main.GenshinAccountList.ItemsSource, vm.GenshinConfiguration.Accounts)
            || main.ImportGenshinButton.Command != vm.GenshinConfiguration.ImportCommand
            || main.BindGenshinLoginButton.Command != vm.GenshinConfiguration.BindLoginScreenshotCommand
            || vm.GenshinConfiguration.Accounts.Count == 0
            || vm.GenshinConfiguration.Accounts.Any(a => a.Game != GameId.Genshin))
            throw new InvalidOperationException("The Genshin account tab is not bound to Genshin accounts");
        main.AccountTabs.SelectedIndex = 2;
        window.UpdateLayout();
        if (!ReferenceEquals(main.ZenlessAccountList.ItemsSource, vm.Accounts.ZenlessRows)
            || vm.Accounts.ZenlessRows.All(a => a.Id != zenless.Id)
            || vm.Accounts.ZenlessRows.Any(a => a.Game != GameId.ZenlessZoneZero))
            throw new InvalidOperationException("The Zenless account tab is not bound to Zenless accounts");
        main.AccountTabs.SelectedIndex = 0;
        tabs.SelectedIndex = Array.IndexOf(headers, "游戏管理");
        main.GameTabs.SelectedIndex = 0;
        window.UpdateLayout();
        var starRailSettingsWidth = ((main.GameTabs.SelectedItem as System.Windows.Controls.TabItem)?.Content
            as System.Windows.Controls.Border)?.ActualWidth ?? 0;
        main.GameTabs.SelectedIndex = 1;
        window.UpdateLayout();
        var genshinSettings = (main.GameTabs.SelectedItem as System.Windows.Controls.TabItem)?.Content as System.Windows.Controls.Border;
        if (starRailSettingsWidth <= 0 || genshinSettings is null ||
            Math.Abs(starRailSettingsWidth - genshinSettings.ActualWidth) > 1)
            throw new InvalidOperationException("Star Rail and Genshin must use the same full-width configuration panel");
        var genshin = vm.GenshinConfiguration;
        if (genshin.SelectedAccount is not { Enabled: true, LoginState: "recognized" })
            throw new InvalidOperationException("The first selected Genshin account must be the enabled recognized fixture");
        var sections = main.GenshinSettingsTabs.Items.OfType<System.Windows.Controls.TabItem>()
            .Select(item => item.Header as string);
        if (!sections.SequenceEqual(new[] { "任务设置", "登录验收" }))
            throw new InvalidOperationException("Genshin settings must have one task editor and a separate login check");
        if (main.SaveGenshinPlanButton.Command != genshin.SaveCommand)
            throw new InvalidOperationException("Genshin plan editor is not bound");
        var recognized = genshin.SelectedAccount;
        genshin.NewAccountName = "原神冒烟档案";
        await genshin.AddAccountAsync();
        var added = genshin.SelectedAccount ?? throw new InvalidOperationException("Genshin account creation failed");
        if (added.Id == recognized.Id || genshin.Parameters.Any(p => p.IsBoolean && p.BoolValue))
            throw new InvalidOperationException("New Genshin profile is not isolated or tasks default to on");
        genshin.Parameters.Single(p => p.Path == "dailyRewards").BoolValue = true;
        await genshin.SaveAsync();
        var saved = await database.LoadAccountSettingsAsync(added.Id);
        var untouched = await database.LoadAccountSettingsAsync(recognized.Id);
        if (saved.Values.GetValueOrDefault("dailyRewards") != "true" || untouched.Values.Count != 0
            || (await database.LoadAccountTaskPlanAsync(row.Id)).DailyEnabled)
            throw new InvalidOperationException("Genshin plan save did not isolate Genshin and Star Rail accounts");
        window.UpdateLayout();
        if (main.ConnectBetterGiButton.Command != genshin.ConnectSourceCommand ||
            main.OpenBetterGiButton.Command != genshin.Source.OpenCommand ||
            main.ConnectBetterGiButton.IsEnabled || main.OpenBetterGiButton.IsEnabled ||
            main.GenshinPlanFields.Items.Count != genshin.Parameters.Count)
            throw new InvalidOperationException("Unified Genshin editor failed to bind or enabled BetterGI without a verified installation");
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(7)))
        {
            while (!genshin.Source.Status.Contains("保存 BetterGI 安装目录", StringComparison.Ordinal))
                await Task.Delay(25, deadline.Token);
            var card = vm.Engines.Single(e => e.Id == GameId.Genshin);
            var oldPath = card.SavedInstallPath;
            try
            {
                card.SavedInstallPath = card.InstallPath;
                while (!genshin.Source.Status.Contains(BetterGiContract.PinnedTag, StringComparison.Ordinal))
                    await Task.Delay(25, deadline.Token);
                if (main.ConnectBetterGiButton.IsEnabled)
                    throw new InvalidOperationException("The unverified BetterGI fixture can create an account profile");
            }
            finally { card.SavedInstallPath = oldPath; }
            var finishSourceWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sourceWrite = vm.SerializeGenshinOperationAsync(() => finishSourceWrite.Task);
            try
            {
                vm.SaveCommand.Execute(null);
                var storedPath = (await database.LoadSettingsAsync()).Single(setting => setting.Game == GameId.Genshin).InstallPath;
                if (storedPath != oldPath || card.SavedInstallPath != oldPath)
                    throw new InvalidOperationException("Saving engine settings changed the root during a Genshin source write");
            }
            finally
            {
                finishSourceWrite.SetResult();
                await sourceWrite;
            }
        }
        if (RealExecutionPolicy.IsUnifiedInstall && !genshin.IsLoginVerificationAvailable)
            throw new InvalidOperationException("The unified app must expose Genshin login verification");
        if (genshin.IsLoginVerificationAvailable)
        {
            main.GenshinSettingsTabs.SelectedIndex = 1;
            window.UpdateLayout();
            if (main.VerifyGenshinLoginButton.Command != genshin.VerifyLoginCommand ||
                !Equals(main.VerifyGenshinLoginButton.Content, "启动并验收登录"))
                throw new InvalidOperationException("Genshin login verification must bind directly without a countdown");
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var login = vm.SerializeGenshinOperationAsync(() => genshin.RunLoginOperationAsync(async token =>
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    cancelled.SetResult();
                    throw;
                }
            }));
            try
            {
                vm.Configuration.OpenUpstreamCommand.Execute(null);
                if (!vm.Configuration.Status.Contains("任务运行中", StringComparison.Ordinal))
                    throw new InvalidOperationException("The Star Rail launcher was not blocked during simulated Genshin login");
                if (!genshin.IsVerifyingLogin || !vm.CanStopAll ||
                    !await vm.WaitForDesktopOperationShutdownAsync(TimeSpan.FromSeconds(5)) ||
                    !cancelled.Task.IsCompletedSuccessfully || vm.CanStopAll)
                    throw new InvalidOperationException("The common stop and shutdown path did not cancel the simulated Genshin login");
            }
            finally
            {
                genshin.CancelLogin();
                await login;
            }
        }
        CaptureAccountScreens(main, Array.IndexOf(headers, "账号管理"), Array.IndexOf(headers, "游戏管理"), root);
        main.GenshinSettingsTabs.SelectedIndex = 0;
        main.GameTabs.SelectedIndex = 0;

        await vm.ExportDiagnosticsAsync();
        if (vm.LastExportPath is null || !File.Exists(vm.LastExportPath))
            throw new InvalidOperationException("Diagnostic bundle was not exported: " + vm.ExportStatus);
        var loginReport = genshin.IsLoginVerificationAvailable ? " direct Genshin login entry and simulated stop verified;" : "";
        return "three game account tabs isolated; unified Genshin editor picked the recognized account and checked late BetterGI discovery;" +
            loginReport + " Star Rail task setting and isolated Genshin plan saved; diagnostic bundle exported.";
    }

    private static void CaptureAccountScreens(MainWindow window, int accounts, int games, string root)
    {
        var size = (window.Width, window.Height, window.MinWidth, window.MinHeight);
        try
        {
            window.MinWidth = Math.Min(window.MinWidth, 1024);
            window.MinHeight = Math.Min(window.MinHeight, 768);
            window.Width = 1024;
            window.Height = 768;
            window.Navigation.SelectedIndex = accounts;
            window.AccountTabs.SelectedIndex = 1;
            SaveScreen(window, root, "accounts-genshin-1024x768.png");
            window.Navigation.SelectedIndex = games;
            window.GameTabs.SelectedIndex = 1;
            window.GenshinSettingsTabs.SelectedIndex = 0;
            SaveScreen(window, root, "settings-bettergi-1024x768.png");
        }
        finally
        {
            window.AccountTabs.SelectedIndex = 0;
            (window.Width, window.Height, window.MinWidth, window.MinHeight) = size;
        }
    }
    /// <summary>Saves a PNG of every tab into the smoke evidence folder, for human visual review of CI builds.</summary>
    private static string SaveTabScreenshots(Window window, string root)
    {
        var tabs = FindTabControl(window) ?? throw new InvalidOperationException("TabControl not found");
        var failures = new List<string>();
        int checkedTotal = 0, skippedTotal = 0;
        for (var i = 0; i < tabs.Items.Count; i++)
        {
            tabs.SelectedIndex = i;
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            var (found, checkedCount, skipped) = ContrastAudit.Check(window);
            checkedTotal += checkedCount;
            skippedTotal += skipped;
            failures.AddRange(found.Select(f => $"tab {i}: \"{f.Text}\" {f.Ratio:F1}:1 ({f.Foreground} on {f.Background})"));
            SaveScreen(window, root, $"screen-{i}.png");
        }
        tabs.SelectedIndex = 0;
        if (failures.Count > 0)
        {
            File.WriteAllLines(Path.Combine(root, "contrast-failures.txt"), failures.Distinct());
            throw new InvalidOperationException("Text below WCAG AA contrast: " + string.Join(" | ", failures.Distinct().Take(8)));
        }
        return $"contrast ok for {checkedTotal} text elements ({skippedTotal} over non-solid backgrounds not checked).";
    }

    private static void SaveScreen(Window window, string root, string name)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        var content = (FrameworkElement)window.Content;
        var width = content.ActualWidth + content.Margin.Left + content.Margin.Right;
        var height = content.ActualHeight + content.Margin.Top + content.Margin.Bottom;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var background = new System.Windows.Media.DrawingVisual();
        using (var dc = background.RenderOpen())
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        bitmap.Render(background);
        bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(root, name));
        encoder.Save(file);
    }

    private static System.Windows.Controls.TabControl? FindTabControl(DependencyObject node)
    {
        if (node is System.Windows.Controls.TabControl tabs) return tabs;
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            if (FindTabControl(child) is { } found) return found;
        return null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        Log.CloseAndFlush();
        _activation?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
