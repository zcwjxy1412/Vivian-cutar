using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Win32;
using VIvian.Core;
using VIvian.Core.Accounts;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Accounts;

namespace VIvian.App;

public sealed record AccountGameOption(GameId Id, string Name);

/// <summary>One persisted account plus the login-screen binding that lets VIvian select it without a UID.</summary>
public sealed class AccountRowViewModel : ObservableObject
{
    private readonly GameAccount _account;
    private readonly LocalDatabase.AccountLoginProfile? _profile;
    private string _name;
    private bool _isSelected;
    private bool _dailyEnabled;

    public AccountRowViewModel(GameAccount account, LocalDatabase.AccountLoginProfile? profile, AccountTaskPlan? plan)
    {
        _account = account;
        _profile = profile;
        _name = account.Name;
        _dailyEnabled = plan?.DailyEnabled ?? true;
        _isSelected = account.AutoRun && profile is not null;
    }

    public GameAccount Account => _account;
    public LocalDatabase.AccountLoginProfile? Profile => _profile;
    public Guid Id => _account.Id;
    public string Name { get => _name; set => Set(ref _name, value); }
    public string MaskedPhone => _profile?.MaskedPhone ?? "未绑定";
    public string LoginStatus => _profile is null ? "未绑定登录界面" : $"已识别 · {_profile.MaskedPhone}";
    public string BindingDetail => _profile is null
        ? "请打开星铁登录界面后扫描，或导入登录界面截图。"
        : $"第 {_profile.RowOrder + 1} 个选择项 · 置信度 {_profile.Confidence:P0}"
          + (string.IsNullOrWhiteSpace(_profile.ObservedUid) ? "" : $" · 已见 UID {_profile.ObservedUid}");
    public string Verification => _account.VerificationState switch
    {
        "uid_observed" => "已见过游戏 UID",
        "manual_verified" => "曾人工核对",
        "unverified" => "未核对",
        _ => _account.VerificationState,
    };
    public string LastResult => string.IsNullOrWhiteSpace(_account.LastResult) ? "尚无结果" : _account.LastResult;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public bool DailyEnabled { get => _dailyEnabled; set => Set(ref _dailyEnabled, value); }
}

/// <summary>Persisted account profiles. Login secrets and game session files are never handled here.</summary>
public sealed class AccountViewModel : ObservableObject
{
    private readonly LocalDatabase _database;
    private readonly AccountRecognitionService _recognition;
    private readonly IScanWindowShield _shield;

    public AccountViewModel(LocalDatabase database, AccountRecognitionService recognition, IScanWindowShield shield)
    {
        _database = database;
        _recognition = recognition;
        _shield = shield;
        ScanCommand = new(() => ExecuteScan(ScanAsync), Error);
        ImportCommand = new(ImportAsync, Error);
        SaveNameCommand = new(SaveNameAsync, Error);
        DeleteCommand = new(DeleteAsync, Error);
        MoveUpCommand = new(() => MoveAsync(-1), Error);
        MoveDownCommand = new(() => MoveAsync(1), Error);
        ReloadCommand = new(ReloadAsync, Error);
    }

    public LocalDatabase Database => _database;
    public Func<Func<Task>, Task> ExecuteScan { private get; set; } = action => action();
    public ObservableCollection<AccountRowViewModel> Rows { get; } = [];
    public ObservableCollection<AccountBatch> RecentBatches { get; } = [];
    public ObservableCollection<AccountExecutionRecord> BatchResults { get; } = [];
    public AccountConfigurationMode ConfigurationMode { get; set; } = AccountConfigurationMode.Unified;
    public bool IsUnified
    {
        get => ConfigurationMode == AccountConfigurationMode.Unified;
        set { if (value) { ConfigurationMode = AccountConfigurationMode.Unified; Raise(); Raise(nameof(IsIndependent)); } }
    }
    public bool IsIndependent
    {
        get => ConfigurationMode == AccountConfigurationMode.Independent;
        set { if (value) { ConfigurationMode = AccountConfigurationMode.Independent; Raise(); Raise(nameof(IsUnified)); } }
    }

    private AccountRowViewModel? _selectedRow;
    public AccountRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            Set(ref _selectedRow, value);
            Raise(nameof(SelectedStarRail));
            SelectionChanged?.Invoke();
        }
    }

    /// <summary>Kept for the engine-page account selector. It always maps back to the same row.</summary>
    public GameAccount? SelectedStarRail
    {
        get => SelectedRow?.Account;
        set
        {
            if (value is null) return;
            var row = Rows.FirstOrDefault(r => r.Id == value.Id);
            if (row is not null) SelectedRow = row;
        }
    }
    public event Action? SelectionChanged;

    /// <summary>Items for the game page's account selector (restored: the binding was left without a source).</summary>
    public IReadOnlyList<GameAccount> StarRailAccounts => Rows.Select(r => r.Account).ToArray();

    private AccountBatch? _selectedBatch;
    public AccountBatch? SelectedBatch
    {
        get => _selectedBatch;
        set { Set(ref _selectedBatch, value); _ = LoadBatchResultsAsync(value?.Id); }
    }

    private string _status = "先扫描星铁登录界面，VIvian 会按脱敏手机号建立账号档案。";
    public string Status { get => _status; private set => Set(ref _status, value); }
    public AsyncCommand ScanCommand { get; }
    public AsyncCommand ImportCommand { get; }
    public AsyncCommand SaveNameCommand { get; }
    public AsyncCommand DeleteCommand { get; }
    public AsyncCommand MoveUpCommand { get; }
    public AsyncCommand MoveDownCommand { get; }
    public AsyncCommand ReloadCommand { get; }

    public IReadOnlyList<AccountRowViewModel> SelectedRows => Rows
        .Where(r => r.IsSelected && r.Profile is not null)
        .OrderBy(r => r.Account.Order).ThenBy(r => r.Id).ToArray();

    public async Task ReloadAsync()
    {
        var selectedId = SelectedRow?.Id;
        var selectedIds = Rows.Where(r => r.IsSelected).Select(r => r.Id).ToHashSet();
        var names = Rows.ToDictionary(r => r.Id, r => r.Name);
        var daily = Rows.ToDictionary(r => r.Id, r => r.DailyEnabled);
        var accounts = await _database.LoadAccountsAsync(GameId.StarRail);
        var profiles = (await _database.LoadLoginProfilesAsync(GameId.StarRail)).ToDictionary(p => p.AccountId);
        var rows = new List<AccountRowViewModel>();
        foreach (var account in accounts)
        {
            var plan = await _database.LoadAccountTaskPlanAsync(account.Id);
            var row = new AccountRowViewModel(account, profiles.GetValueOrDefault(account.Id), plan);
            if (names.TryGetValue(account.Id, out var name) && !string.IsNullOrWhiteSpace(name)) row.Name = name;
            if (selectedIds.Contains(account.Id)) row.IsSelected = true;
            if (daily.TryGetValue(account.Id, out var dailyEnabled)) row.DailyEnabled = dailyEnabled;
            rows.Add(row);
        }
        Rows.Clear();
        foreach (var row in rows) Rows.Add(row);
        Raise(nameof(StarRailAccounts));
        var batchId = SelectedBatch?.Id;
        RecentBatches.Clear();
        foreach (var batch in await _database.LoadRecentBatchesAsync()) RecentBatches.Add(batch);
        SelectedBatch = RecentBatches.FirstOrDefault(b => b.Id == batchId) ?? RecentBatches.FirstOrDefault();
        SelectedRow = Rows.FirstOrDefault(r => r.Id == selectedId) ?? Rows.FirstOrDefault();
        Status = Rows.Count == 0
            ? "尚无账号档案。请打开星铁登录界面并点击“扫描登录账号”。"
            : $"已读取 {Rows.Count} 个账号档案，其中 {Rows.Count(r => r.Profile is not null)} 个已绑定登录界面。";
    }

    private async Task ScanAsync()
    {
        Status = "正在识别星铁登录界面（本地 OCR）：VIvian 会暂时最小化，截图只在本机处理…";
        AccountRecognitionService.RegistrationResult result;
        // VIvian runs unelevated and must not raise the elevated game; it steps aside instead and verifies that the
        // captured pixels belong to the game window before reading them.
        await using (await _shield.HideAsync(CancellationToken.None))
            result = await _recognition.ScanAndRegisterAsync(CancellationToken.None);
        await ReloadAsync();
        var where = result.View?.Evidence is { } evidence
            ? $"（游戏窗口 pid {evidence.ProcessId}，{evidence.ClientWidth}x{evidence.ClientHeight}，{(evidence.Check.Method == "print" ? "窗口渲染" : $"屏幕截取 {evidence.Check.SamplesOwnedByGame}/{evidence.Check.Samples} 校验点")}）"
            : "";
        Status = result.Success
            ? result.Detail + where + (result.Warnings.Count > 0 ? " " + string.Join(" ", result.Warnings) : "")
            : "扫描失败：" + result.Detail + where;
        Serilog.Log.Information("Account scan success={Success}, registered={Count}, detail={Detail}",
            result.Success, result.Registered.Count,
            VIvian.Infrastructure.Diagnostics.Redactor.ForCurrentUser().Redact(result.Detail));
    }

    private async Task ImportAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入星铁登录界面截图",
            Filter = "图片 (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;
        Status = "正在识别导入的截图（本地 OCR）…";
        var result = await _recognition.ImportAndRegisterAsync(dialog.FileName, CancellationToken.None);
        await ReloadAsync();
        Status = result.Success
            ? result.Detail + (result.Warnings.Count > 0 ? " " + string.Join(" ", result.Warnings) : "")
            : "识别失败：" + result.Detail;
    }

    private async Task SaveNameAsync()
    {
        if (SelectedRow is null) { Status = "请先选择账号。"; return; }
        var name = SelectedRow.Name.Trim();
        if (name.Length is 0 or > 80) { Status = "账号备注须为 1–80 个字符。"; return; }
        var saved = await _database.SaveAccountAsync(SelectedRow.Account with { Name = name, AutoRun = SelectedRow.IsSelected });
        var plan = await _database.LoadAccountTaskPlanAsync(saved.Id);
        await _database.SaveAccountTaskPlanAsync(plan with { DailyEnabled = SelectedRow.DailyEnabled });
        await ReloadAsync();
        Status = "账号备注、执行勾选与任务计划已保存。";
    }

    private async Task DeleteAsync()
    {
        if (SelectedRow is null) return;
        if (MessageBox.Show($"删除 VIvian 中的“{SelectedRow.Name}”档案？\n\n不会删除游戏客户端账号或登录凭据。",
                "确认删除账号档案", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _database.DeleteAccountAsync(SelectedRow.Id);
        SelectedRow = null;
        await ReloadAsync();
        Status = "已删除 VIvian 账号档案；游戏客户端账号未受影响。";
    }

    private async Task MoveAsync(int direction)
    {
        if (SelectedRow is null) return;
        var index = Rows.IndexOf(SelectedRow);
        var next = index + direction;
        if (next < 0 || next >= Rows.Count) return;
        var other = Rows[next];
        var selectedId = SelectedRow.Id;
        await _database.SwapAccountOrderAsync(SelectedRow.Account, other.Account);
        await ReloadAsync();
        SelectedRow = Rows.Single(r => r.Id == selectedId);
        Status = "执行顺序已更新。";
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
