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
            Log.Logger = new LoggerConfiguration().MinimumLevel.Information()
                .WriteTo.File(Path.Combine(root, "logs", "vivian-.log"), rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14, fileSizeLimitBytes: 5_000_000, rollOnFileSizeLimit: true).CreateLogger();
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
                        await File.WriteAllTextAsync(Path.Combine(root, "smoke.ok"), "WPF content rendered; SQLite round-trip passed.");
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
        base.OnExit(e);
    }
}
