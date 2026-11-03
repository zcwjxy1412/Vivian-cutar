using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using VIvian.Core;
using VIvian.Infrastructure;

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
                        await File.WriteAllTextAsync(Path.Combine(root, "smoke.ok"),
                            "WPF content rendered; SQLite round-trip passed; second instance refused.");
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
    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        Log.CloseAndFlush();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
