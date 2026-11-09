using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Data.Sqlite;
using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Infrastructure;

namespace VIvian.App;

public sealed class GenshinConfigurationViewModel
    : ObservableObject
{
    private readonly LocalDatabase _database;
    private readonly IAccountConfigurationProvider _provider;
    private readonly Func<bool> _hostIdle;
    private AccountConfigurationView? _snapshot;
    private GameAccount? _selectedAccount;
    private string _newAccountName = "";
    private string _status = "请选择原神档案，编辑本机任务计划。";
    private bool _busy;

    public GenshinConfigurationViewModel(LocalDatabase database, IAccountConfigurationProvider provider, Func<bool> hostIdle)
    {
        _database = database;
        _provider = provider;
        _hostIdle = hostIdle;
        ReloadCommand = new(LoadAsync, ReportError);
        AddAccountCommand = new(AddAccountAsync, ReportError);
        SaveCommand = new(SaveAsync, ReportError);
    }

    public ObservableCollection<GameAccount> Accounts { get; } = [];
    public ObservableCollection<ParameterRow> Parameters { get; } = [];
    public AsyncCommand ReloadCommand { get; }
    public AsyncCommand AddAccountCommand { get; }
    public AsyncCommand SaveCommand { get; }

    public GameAccount? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (Equals(_selectedAccount, value) || !IsIdle) return;
            if (!ConfirmDiscard())
            {
                Raise(nameof(SelectedAccount));
                return;
            }
            _ = RunAsync(() => SelectAsync(value));
        }
    }

    public string NewAccountName { get => _newAccountName; set => Set(ref _newAccountName, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool IsIdle => !_busy;
    public bool CanEdit => IsIdle && _snapshot is { WriteUnavailableReason: null };

    internal Task LoadAsync() => ConfirmDiscard() ? RunAsync(() => LoadAccountsAsync(SelectedAccount?.Id)) : Task.CompletedTask;

    internal Task AddAccountAsync() => RunAsync(async () =>
    {
        if (!_hostIdle() || !ConfirmDiscard()) return;
        var name = NewAccountName.Trim();
        if (name.Length == 0)
        {
            Status = "请填写原神账号档案的备注。";
            return;
        }
        var accounts = await _database.LoadAccountsAsync(GameId.Genshin);
        if (accounts.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            Status = "已有同名档案，请用不同备注区分账号。";
            return;
        }
        var added = await _database.AddAccountAsync(GameId.Genshin, name, "", null);
        NewAccountName = "";
        await LoadAccountsAsync(added.Id);
    });

    internal Task SaveAsync() => RunAsync(async () =>
    {
        if (!_hostIdle() || _snapshot is not { WriteUnavailableReason: null } snapshot) return;
        var changes = Parameters.Where(row => row.IsChanged).ToDictionary(row => row.Path, row => row.Current);
        if (changes.Count == 0) { Status = "没有需要保存的修改。"; return; }
        var proposal = await _provider.ProposeAsync(snapshot.Target, snapshot.Revision, changes, CancellationToken.None);
        if (!proposal.Validation.Valid) { Status = proposal.Validation.Reason; return; }
        var applied = await _provider.ApplyAsync(proposal,
            () => ReferenceEquals(_snapshot, snapshot) && SelectedAccount?.Id == snapshot.Target.AccountId && _hostIdle(),
            CancellationToken.None);
        var saved = proposal.Changes.ToDictionary(change => change.Path, change => change.NewValue, StringComparer.Ordinal);
        Show(snapshot with
        {
            Revision = applied.Revision,
            Values = snapshot.Values.Select(value => saved.TryGetValue(value.Schema.Path, out var updated)
                ? value with { Value = updated } : value).ToArray(),
        });
        Status = $"已保存并读回核对「{snapshot.AccountName}」的原神计划。审计 {applied.AuditId:D}";
    });

    private async Task LoadAccountsAsync(Guid? accountId)
    {
        var current = await _database.LoadAccountsAsync(GameId.Genshin);
        Accounts.Clear();
        foreach (var account in current) Accounts.Add(account);
        await SelectAsync(current.FirstOrDefault(a => a.Id == accountId) ?? current.FirstOrDefault());
    }

    private async Task SelectAsync(GameAccount? account)
    {
        _selectedAccount = account;
        Raise(nameof(SelectedAccount));
        Show(null);
        if (account is null) { Status = "请先新增原神档案。"; return; }
        var view = await _provider.ReadAsync(new(GameId.Genshin, account.Id), CancellationToken.None);
        Show(view);
        Status = view.WriteUnavailableReason ?? $"正在编辑「{view.AccountName}」的本机任务计划。";
    }

    private void Show(AccountConfigurationView? snapshot)
    {
        _snapshot = snapshot;
        Parameters.Clear();
        if (snapshot is not null)
            foreach (var value in snapshot.Values) Parameters.Add(new(value));
        Raise(nameof(CanEdit));
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        Raise(nameof(IsIdle));
        Raise(nameof(CanEdit));
        try { await action(); }
        catch (Exception ex) when (ex is SettingsConflictException or ArgumentException or InvalidOperationException
            or IOException or SqliteException or UnauthorizedAccessException)
        {
            ReportError(ex);
        }
        finally
        {
            _busy = false;
            Raise(nameof(IsIdle));
            Raise(nameof(CanEdit));
        }
    }

    private bool ConfirmDiscard() => !Parameters.Any(row => row.IsChanged) ||
        MessageBox.Show("切换或重新读取会丢弃当前未保存的修改。继续？", "原神任务计划",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void ReportError(Exception error) => Status = UserFacingError.Describe(error);
}
