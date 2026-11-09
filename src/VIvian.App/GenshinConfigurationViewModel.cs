using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Engines.BetterGi;
using VIvian.Infrastructure.Processes;

namespace VIvian.App;

public sealed class GenshinConfigurationViewModel
    : ObservableObject
{
    private readonly LocalDatabase _database;
    private readonly IAccountConfigurationProvider _provider;
    private readonly Func<bool> _hostIdle;
    private readonly GenshinAccountImportService _import;
    private readonly CancellationTokenSource _close = new();
    private readonly Func<bool> _desktopIdle;
    private readonly Func<string> _betterGiRoot;
    private readonly string _profileDirectory;
    private CancellationTokenSource? _verification;
    private AccountConfigurationView? _snapshot;
    private GameAccount? _selectedAccount;
    private string _newAccountName = "";
    private string _status = "请选择原神档案，编辑本机任务计划。";
    private string _loginStatus = "请选择已从截图建档的账号，然后指定原神国服程序。";
    private string _gameExecutable = "";
    private bool _gameExecutableAuto;
    private GameAccount? _secondLoginAccount;
    private bool _busy;

    public GenshinConfigurationViewModel(LocalDatabase database, IAccountConfigurationProvider provider, Func<bool> hostIdle,
        string profileDirectory, Func<bool> desktopIdle, Func<string> betterGiRoot, Func<Func<Task>, Task> serializeSourceWrite)
    {
        _database = database;
        _provider = provider;
        _hostIdle = hostIdle;
        _desktopIdle = desktopIdle;
        _betterGiRoot = betterGiRoot;
        _profileDirectory = profileDirectory;
        _import = new(database);
        Source = new(profileDirectory, desktopIdle, () => IsIdle && !Parameters.Any(row => row.IsChanged), betterGiRoot,
            serializeSourceWrite);
        Source.FieldsChanged += () => Raise(nameof(VisiblePlanParameters));
        Source.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Source.IsIdle)) Raise(nameof(CanEdit));
            if (e.PropertyName == nameof(Source.CanConnect)) Raise(nameof(CanConnect));
        };
        ReloadCommand = new(ReloadConfigurationAsync, ReportError);
        AddAccountCommand = new(AddAccountAsync, ReportError);
        ImportCommand = new(() => ChooseScreenshotAsync(bindExisting: false), ReportError);
        BindLoginScreenshotCommand = new(() => ChooseScreenshotAsync(bindExisting: true), ReportError);
        SaveCommand = new(SaveAsync, ReportError);
        ConnectSourceCommand = new(ConnectSourceAsync, ReportError);
        VerifyLoginCommand = new(VerifyLoginAsync, ex => LoginStatus = UserFacingError.Describe(ex));
        CancelLoginCommand = new(_ => _verification?.Cancel());
        ClearSecondLoginAccountCommand = new(_ => SecondLoginAccount = null);
    }

    public ObservableCollection<GameAccount> Accounts { get; } = [];
    public ObservableCollection<ParameterRow> Parameters { get; } = [];
    public IEnumerable<ParameterRow> VisiblePlanParameters => Parameters.Select(row =>
        Source.ProfileRows.FirstOrDefault(source => source.Path == row.Path) ?? row);
    public AsyncCommand ReloadCommand { get; }
    public AsyncCommand AddAccountCommand { get; }
    public AsyncCommand ImportCommand { get; }
    public AsyncCommand BindLoginScreenshotCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand ConnectSourceCommand { get; }
    public GenshinSourceConfigurationViewModel Source { get; }
    public AsyncCommand VerifyLoginCommand { get; }
    public RelayCommand<object> CancelLoginCommand { get; }
    public RelayCommand<object> ClearSecondLoginAccountCommand { get; }
    public bool IsLoginVerificationAvailable => RealExecutionPolicy.IsM5DailyTest && !RealExecutionPolicy.IsUnifiedInstall;
    public bool IsVerifyingLogin => _verification is not null;
    public string LoginStatus { get => _loginStatus; private set => Set(ref _loginStatus, value); }
    public string GameExecutable
    {
        get => _gameExecutable;
        set { _gameExecutableAuto = false; Set(ref _gameExecutable, value); }
    }
    public GameAccount? SecondLoginAccount { get => _secondLoginAccount; set => Set(ref _secondLoginAccount, value); }

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
    public bool IsIdle => !_busy && !IsVerifyingLogin;
    public bool CanBindLoginScreenshot => IsIdle && SelectedAccount is { Enabled: true, LoginState: not "recognized" };
    public bool CanEdit => IsIdle && Source.IsIdle && _snapshot is { WriteUnavailableReason: null };
    public bool CanConnect => CanEdit && Source.CanConnect;

    internal void Close()
    {
        Source.SetVisible(false);
        _close.Cancel();
    }

    internal async Task DiscoverGameExecutableAsync()
    {
        var root = _betterGiRoot();
        var previous = GameExecutable;
        var wasAuto = _gameExecutableAuto;
        if (previous.Length > 0 && !wasAuto) return;
        if (root.Length == 0)
        {
            if (wasAuto) GameExecutable = "";
            return;
        }
        try
        {
            var executable = await BetterGiConfiguration.FindClientExecutableAsync(root, _close.Token);
            if (_close.IsCancellationRequested || previous != GameExecutable || wasAuto != _gameExecutableAuto ||
                root != _betterGiRoot()) return;
            _gameExecutableAuto = executable is not null;
            Set(ref _gameExecutable, executable ?? "", nameof(GameExecutable));
        }
        catch (OperationCanceledException) when (_close.IsCancellationRequested) { }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException
            or IOException or UnauthorizedAccessException or TimeoutException or System.Text.Json.JsonException)
        {
            if (previous != GameExecutable || wasAuto != _gameExecutableAuto || root != _betterGiRoot()) return;
            if (wasAuto) GameExecutable = "";
            LoginStatus = "暂时无法读取 BetterGI 保存的国服客户端路径，可以手动选择。";
        }
    }

    internal Task LoadAsync() => ConfirmDiscard(includeSource: false) ? RunAsync(() => LoadAccountsAsync(SelectedAccount?.Id)) : Task.CompletedTask;

    private Task ReloadConfigurationAsync() => ConfirmDiscard() ? RunAsync(async () =>
    {
        await LoadAccountsAsync(SelectedAccount?.Id);
        await Source.ReloadAsync();
    }) : Task.CompletedTask;

    private Task ConnectSourceAsync()
    {
        if (!CanConnect || !_desktopIdle()) return Task.CompletedTask;
        return RunAsync(async () =>
        {
            if (Parameters.Any(row => row.IsChanged))
            {
                Status = "请先保存当前账号的任务设置，再连接 BetterGI。";
                return;
            }
            if (_snapshot is not { } snapshot) return;
            var current = await _provider.ReadAsync(snapshot.Target, _close.Token);
            if (current.Revision != snapshot.Revision || current.WriteUnavailableReason is not null)
                throw new SettingsConflictException();
            await Source.ConnectAsync(current.Target.AccountId,
                current.Values.ToDictionary(value => value.Schema.Path, value => value.Value!), _close.Token);
        });
    }

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

    private async Task ChooseScreenshotAsync(bool bindExisting)
    {
        if (!_hostIdle() || !IsIdle || !Source.IsIdle) return;
        var target = bindExisting ? SelectedAccount : null;
        if (bindExisting && (target is null || !CanBindLoginScreenshot)) return;
        var dialog = new OpenFileDialog
        {
            Title = target is null ? "导入原神登录截图或照片" : $"为「{target.Name}」绑定原神登录截图",
            Filter = "图片 (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true) await ImportScreenshotAsync(dialog.FileName, target?.Id);
    }

    internal Task ImportScreenshotAsync(string path, Guid? target = null) => RunAsync(async () =>
    {
        if (!_hostIdle() || (target is not null && SelectedAccount?.Id != target) || !ConfirmDiscard()) return;
        Status = "正在本机识别原神登录截图…";
        var result = target is { } id ? await _import.BindExistingAsync(path, id, _close.Token)
            : await _import.ImportAndRegisterAsync(path, _close.Token);
        _close.Token.ThrowIfCancellationRequested();
        if (result is { Success: true, Account: { Game: GameId.Genshin } account })
            await LoadAccountsAsync(account.Id);
        Status = result.Detail;
    });

    private async Task VerifyLoginAsync()
    {
        if (!IsLoginVerificationAvailable || !IsIdle || !Source.IsIdle) return;
        if (!_desktopIdle()) { LoginStatus = "请先结束正在运行的任务，并核对清理和恢复状态。"; return; }
        if (!WindowsProcessSupervisor.IsCurrentProcessElevated())
        {
            LoginStatus = "登录验收需要以管理员身份打开隔离测试版。";
            return;
        }
        await DiscoverGameExecutableAsync();
        if (!IsIdle || !Source.IsIdle || !_desktopIdle() || _close.IsCancellationRequested) return;
        if (SelectedAccount is not { LoginState: "recognized", Enabled: true } first ||
            SecondLoginAccount is { } second && (second.Id == first.Id || !second.Enabled || second.LoginState != "recognized"))
        {
            LoginStatus = "请选择一个或两个不同、已从截图识别的原神档案。";
            return;
        }
        var accounts = SecondLoginAccount is { } next ? new[] { first, next } : new[] { first };
        var program = GameExecutable.Trim();
        if (!Path.IsPathFullyQualified(program) || !File.Exists(program) ||
            !string.Equals(Path.GetFileName(program), "YuanShen.exe", StringComparison.OrdinalIgnoreCase))
        {
            LoginStatus = "请选择已安装的原神国服 YuanShen.exe。";
            return;
        }
        var betterGi = Source.BoundInstallRoot ?? _betterGiRoot();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_close.Token);
        _verification = cancel;
        Raise(nameof(IsVerifyingLogin));
        Raise(nameof(IsIdle));
        Raise(nameof(CanEdit));
        Raise(nameof(CanBindLoginScreenshot));
        try
        {
            for (var seconds = 5; seconds > 0; seconds--)
            {
                LoginStatus = $"{seconds} 秒后开始。请切到已打开的原神国服前台。";
                await Task.Delay(TimeSpan.FromSeconds(1), cancel.Token);
            }
            if (!_desktopIdle()) { LoginStatus = "桌面或任务状态已变化，登录验收已取消。"; return; }
            LoginStatus = "正在验收原神登录。";
            var results = await GenshinLoginAcceptance.VerifyAsync(_database, accounts.Select(a => a.Id).ToArray(),
                betterGi, program, _profileDirectory, cancel.Token);
            LoginStatus = string.Join("\n", results.Select(result =>
                $"{accounts.Single(a => a.Id == result.AccountId).Name} · {(result.Success ? "通过" : "未通过")} · {result.Detail}"));
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { LoginStatus = "已取消登录验收。"; }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or TimeoutException)
        {
            LoginStatus = ex.Message;
        }
        finally
        {
            _verification = null;
            Raise(nameof(IsVerifyingLogin));
            Raise(nameof(IsIdle));
            Raise(nameof(CanEdit));
            Raise(nameof(CanBindLoginScreenshot));
        }
    }

    internal Task SaveAsync()
    {
        if (!CanEdit || !_hostIdle()) return Task.CompletedTask;
        return RunAsync(async () =>
        {
            var hasSourceDraft = Source.HasDraft;
            if (await SaveLocalChangesAsync() && hasSourceDraft) await Source.SaveAsync();
        });
    }

    private async Task<bool> SaveLocalChangesAsync()
    {
        if (!_hostIdle() || _snapshot is not { WriteUnavailableReason: null } snapshot) return false;
        var changes = Parameters.Where(row => row.IsChanged && !Source.Manages(row.Path))
            .ToDictionary(row => row.Path, row => row.Current);
        if (changes.Count == 0)
        {
            if (!Source.HasDraft) Status = "没有需要保存的修改。";
            return true;
        }
        var proposal = await _provider.ProposeAsync(snapshot.Target, snapshot.Revision, changes, CancellationToken.None);
        if (!proposal.Validation.Valid) { Status = proposal.Validation.Reason; return false; }
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
        Status = $"已保存并读回核对「{snapshot.AccountName}」在 VIvian 中管理的字段。审计 {applied.AuditId:D}";
        return true;
    }

    private async Task LoadAccountsAsync(Guid? accountId)
    {
        var current = await _database.LoadAccountsAsync(GameId.Genshin);
        var secondId = SecondLoginAccount?.Id;
        Accounts.Clear();
        foreach (var account in current) Accounts.Add(account);
        SecondLoginAccount = current.FirstOrDefault(a => a.Id == secondId);
        await SelectAsync(current.FirstOrDefault(a => a.Id == accountId)
            ?? current.FirstOrDefault(a => a.Enabled && a.LoginState == "recognized")
            ?? current.FirstOrDefault());
    }

    private async Task SelectAsync(GameAccount? account)
    {
        _selectedAccount = account;
        Source.SetAccount(account?.Id);
        Raise(nameof(SelectedAccount));
        Raise(nameof(CanBindLoginScreenshot));
        Show(null);
        if (account is null) { Status = "请先新增原神档案。"; return; }
        var view = await _provider.ReadAsync(new(GameId.Genshin, account.Id), CancellationToken.None);
        Show(view);
        Status = view.WriteUnavailableReason ?? $"正在编辑「{view.AccountName}」的任务设置。";
    }

    private void Show(AccountConfigurationView? snapshot)
    {
        _snapshot = snapshot;
        Parameters.Clear();
        if (snapshot is not null)
            foreach (var value in snapshot.Values) Parameters.Add(new(value));
        Raise(nameof(VisiblePlanParameters));
        Raise(nameof(CanEdit));
        Raise(nameof(CanConnect));
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (!IsIdle) return;
        _busy = true;
        Raise(nameof(IsIdle));
        Raise(nameof(CanEdit));
        Raise(nameof(CanConnect));
        Raise(nameof(CanBindLoginScreenshot));
        try { await action(); }
        catch (OperationCanceledException) when (_close.IsCancellationRequested) { }
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
            Raise(nameof(CanConnect));
            Raise(nameof(CanBindLoginScreenshot));
        }
    }

    private bool ConfirmDiscard(bool includeSource = true) => Source.IsIdle &&
        (!Parameters.Any(row => row.IsChanged) ||
            MessageBox.Show("切换或重新读取会丢弃当前未保存的修改。继续？", "原神任务计划",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes) &&
        (!includeSource || Source.ConfirmDiscard());

    private void ReportError(Exception error) => Status = UserFacingError.Describe(error);
}
