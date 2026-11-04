using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using VIvian.Core;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Simulation;

namespace VIvian.App;

public sealed record ScenarioOption(DemoScenario Value, string Label);

public sealed class DemoGameOption(GameId game) : ObservableObject
{
    public static IReadOnlyList<ScenarioOption> Scenarios { get; } =
    [
        new(DemoScenario.Success, "完成并给出证据"),
        new(DemoScenario.NoEvidence, "结束但没有证据"),
        new(DemoScenario.Failure, "报告失败"),
        new(DemoScenario.Stall, "卡死（长时间无输出）"),
        new(DemoScenario.CleanupUnconfirmed, "完成但无法确认清理"),
    ];

    public GameId Game { get; } = game;
    public string Name => RunPresentation.GameName(Game);
    private bool _include = true;
    public bool Include { get => _include; set => Set(ref _include, value); }
    private ScenarioOption _scenario = Scenarios[0];
    public ScenarioOption Scenario { get => _scenario; set => Set(ref _scenario, value); }
}

public sealed record RunRow(Guid RunId, string Summary, string When, bool IsSimulation, bool IsUnresolved);

public sealed class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}

/// <summary>
/// Simulation-mode queue. Scheduler callbacks arrive on background threads and are marshalled to the UI with
/// BeginInvoke (never blocking the scheduler). The queue itself runs off the UI thread.
/// </summary>
public sealed class QueueViewModel : ObservableObject
{
    public const int MaxLogLines = 300;
    private readonly QueueScheduler _scheduler;
    private readonly IRunHistory _history;
    private readonly Dispatcher _dispatcher;
    private CancellationTokenSource? _stop;

    public QueueViewModel(QueueScheduler scheduler, IRunHistory history)
    {
        _scheduler = scheduler;
        _history = history;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _scheduler.RunChanged += update => Post(() => OnRunChanged(update));
        _scheduler.EventReceived += runEvent => Post(() => AppendLog(runEvent.At, runEvent.Message));
        StartCommand = new AsyncCommand(() => RunDemoAsync(), HandleError);
        StopCommand = new RelayCommand(EmergencyStop);
        AcknowledgeCommand = new AsyncCommand(AcknowledgeAllAsync, HandleError);
    }

    public ObservableCollection<DemoGameOption> Games { get; } =
        [new(GameId.Genshin), new(GameId.StarRail), new(GameId.ZenlessZoneZero)];
    public ObservableCollection<string> LogLines { get; } = [];
    public ObservableCollection<RunRow> Runs { get; } = [];
    public ObservableCollection<RunRow> Unresolved { get; } = [];
    public AsyncCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public AsyncCommand AcknowledgeCommand { get; }

    /// <summary>Delay between simulated steps. Short in the smoke test, readable in normal use.</summary>
    public TimeSpan Step { get; set; } = TimeSpan.FromMilliseconds(700);
    public RunLimits Limits { get; set; } = new(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(8));

    private bool _isRunning;
    public bool IsRunning { get => _isRunning; private set { Set(ref _isRunning, value); Raise(nameof(IsIdle)); } }
    public bool IsIdle => !IsRunning;
    public bool HasUnresolved => Unresolved.Count > 0;

    private string _current = "空闲";
    public string Current { get => _current; private set => Set(ref _current, value); }
    private string _status = "选择每款游戏的模拟情形，然后开始。不会启动任何游戏或程序。";
    public string Status { get => _status; private set => Set(ref _status, value); }

    public async Task InitializeAsync() => await RefreshAsync();

    public async Task<QueueResult?> RunDemoAsync(IReadOnlyList<(GameId Game, DemoScenario Scenario)>? plan = null)
    {
        if (IsRunning) return null;
        var items = (plan ?? Games.Where(g => g.Include).Select(g => (g.Game, g.Scenario.Value)).ToList())
            .Select(p => new QueueItem(p.Game, new DemoEngineAdapter(p.Game, p.Scenario, Step), Limits)).ToList();
        if (items.Count == 0)
        {
            Status = "请至少选择一款游戏。";
            return null;
        }
        // Created on the UI thread before the queue starts, so a stop pressed at any moment after this is never lost.
        using var stop = new CancellationTokenSource();
        _stop = stop;
        IsRunning = true;
        LogLines.Clear();
        Status = "模拟队列运行中…（可随时紧急停止）";
        try
        {
            var queueTask = Task.Run(() => _scheduler.RunAsync(items, stop.Token));
            _queueTask = queueTask;
            var result = await queueTask;
            Status = RunPresentation.SimulationLabel + RunPresentation.QueueStatusText(result)
                + (result.NotStarted.Count > 0 ? "；未启动：" + string.Join("、", result.NotStarted.Select(RunPresentation.GameName)) : "");
            Serilog.Log.Information("Simulation queue finished: {Status} {Reason}", result.Status, result.BlockReason);
            return result;
        }
        finally
        {
            _stop = null;
            IsRunning = false;
            Current = "空闲";
            await RefreshAsync();
        }
    }

    private Task? _queueTask;

    /// <summary>
    /// Application shutdown: requests the emergency stop and waits, bounded, for the queue to end so its final state
    /// is recorded. Returns false when it did not end in time; the caller then exits anyway: every engine process
    /// VIvian started is in a kill-on-close Job that Windows terminates when VIvian exits, and the next start
    /// reconciles the unfinished run as Blocked with cleanup unconfirmed (never as cleaned up).
    /// </summary>
    public async Task<bool> StopForShutdownAsync(TimeSpan timeout)
    {
        if (_queueTask is not { IsCompleted: false } running) return true;
        EmergencyStop();
        var ended = await Task.WhenAny(running, Task.Delay(timeout)) == running;
        Serilog.Log.Information("Shutdown stop: queue {State}", ended ? "ended" : "still running at exit");
        return ended;
    }

    public void EmergencyStop()
    {
        if (_stop is not { } stop) return;
        // Sent immediately, no confirmation dialog or countdown. Cancelling runs adapter cancellation callbacks
        // synchronously, so it is done off the UI thread: a slow callback must never freeze the window.
        _ = Task.Run(() =>
        {
            try { stop.Cancel(); }
            catch (Exception ex) when (ex is ObjectDisposedException or AggregateException) { /* queue ended or callback failed */ }
        });
        Status = "已发出紧急停止…";
        Serilog.Log.Information("Emergency stop requested from UI");
    }

    private async Task AcknowledgeAllAsync()
    {
        foreach (var row in Unresolved.ToList())
            await Task.Run(() => _history.AcknowledgeAsync(row.RunId, "用户在界面上确认引擎和游戏均已关闭。", DateTimeOffset.UtcNow));
        Status = "已记录你的确认。原判定结果保持不变。";
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        var recent = await Task.Run(() => _history.ReadRecentRunsAsync(30));
        var unresolved = await Task.Run(() => _history.ReadUnresolvedAsync());
        Runs.Clear();
        foreach (var r in recent) Runs.Add(ToRow(r));
        Unresolved.Clear();
        foreach (var r in unresolved) Unresolved.Add(ToRow(r));
        Raise(nameof(HasUnresolved));
    }

    private static RunRow ToRow(RunRecord r) => new(r.RunId, RunPresentation.Summary(r),
        r.UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm:ss"), r.IsSimulation, r.IsUnresolved);

    private void OnRunChanged(RunUpdate update)
    {
        Current = RunPresentation.Summary(update);
        if (update.Phase is RunPhase.Finished or RunPhase.Blocked)
            AppendLog(update.At, "—— " + RunPresentation.Summary(update));
    }

    private void AppendLog(DateTimeOffset at, string message)
    {
        LogLines.Add($"{at.ToLocalTime():HH:mm:ss}  {message}");
        while (LogLines.Count > MaxLogLines) LogLines.RemoveAt(0);
    }

    private void Post(Action action)
    {
        // Observers must never block the scheduler: queue the update and return.
        try { _dispatcher.BeginInvoke(action, DispatcherPriority.Background); }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private void HandleError(Exception ex)
    {
        Status = "操作失败。请查看日志。";
        Serilog.Log.Warning("Queue operation failed: {ErrorType}", ex.GetType().Name);
    }
}
