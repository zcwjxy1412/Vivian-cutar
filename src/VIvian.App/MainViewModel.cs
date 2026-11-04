using System.Collections.ObjectModel;
using Serilog;
using VIvian.Core;
using VIvian.Infrastructure.Engines;
namespace VIvian.App;

public sealed class EngineCard(GameId id, string name, string engine, string glyph) : ObservableObject
{
    public GameId Id { get; } = id;
    public string Name { get; } = name;
    public string Engine { get; } = engine;
    public string Glyph { get; } = glyph;
    public long Revision { get; set; }
    private string _installPath = "";
    public string InstallPath { get => _installPath; set => Set(ref _installPath, value); }
    private string _compatibility = id == GameId.StarRail ? "尚未检查" : "尚未接入";
    public string Compatibility { get => _compatibility; set => Set(ref _compatibility, value); }
    private string _evidence = "";
    public string Evidence { get => _evidence; set => Set(ref _evidence, value); }
}
public sealed class MainViewModel : ObservableObject
{
    private readonly ISettingsStore _settings;
    private readonly IDiagnosticStore _history;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private string _status = "就绪。真实引擎尚未接入，可在“模拟队列”中体验调度流程。";
    public string Status { get => _status; private set => Set(ref _status, value); }
    private bool _busy;
    public bool IsIdle => !_busy;
    private void Busy(bool value) { _busy = value; Raise(nameof(IsIdle)); }
    public bool LastCheckPassed { get; private set; }
    public ObservableCollection<EngineCard> Engines { get; } =
    [new(GameId.Genshin, "原神", "BetterGI", "01"), new(GameId.StarRail, "崩坏：星穹铁道", "March7thAssistant", "02"), new(GameId.ZenlessZoneZero, "绝区零", "OneDragon", "03")];
    public ObservableCollection<DiagnosticRecord> History { get; } = [];
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand ReloadCommand { get; }
    public AsyncCommand CheckCommand { get; }
    public AsyncCommand InspectMarch7thCommand { get; }
    public QueueViewModel Queue { get; }
    public MainViewModel(ISettingsStore settings, IDiagnosticStore history, QueueViewModel queue)
    {
        _settings = settings; _history = history; Queue = queue;
        SaveCommand = new(() => RunAsync(SaveAsync), HandleError);
        ReloadCommand = new(() => RunAsync(async () => { await InitializeAsync(); Status = "已重新加载保存的配置与记录。"; }), HandleError);
        CheckCommand = new(CheckFoundationAsync, HandleError);
        InspectMarch7thCommand = new(() => RunAsync(InspectMarch7thAsync), HandleError);
    }
    public async Task InitializeAsync()
    {
        var settings = await Task.Run(() => _settings.LoadSettingsAsync());
        foreach (var value in settings)
        {
            var card = Engines.Single(x => x.Id == value.Game);
            card.InstallPath = value.InstallPath; card.Revision = value.Revision;
        }
        await RefreshHistoryAsync();
        await Queue.InitializeAsync();
    }
    public void ReportReconciled(int count)
    {
        if (count > 0) Status = $"上次 VIvian 意外退出，{count} 条运行已标记为结果未知，请在“模拟队列”中查看并确认。";
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
        var snapshot = Engines.Select(x => new EngineSetting(x.Id, x.InstallPath.Trim(), x.Revision)).ToList();
        await Task.Run(() => _settings.SaveSettingsAsync(snapshot));
        await InitializeAsync();
        Status = "路径已保存到 VIvian；未校验引擎兼容性，也未改动上游配置。";
        Log.Information("Local settings saved for {Count} games", snapshot.Count);
    }
    private async Task RefreshHistoryAsync()
    {
        var items = await Task.Run(() => _history.ReadRecentAsync(50));
        History.Clear(); foreach (var item in items) History.Add(item);
    }
    private async Task InspectMarch7thAsync()
    {
        var card = Engines.Single(x => x.Id == GameId.StarRail);
        var result = await March7thReadOnlyInspector.InspectAsync(card.InstallPath.Trim());
        card.Compatibility = result.Compatibility switch
        {
            March7thCompatibility.NotInstalled => "未安装 / 路径无效",
            March7thCompatibility.MissingCli => "缺少 CLI，暂不支持",
            _ => "发行包未验证，暂不支持查询",
        };
        card.Evidence = result.Sha256 is null ? result.Detail
            : $"文件版本：{result.FileVersion ?? "未知"} · CLI SHA-256：{result.Sha256}。{result.Detail}";
        Status = "星铁只读检查完成；未运行上游程序或游戏。";
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
    private void HandleError(Exception ex)
    {
        Status = ex is SettingsConflictException ? ex.Message : ex is ArgumentException ? "配置无效：路径过长或包含控制字符。" : "操作失败。请检查数据目录权限与磁盘空间，或查看日志。";
        Log.Warning("Operation failed: {ErrorType}", ex.GetType().Name);
    }
}
