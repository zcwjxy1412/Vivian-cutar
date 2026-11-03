using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using VIvian.Core;
using VIvian.Core.Execution;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Simulation;

namespace VIvian.App;

public partial class App : Application
{
    private IHost? _host;
    private SingleInstanceGuard? _instance;
    private bool _smoke;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _smoke = e.Args.Contains("--smoke-test");
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Fatal("Unhandled UI error: {ErrorType}", args.Exception.GetType().Name);
            args.Handled = true;
            if (!_smoke) MessageBox.Show("程序遇到未处理错误，将退出。请查看本地日志。", "VIvian");
            Shutdown(1);
        };
        try
        {
            // Smoke tests use a disposable directory, never the user's profile.
            var root = _smoke
                ? Environment.GetEnvironmentVariable("VIVIAN_SMOKE_DATA") ?? throw new InvalidOperationException("Missing smoke directory")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VIvian");
            Directory.CreateDirectory(root);
            // One instance per data directory, decided before the database or logs are touched.
            _instance = SingleInstanceGuard.Acquire(root);
            if (!_instance.IsOwner)
            {
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
                // M2 simulation mode: the only supervisor available refuses to start processes.
                services.AddSingleton<IProcessSupervisor, RefusingProcessSupervisor>();
                services.AddSingleton(sp => new QueueScheduler(sp.GetRequiredService<IRunJournal>(), sp.GetRequiredService<IProcessSupervisor>()));
                services.AddSingleton<QueueViewModel>();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
            }).Build();
            await _host.StartAsync();
            await Task.Run(() => database.InitializeAsync());
            // Safe only while holding the instance lock: no other VIvian can have a run in progress.
            var reconciled = await Task.Run(() => database.ReconcileInterruptedRunsAsync(DateTimeOffset.UtcNow));
            if (reconciled.Count > 0)
                Log.Warning("Reconciled {Count} runs interrupted by an unexpected exit", reconciled.Count);
            var vm = _host.Services.GetRequiredService<MainViewModel>();
            await vm.InitializeAsync();
            vm.ReportReconciled(reconciled.Count);
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
                        using (var second = SingleInstanceGuard.Acquire(root))
                            if (second.IsOwner) throw new InvalidOperationException("Second instance was not refused");
                        var queueReport = await SmokeQueueAsync(vm.Queue, database);
                        var contrastReport = SaveTabScreenshots(window, root);
                        await File.WriteAllTextAsync(Path.Combine(root, "smoke.ok"),
                            "WPF content rendered; SQLite round-trip passed; second instance refused; " + queueReport + " " + contrastReport);
                        Shutdown(0);
                    }
                    catch (Exception ex) { Log.Error("Smoke failed: {ErrorType}", ex.GetType().Name); Shutdown(1); }
                };
            }
            window.Show();
            Log.Information("M1 application started. Real engines are not enabled.");
        }
        catch (Exception ex)
        {
            Log.Error("Startup failed: {ErrorType}", ex.GetType().Name);
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

        probeStop.Cancel();
        var worstUi = await probe;
        if (worstUi > TimeSpan.FromSeconds(1)) throw new InvalidOperationException("UI thread unresponsive: " + worstUi);
        return $"simulation queue 3/3 success persisted; emergency stop {stopLatency.TotalMilliseconds:F0} ms; worst UI response {worstUi.TotalMilliseconds:F0} ms.";
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
        _instance?.Dispose();
        base.OnExit(e);
    }
}
