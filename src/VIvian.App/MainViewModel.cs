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
    private string _compatibility = "尚未检查";
    /// <summary>Short state shown on the card: 已验证 / 未验证 / 不支持 / 未安装 / 无法访问.</summary>
    public string Compatibility { get => _compatibility; set => Set(ref _compatibility, value); }
    private string _evidence = "保存安装目录后，VIvian 会只读检查引擎文件。";
    public string Evidence { get => _evidence; set => Set(ref _evidence, value); }
    private string _stateBrush = StateColors.Neutral;
    /// <summary>Text colour of the state (hex), chosen together with the state text.</summary>
    public string StateBrush { get => _stateBrush; set => Set(ref _stateBrush, value); }

    /// <summary>Real task execution is not available for any engine in this build.</summary>
    public string Execution => Id == GameId.StarRail
        ? "真实任务执行：未开放（只做只读检查和官方发行包核对）"
        : "真实任务执行：未开放（该引擎尚未接入）";

    public void Show(string state, string brush, string detail)
    {
        Compatibility = state; StateBrush = brush; Evidence = detail;
    }
}

public static class StateColors
{
    public const string Verified = "#7FD49B";
    public const string Warning = "#E5BF76";
    public const string Error = "#F09A8E";
    public const string Neutral = "#A9B6CB";
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
    public AsyncCommand CheckEnginesCommand { get; }
    public AsyncCommand VerifyReleaseCommand { get; }
    private string _releaseArchivePath = "";
    /// <summary>A March7thAssistant release archive the user downloaded from the official GitHub release.</summary>
    public string ReleaseArchivePath { get => _releaseArchivePath; set => Set(ref _releaseArchivePath, value); }
    private string _releaseStatus = "尚未核对官方发行包。";
    public string ReleaseStatus { get => _releaseStatus; private set => Set(ref _releaseStatus, value); }
    // Session only: a verified official archive's m7a.exe hash. Not stored; verify again after restarting.
    private March7thReleaseEvidence? _march7thEvidence;
    public QueueViewModel Queue { get; }
    public MainViewModel(ISettingsStore settings, IDiagnosticStore history, QueueViewModel queue)
    {
        _settings = settings; _history = history; Queue = queue;
        SaveCommand = new(() => RunAsync(SaveAsync), HandleError);
        ReloadCommand = new(() => RunAsync(async () => { await InitializeAsync(); Status = "已重新加载保存的配置与记录。"; }), HandleError);
        CheckCommand = new(CheckFoundationAsync, HandleError);
        InspectMarch7thCommand = new(() => RunAsync(InspectMarch7thAsync), HandleError);
        CheckEnginesCommand = new(() => RunAsync(CheckEnginesCoreAsync), HandleError);
        VerifyReleaseCommand = new(() => RunAsync(VerifyReleaseAsync), HandleError);
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
        await CheckEnginesCoreAsync();
    }

    /// <summary>Read-only check of all three engine folders. Never runs an engine or touches its configuration.</summary>
    public Task CheckEnginesAsync() => RunAsync(CheckEnginesCoreAsync);

    private async Task CheckEnginesCoreAsync()
    {
        foreach (var card in Engines)
        {
            if (card.Id == GameId.StarRail) await InspectMarch7thAsync();
            else await DiscoverAsync(card);
        }
        Status = "已只读检查三个引擎目录；没有运行任何程序，也没有修改任何文件。";
    }

    private static async Task DiscoverAsync(EngineCard card)
    {
        var path = card.InstallPath.Trim();
        if (path.Length == 0) { card.Show("未设置路径", StateColors.Neutral, "在“引擎设置”中填写安装目录。"); return; }
        var found = await Task.Run(() => EngineFileDiscovery.InspectAsync(card.Id, path));
        switch (found.State)
        {
            case DiscoveryState.Found:
                card.Show("不支持（尚未接入）", StateColors.Warning, found.Detail + " VIvian 还没有这个引擎的适配器，只显示检查结果。");
                break;
            case DiscoveryState.NotInstalled:
                card.Show("未安装", StateColors.Neutral, found.Detail);
                break;
            default:
                card.Show("无法访问", StateColors.Error, found.Detail);
                break;
        }
    }

    private async Task VerifyReleaseAsync()
    {
        var path = ReleaseArchivePath.Trim().Trim('"');
        ReleaseStatus = "正在计算 SHA-256（大文件需要一些时间）…";
        var result = await Task.Run(() => March7thReleaseVerifier.VerifyAsync(path));
        if (result.Check == ReleaseCheck.Verified) _march7thEvidence = result.Evidence;
        ReleaseStatus = result.Check switch
        {
            ReleaseCheck.Verified => "已验证：" + result.Detail,
            ReleaseCheck.OfficialContentsUnavailable => "是官方文件，但无法用于核对安装：" + result.Detail,
            ReleaseCheck.NotOfficial => "不是官方发行包：" + result.Detail,
            ReleaseCheck.UnexpectedLayout => "内容与官方发行版不符：" + result.Detail,
            _ => "无法核对：" + result.Detail,
        };
        Log.Information("Release archive check: {Check}", result.Check);
        await InspectMarch7thAsync();
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
        var path = card.InstallPath.Trim();
        if (path.Length == 0) { card.Show("未设置路径", StateColors.Neutral, "在“引擎设置”中填写安装目录。"); return; }
        var evidence = _march7thEvidence;
        // Off the UI thread: existence checks on a network path or hashing a large file must not freeze the window.
        var result = await Task.Run(() => March7thReadOnlyInspector.InspectAsync(path, evidence));
        var (state, brush) = result.Compatibility switch
        {
            March7thCompatibility.Verified => ("已验证（官方发行包）", StateColors.Verified),
            March7thCompatibility.Unverified => ("未验证", StateColors.Warning),
            March7thCompatibility.UnsupportedVersion => ("不支持（版本）", StateColors.Warning),
            March7thCompatibility.MissingCli => ("不支持（缺少 m7a.exe）", StateColors.Warning),
            March7thCompatibility.NotInstalled => ("未安装", StateColors.Neutral),
            _ => ("无法访问", StateColors.Error),
        };
        card.Show(state, brush, result.Detail);
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
