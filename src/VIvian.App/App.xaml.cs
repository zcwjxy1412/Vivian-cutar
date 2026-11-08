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

namespace VIvian.App;

public partial class App : Application
{
    private IHost? _host;
    private SingleInstanceGuard? _instance;
    private WindowActivation? _activation;
    private bool _smoke;
    private bool _startupCheck;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "vivian-host-boot.log"), $"{DateTimeOffset.Now:O} OnStartup args={string.Join('|', e.Args)}\n"); } catch { }
        // The elevated input host is a hidden second instance of this same executable. It must be handled before
        // single-instance, database, logging or any window exists, and it owns no user data.
        var hostIndex = Array.IndexOf(e.Args, InputHostProtocol.Argument);
        if (hostIndex >= 0)
        {
            var pipeName = hostIndex + 1 < e.Args.Length ? e.Args[hostIndex + 1] : string.Empty;
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "vivian-host-boot.log"), $"{DateTimeOffset.Now:O} entering host pipe={pipeName}\n"); } catch { }
            var exitCode = InputHostServer.Run(pipeName);
            Shutdown(exitCode);
            return;
        }
        _smoke = e.Args.Contains("--smoke-test");
        _startupCheck = e.Args.Contains("--startup-check");
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Fatal("Unhandled UI error: {ErrorType}; {Stack}", args.Exception.GetType().Name,
                Redactor.ForCurrentUser().Redact(args.Exception.ToString()));
            args.Handled = true;
            if (!_smoke) MessageBox.Show("程序遇到未处理错误，将退出。请查看本地日志。", "VIvian");
            Shutdown(1);
        };
        try
        {
            // Smoke tests use a disposable directory, never the user's profile.
            var screenTest = File.Exists(Path.Combine(AppContext.BaseDirectory, "VIvian-M4A-ScreenTest.marker"));
            var manualTest = File.Exists(Path.Combine(AppContext.BaseDirectory, "VIvian-M4-F1-ManualTest.marker"));
            var m5Daily = File.Exists(Path.Combine(AppContext.BaseDirectory, "VIvian-M5-DailyTest.marker"));
            // Each real-execution build must carry its own marker (and only its own): its data stays isolated.
            if (RealExecutionPolicy.IsOneShotScreenTest && (!screenTest || manualTest))
                throw new InvalidOperationException("单次测试包缺少隔离数据标记，拒绝启动。");
            if (RealExecutionPolicy.IsReusableManualTest && !RealExecutionPolicy.IsM5DailyTest && (!manualTest || screenTest || m5Daily))
                throw new InvalidOperationException("手动实测版缺少隔离数据标记，拒绝启动。");
            if (RealExecutionPolicy.IsM5DailyTest && (!m5Daily || manualTest || screenTest))
                throw new InvalidOperationException("M5 每日实训包缺少独立标记，拒绝启动。");
            if (!RealExecutionPolicy.BuildPermitsRealExecution && manualTest)
                throw new InvalidOperationException("普通构建不能使用手动实测版的数据目录。");
            var threeEnginePreview = File.Exists(Path.Combine(AppContext.BaseDirectory, "VIvian-M4-ThreeEngine-Preview.marker"));
            if (threeEnginePreview && RealExecutionPolicy.BuildPermitsRealExecution)
                throw new InvalidOperationException("三引擎预览包不允许真实执行许可。");
            var preview = e.Args.Contains("--m4-preview")
                || File.Exists(Path.Combine(AppContext.BaseDirectory, "VIvian-M4A-Preview.marker"));
            var root = _smoke
                ? Environment.GetEnvironmentVariable("VIVIAN_SMOKE_DATA") ?? throw new InvalidOperationException("Missing smoke directory")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    RealExecutionPolicy.IsM5DailyTest ? "VIvian-M5-DailyTest"
                    : RealExecutionPolicy.IsReusableManualTest ? "VIvian-M4-ManualTest"
                    : screenTest ? "VIvian-M4A-ScreenTest-R3" : threeEnginePreview ? "VIvian-M4-ThreeEngine-Preview"
                    : preview ? "VIvian-M4A-Preview" : "VIvian");
            Directory.CreateDirectory(root);
            // Smoke runs must never write into the user's screenshot-feedback folder.
            if (_smoke) Environment.SetEnvironmentVariable("VIVIAN_FEEDBACK_DIR", Path.Combine(root, "no-feedback-folder"));
            // One instance per data directory, decided before the database or logs are touched.
            _instance = SingleInstanceGuard.Acquire(root);
            if (!_instance.IsOwner)
            {
                // A second launch brings the open window to the front instead of only saying it is open.
                if (!_smoke && _instance.Result != InstanceLockResult.AccessDenied && WindowActivation.SignalExisting(root))
                {
                    Shutdown(2);
                    return;
                }
                if (!_smoke)
                    MessageBox.Show(_instance.Result == InstanceLockResult.AccessDenied
                        ? "检测到另一个 VIvian 实例（可能以其他用户或管理员身份运行），无法确认它的状态。为避免冲突，本窗口不会启动。"
                        : "VIvian 已经在运行，请切换到已打开的窗口。", "VIvian");
                Shutdown(_instance.Result == InstanceLockResult.AccessDenied ? 3 : 2);
                return;
            }
            Log.Logger = new LoggerConfiguration().MinimumLevel.Information()
                .WriteTo.File(Path.Combine(root, "logs", "vivian-.log"), rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14, fileSizeLimitBytes: 5_000_000, rollOnFileSizeLimit: true).CreateLogger();
            if (_instance.Result == InstanceLockResult.AcquiredAfterAbandoned)
                Log.Warning("Previous instance exited without releasing the instance lock");
            var database = new LocalDatabase(Path.Combine(root, "vivian.db"));
            _host = Host.CreateDefaultBuilder().UseSerilog().ConfigureServices(services =>
            {
                services.AddSingleton(database);
                services.AddSingleton<ISettingsStore>(database);
                services.AddSingleton<IDiagnosticStore>(database);
                services.AddSingleton<IRunJournal>(database);
                services.AddSingleton<IRunHistory>(database);
                services.AddSingleton<ITaskPlanStore>(database);
                services.AddSingleton<IPreferenceStore>(database);
                services.AddSingleton(new AppPaths(root));
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
                        RealExecutionPolicy.BuildPermitsRealExecution && adapter is VIvian.Infrastructure.Engines.March7thAdapter));
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
                    sp.GetRequiredService<IInputHost>(), sp.GetRequiredService<GamePathProvider>().Get));
                services.AddSingleton(sp => new MultiAccountOrchestrator(sp.GetRequiredService<LocalDatabase>(),
                    sp.GetRequiredService<ILoginSwitchDriver>(), sp.GetRequiredService<IInputHostSession>()));
                services.AddSingleton<IScanWindowShield, MainWindowScanShield>();
                services.AddSingleton<AccountViewModel>();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
            }).Build();
            await _host.StartAsync();
            await Task.Run(() => database.InitializeAsync());
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
            var vm = _host.Services.GetRequiredService<MainViewModel>();
            await vm.InitializeAsync(autoPrepare: !_smoke);
            vm.ReportReconciled(reconciled.Count);
            if (_startupCheck)
            {
                await File.WriteAllTextAsync(Path.Combine(root, "startup.ok"),
                    $"{DateTimeOffset.UtcNow:O} {AppPaths.Version} {AppPaths.Commit} auto discovery and diagnostics completed");
                Shutdown(0);
                return;
            }
            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
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
                        var queueReport = await SmokeQueueAsync(vm.Queue, database);
                        var engineReport = await SmokeEngineDiscoveryAsync(vm, root);
                        var tasksReport = await SmokeTasksAndDiagnosticsAsync(vm, database);
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
            window.Show();
            if (!_smoke) _activation = WindowActivation.Listen(root, window);
            // M4-F1 defect B: say what this build actually allows instead of a fixed "not enabled" line.
            Log.Information("VIvian {Version} ({Commit}) started. {Mode}", AppPaths.Version, AppPaths.Commit,
                RealExecutionPolicy.IsM5DailyTest
                    ? "M5 daily manual-test build: per-run consent, pinned identity and cleanup admission."
                    : RealExecutionPolicy.IsReusableManualTest
                    ? "Manual-test build: March7thAssistant screen_test only, each run confirmed by the user; no scheduled or unattended real runs."
                    : RealExecutionPolicy.BuildPermitsRealExecution
                    ? "One-attempt screen_test package: a single user-confirmed March7thAssistant screen_test."
                    : "Real engines are not enabled in this build.");
        }
        catch (Exception ex)
        {
            Log.Error("Startup failed: {ErrorType}; {Stack}", ex.GetType().Name,
                Redactor.ForCurrentUser().Redact(ex.ToString()));
            if (!_smoke) MessageBox.Show("启动失败。请检查数据目录权限、磁盘空间及数据库版本；原数据不会自动删除。", "VIvian");
            Shutdown(1);
        }
    }
    /// <summary>
    /// Smoke check for M2: runs a simulation queue through the real view model, scheduler and SQLite journal while a
    /// background probe measures how long the UI thread takes to answer; then checks the emergency stop.
    /// </summary>
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

    /// <summary>Smoke check: task plans persist and stay non-executable; a redacted diagnostic bundle is exported.</summary>
    private static async Task<string> SmokeTasksAndDiagnosticsAsync(MainViewModel vm, LocalDatabase database)
    {
        var plan = vm.TaskPlans.Single(p => p.Game == GameId.StarRail);
        vm.SelectedPlan = plan;
        plan.Rows[1].Enabled = true;
        plan.Selected = plan.Rows[1];
        plan.Move(-1);
        plan.Selected = null; // no list selection highlight in the screenshots
        if (!plan.Validation.StartsWith("不能执行", StringComparison.Ordinal))
            throw new InvalidOperationException("A selected candidate task was shown as executable");
        await vm.SaveTasksCommandAsync();
        var stored = (await database.LoadTaskPlansAsync()).Single(p => p.Game == GameId.StarRail);
        if (stored.Revision != 1 || !stored.Entries[0].Enabled || stored.Entries[0].TaskId != EngineCatalog.March7th.Tasks[1].Id)
            throw new InvalidOperationException("Task plan was not persisted in order");
        await vm.ExportDiagnosticsAsync();
        if (vm.LastExportPath is null || !File.Exists(vm.LastExportPath))
            throw new InvalidOperationException("Diagnostic bundle was not exported: " + vm.ExportStatus);
        return "task plan persisted (non-executable); diagnostic bundle exported.";
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
            // Render the whole client area (content plus its margin), not just the content's own size.
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
            using var file = File.Create(Path.Combine(root, $"screen-{i}.png"));
            encoder.Save(file);
        }
        tabs.SelectedIndex = 0;
        if (failures.Count > 0)
        {
            File.WriteAllLines(Path.Combine(root, "contrast-failures.txt"), failures.Distinct());
            throw new InvalidOperationException("Text below WCAG AA contrast: " + string.Join(" | ", failures.Distinct().Take(8)));
        }
        return $"contrast ok for {checkedTotal} text elements ({skippedTotal} over non-solid backgrounds not checked).";
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
