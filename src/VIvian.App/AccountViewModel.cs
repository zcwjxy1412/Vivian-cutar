using System.Collections.ObjectModel;
using System.Windows;
using VIvian.Core;
using VIvian.Infrastructure;

namespace VIvian.App;

public sealed record AccountGameOption(GameId Id, string Name);

/// <summary>Persisted account profiles. Login secrets and game session files are never handled here.</summary>
public sealed class AccountViewModel : ObservableObject
{
    private readonly LocalDatabase _database;
    public LocalDatabase Database => _database;
    public AccountViewModel(LocalDatabase database)
    {
        _database = database;
        AddCommand = new(AddAsync, Error);
        SaveCommand = new(SaveAsync, Error);
        DeleteCommand = new(DeleteAsync, Error);
        MoveUpCommand = new(() => MoveAsync(-1), Error);
        MoveDownCommand = new(() => MoveAsync(1), Error);
        ReloadCommand = new(ReloadAsync, Error);
    }
    public IReadOnlyList<AccountGameOption> Games { get; } =
        [new(GameId.Genshin, "原神"), new(GameId.StarRail, "崩坏：星穹铁道"), new(GameId.ZenlessZoneZero, "绝区零")];
    private AccountGameOption _game = new(GameId.StarRail, "崩坏：星穹铁道");
    public AccountGameOption Game { get => _game; set { Set(ref _game, value); Raise(nameof(CanEditDailyPlan)); _ = ReloadAsync(); } }
    public bool CanEditDailyPlan => Game.Id == GameId.StarRail;
    public ObservableCollection<GameAccount> Accounts { get; } = [];
    public ObservableCollection<GameAccount> StarRailAccounts { get; } = [];
    public ObservableCollection<AccountBatch> RecentBatches { get; } = [];
    public ObservableCollection<AccountExecutionRecord> BatchResults { get; } = [];
    private AccountBatch? _selectedBatch;
    public AccountBatch? SelectedBatch
    {
        get => _selectedBatch;
        set { Set(ref _selectedBatch, value); _ = LoadBatchResultsAsync(value?.Id); }
    }
    private GameAccount? _selectedStarRail;
    public GameAccount? SelectedStarRail
    {
        get => _selectedStarRail;
        set { Set(ref _selectedStarRail, value); SelectionChanged?.Invoke(); }
    }
    private GameAccount? _selected;
    public event Action? SelectionChanged;
    public GameAccount? Selected
    {
        get => _selected;
        set
        {
            Set(ref _selected, value);
            if (Game.Id == GameId.StarRail && value is not null) SelectedStarRail = value;
            if (value is null) return;
            Name = value.Name; Region = value.Region; Uid = value.Uid ?? "";
            EngineInstallPath = value.InstallPath;
            Enabled = value.Enabled; AutoRun = value.AutoRun;
            _ = LoadSelectedPlanAsync(value.Id);
        }
    }
    private string _name = "";
    public string Name { get => _name; set => Set(ref _name, value); }
    private string _region = "";
    public string Region { get => _region; set => Set(ref _region, value); }
    private string _uid = "";
    public string Uid { get => _uid; set => Set(ref _uid, value); }
    private string _engineInstallPath = "";
    public string EngineInstallPath { get => _engineInstallPath; set => Set(ref _engineInstallPath, value); }
    private bool _enabled = true;
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    private bool _autoRun;
    public bool AutoRun { get => _autoRun; set => Set(ref _autoRun, value); }
    private bool _dailyTaskEnabled;
    public bool DailyTaskEnabled { get => _dailyTaskEnabled; set => Set(ref _dailyTaskEnabled, value); }
    private AccountTaskPlan? _selectedPlan;
    private string _status = "账号档案只保存名称、区服和 UID；实际登录须在游戏中核对。";
    public string Status { get => _status; private set => Set(ref _status, value); }
    public AsyncCommand AddCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand DeleteCommand { get; }
    public AsyncCommand MoveUpCommand { get; }
    public AsyncCommand MoveDownCommand { get; }
    public AsyncCommand ReloadCommand { get; }

    public async Task ReloadAsync()
    {
        var selectedId = Selected?.Id;
        var accounts = await _database.LoadAccountsAsync(Game.Id);
        Accounts.Clear();
        foreach (var account in accounts) Accounts.Add(account);
        var starId = SelectedStarRail?.Id;
        StarRailAccounts.Clear();
        foreach (var account in await _database.LoadAccountsAsync(GameId.StarRail)) StarRailAccounts.Add(account);
        var selectedBatchId = SelectedBatch?.Id;
        RecentBatches.Clear();
        foreach (var batch in await _database.LoadRecentBatchesAsync()) RecentBatches.Add(batch);
        SelectedBatch = RecentBatches.FirstOrDefault(b => b.Id == selectedBatchId) ?? RecentBatches.FirstOrDefault();
        SelectedStarRail = StarRailAccounts.FirstOrDefault(a => a.Id == starId) ?? StarRailAccounts.FirstOrDefault();
        Selected = Accounts.FirstOrDefault(a => a.Id == selectedId) ?? Accounts.FirstOrDefault();
        if (Selected is not null) await LoadSelectedPlanAsync(Selected.Id);
        Status = $"已读取 {Accounts.Count} 个账号档案。";
    }

    private async Task AddAsync()
    {
        var name = Name.Trim();
        if (name.Length == 0) { Status = "请先输入账号名称。"; return; }
        var added = await _database.AddAccountAsync(Game.Id, name, Region, string.IsNullOrWhiteSpace(Uid) ? null : Uid,
            EngineInstallPath);
        await ReloadAsync();
        Selected = Accounts.Single(a => a.Id == added.Id);
        await LoadSelectedPlanAsync(added.Id);
        Status = "账号档案已保存；登录状态尚未验证。";
    }

    private async Task SaveAsync()
    {
        if (Selected is null) { Status = "请先选择账号档案。"; return; }
        var desired = Selected with
        { Name = Name, Region = Region, Uid = string.IsNullOrWhiteSpace(Uid) ? null : Uid,
            InstallPath = EngineInstallPath, Enabled = Enabled, AutoRun = AutoRun };
        GameAccount updated;
        if (CanEditDailyPlan && _selectedPlan is { } plan && plan.AccountId == desired.Id)
        {
            var saved = await _database.SaveAccountAndPlanAsync(desired, plan with { DailyEnabled = DailyTaskEnabled });
            updated = saved.Account; _selectedPlan = saved.Plan;
        }
        else updated = await _database.SaveAccountAsync(desired);
        var index = Accounts.IndexOf(Selected);
        Accounts[index] = updated;
        Selected = updated;
        Status = "账号档案已保存。";
    }

    private async Task DeleteAsync()
    {
        if (Selected is null) return;
        if (MessageBox.Show($"删除 VIvian 中的“{Selected.Name}”档案？\n\n不会删除游戏客户端账号或登录凭据。",
                "确认删除账号档案", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (MessageBox.Show("请再次确认删除此档案。此操作会移除该账号在 VIvian 中保存的设置与任务计划。",
                "再次确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _database.DeleteAccountAsync(Selected.Id);
        Selected = null;
        await ReloadAsync();
        Status = "已删除 VIvian 账号档案；游戏客户端账号未受影响。";
    }

    private async Task MoveAsync(int direction)
    {
        if (Selected is null) return;
        var index = Accounts.IndexOf(Selected);
        var next = index + direction;
        if (next < 0 || next >= Accounts.Count) return;
        var other = Accounts[next];
        var selectedId = Selected.Id;
        await _database.SwapAccountOrderAsync(Selected, other);
        await ReloadAsync();
        Selected = Accounts.Single(a => a.Id == selectedId);
        Status = "执行顺序已更新。";
    }

    private async Task LoadSelectedPlanAsync(Guid id)
    {
        var plan = await _database.LoadAccountTaskPlanAsync(id);
        if (Selected?.Id != id) return;
        _selectedPlan = plan;
        DailyTaskEnabled = plan.DailyEnabled;
    }

    private async Task LoadBatchResultsAsync(Guid? id)
    {
        IReadOnlyList<AccountExecutionRecord> rows = id is null ? [] : await _database.LoadBatchResultsAsync(id.Value);
        if (SelectedBatch?.Id != id) return;
        BatchResults.Clear();
        foreach (var row in rows) BatchResults.Add(row);
    }

    private void Error(Exception ex) => Status = "账号操作失败：" + ex.Message;
}
