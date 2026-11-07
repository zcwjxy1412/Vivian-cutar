using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Infrastructure;

namespace VIvian.App;

public sealed class ParameterRow(ConfigurationValue value) : ObservableObject
{
    public string Path => value.Schema.Path;
    public string Title => value.Schema.Title;
    public string Description => value.Schema.Description;
    public string Access => value.Schema.Access == ParameterAccess.VerifiedReadWrite ? "可编辑" : "由上游管理";
    public bool Editable => value.Schema.Access == ParameterAccess.VerifiedReadWrite && value.Value is not null;
    public string Original => value.Value ?? "";
    private string _current = value.Value ?? "";
    public string Current
    {
        get => _current;
        set { Set(ref _current, value); Raise(nameof(BoolValue)); Raise(nameof(IsChanged)); }
    }
    public string State => value.State;
    public string Group => value.Schema.Group;
    public string Rule => ParameterRules.Describe(value.Schema);
    public string Scope => value.Schema.Scope;
    public string Dependencies => value.Schema.Dependencies;
    public bool IsBoolean => value.Schema.DataType == "boolean";
    public bool IsChoice => value.Schema.DataType == "enum";
    public bool IsTextual => value.Schema.DataType is "integer" or "text";
    public bool IsReadOnlyDisplay => !IsBoolean && !IsChoice && !IsTextual;
    public IReadOnlyList<ParameterOption> Options => ParameterRules.LabelledOptions(value.Schema);
    public bool IsChanged => Editable && Current != Original;
    /// <summary>Two-way switch binding for boolean parameters; writes "true"/"false" into <see cref="Current"/>.</summary>
    public bool BoolValue { get => Current == "true"; set => Current = value ? "true" : "false"; }
    /// <summary>What is shown when the field cannot be edited here.</summary>
    public string DisplayValue => value.Value ?? "—";
}

public sealed class ConfigurationViewModel : ObservableObject, IDisposable
{
    private readonly IConfigurationControl _control;
    private readonly Func<GameId, string> _installPath;
    private readonly Func<bool> _isRunning;
    private readonly string _auditDirectory;
    private readonly LocalDatabase _accounts;
    private readonly Func<GameAccount?> _selectedAccount;
    private AccountSettingsSnapshot? _accountSettings;
    private ConfigurationSnapshot? _snapshot;
    private FileSystemWatcher? _watcher;
    private System.Windows.Threading.DispatcherTimer? _timer;
    private System.Windows.Threading.DispatcherTimer? _debounce;
    private bool _applying;
    private GameId _game = GameId.StarRail;
    private string _status = "正在读取星铁助手的配置…";
    public string Status { get => _status; private set => Set(ref _status, value); }
    public ObservableCollection<ParameterRow> Parameters { get; } = [];
    public AsyncCommand ReadCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    /// <summary>Edits not yet written to the upstream file (queued while a task runs, or invalid values).</summary>
    public int PendingCount => Parameters.Count(p => p.IsChanged);

    public ConfigurationViewModel(IConfigurationControl control, Func<GameId, string> installPath,
        Func<bool> isRunning, string auditDirectory, LocalDatabase accounts, Func<GameAccount?> selectedAccount)
    {
        _control = control; _installPath = installPath; _isRunning = isRunning; _auditDirectory = auditDirectory;
        _accounts = accounts; _selectedAccount = selectedAccount;
        ReadCommand = new(ReadAsync, Error);
        SaveCommand = new(() => { TryApplyPending(interactive: true); return Task.CompletedTask; }, Error);
        OpenSettingsCommand = new(OpenSettings);
        OpenUpstreamCommand = new(OpenUpstreamAsync, Error);
    }

    public AsyncCommand OpenUpstreamCommand { get; }

    /// <summary>Opens March7th's own window; whatever is saved there flows back here through the file watcher.</summary>
    private async Task OpenUpstreamAsync()
    {
        if (_isRunning()) { Status = "任务运行中，请等它结束后再打开星铁助手，避免两边同时操作游戏。"; return; }
        var path = _installPath(_game);
        if (string.IsNullOrWhiteSpace(path)) { Status = "请先在“引擎与环境”填写安装目录。"; return; }
        if (_snapshot is null) await ReadAsync();
        try
        {
            var version = Path.Combine(path, "assets", "config", "version.txt");
            if (!File.Exists(version)) { Status = "未找到星铁助手版本文件；请检查安装目录。"; return; }
            if (File.ReadAllText(version).Trim() != EngineCatalog.March7th.PinnedVersion)
            { Status = "星铁助手版本与当前适配版本不兼容，请先核对安装版本。"; return; }
            VIvian.Infrastructure.Processes.WindowsProcessSupervisor.OpenUserInterface(path, "March7th Launcher.exe");
            Status = "已打开星铁助手主界面；此版本没有已核对的支援设置直达入口，请在原程序中打开详细设置。保存后会同步到这里。";
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { Status = "你在管理员确认里选了“否”，星铁助手没有打开。"; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or ArgumentException)
        { Status = "没能打开星铁助手：" + ex.Message; }
    }

    public void SelectGame(GameId game)
    {
        _game = game;
        _watcher?.Dispose(); _watcher = null;
        _snapshot = null; Parameters.Clear();
        Status = "已切换游戏；点击“重新读取”。";
    }

    public async Task SelectAccountAsync()
    {
        _debounce?.Stop();
        _snapshot = null;
        _accountSettings = null;
        Parameters.Clear();
        if (!string.IsNullOrWhiteSpace(_installPath(GameId.StarRail))) await ReadAsync();
        else Status = "请先为新增账号设置专属 March7thAssistant 安装目录。";
    }

    public async Task ReadAsync()
    {
        var selectedId = _selectedAccount()?.Id;
        var path = _installPath(_game);
        if (string.IsNullOrWhiteSpace(path)) { Status = "请先在“引擎与环境”填写安装目录。"; return; }
        var snapshot = _control.ReadConfiguration(_game, path);
        _accountSettings = null;
        if (_game == GameId.StarRail && _selectedAccount() is { } account)
        {
            _accountSettings = await _accounts.LoadAccountSettingsAsync(account.Id);
            if (account.Id != Guid.Parse("00000000-0000-4000-8000-000000000002"))
            {
                if (_accountSettings.Values.Count == 0)
                {
                    var initial = snapshot.Values.Where(v => v.Schema.Access == ParameterAccess.VerifiedReadWrite && v.Value is not null)
                        .ToDictionary(v => v.Schema.Path, v => v.Value!);
                    _accountSettings = await _accounts.SaveAccountSettingsAsync(_accountSettings with { Values = initial });
                }
                snapshot = OverlayAccount(snapshot, _accountSettings);
            }
        }
        if (_selectedAccount()?.Id != selectedId) return;
        _snapshot = snapshot;
        Show(snapshot, null);
        Status = _accountSettings is not null && _selectedAccount()?.Id != Guid.Parse("00000000-0000-4000-8000-000000000002")
            ? "已读取所选账号的独立设置；修改保存到 VIvian，运行前需核对登录身份。"
            : $"已读取 {snapshot.PinnedVersion}；{snapshot.ReadAt.ToLocalTime():HH:mm:ss} 同步。修改会立即写入星铁助手。";
        _watcher?.Dispose();
        if (File.Exists(snapshot.Path))
        {
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(snapshot.Path)!, Path.GetFileName(snapshot.Path))
            { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size, EnableRaisingEvents = true };
            _watcher.Changed += OnExternalChange;
            _watcher.Created += OnExternalChange;
            _watcher.Renamed += OnExternalChange;
        }
        if (_timer is null && Application.Current is not null)
        {
            _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _timer.Tick += (_, _) => { Reconcile(); TryApplyPending(interactive: false); };
            _timer.Start();
        }
    }

    private static ConfigurationSnapshot OverlayAccount(ConfigurationSnapshot source, AccountSettingsSnapshot settings) => source with
    {
        Values = source.Values.Select(v => settings.Values.TryGetValue(v.Schema.Path, out var saved)
            && v.Schema.Access == ParameterAccess.VerifiedReadWrite && ParameterRules.IsValid(v.Schema, saved)
                ? v with { Value = saved, State = "账号独立设置" } : v).ToArray()
    };

    /// <summary>Rows for a snapshot; carried-over edits keep their values. Each row auto-saves when edited.</summary>
    private void Show(ConfigurationSnapshot snapshot, IReadOnlyDictionary<string, string>? edits)
    {
        foreach (var old in Parameters) old.PropertyChanged -= OnRowChanged;
        Parameters.Clear();
        foreach (var value in snapshot.Values)
        {
            var row = new ParameterRow(value);
            if (edits is not null && edits.TryGetValue(row.Path, out var current)) row.Current = current;
            row.PropertyChanged += OnRowChanged;
            Parameters.Add(row);
        }
        Raise(nameof(PendingCount));
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ParameterRow.Current) || _applying) return;
        Raise(nameof(PendingCount));
        // Switches and choices save at once; typed text waits until the user pauses.
        var delay = sender is ParameterRow { IsTextual: true } ? TimeSpan.FromMilliseconds(900) : TimeSpan.FromMilliseconds(150);
        if (Application.Current is null) { TryApplyPending(interactive: true); return; }
        _debounce ??= new System.Windows.Threading.DispatcherTimer();
        _debounce.Stop();
        _debounce.Interval = delay;
        _debounce.Tick -= OnDebounce;
        _debounce.Tick += OnDebounce;
        _debounce.Start();
    }

    private void OnDebounce(object? sender, EventArgs e)
    {
        _debounce?.Stop();
        TryApplyPending(interactive: true);
    }

    /// <summary>
    /// Writes every pending edit to the upstream file (validated, backed up, read back). Edits that cannot be
    /// written now because a task is running stay queued and are retried every few seconds.
    /// Returns true when nothing is left pending.
    /// </summary>
    public bool TryApplyPending(bool interactive)
    {
        if (_snapshot is null || _applying) return PendingCount == 0;
        var values = Parameters.Where(p => p.IsChanged).ToDictionary(p => p.Path, p => p.Current);
        if (values.Count == 0) return true;
        _applying = true;
        try
        {
            if (_accountSettings is not null && _selectedAccount() is { } account
                && account.Id != Guid.Parse("00000000-0000-4000-8000-000000000002"))
            {
                if (_isRunning()) { Status = "任务运行中，账号设置暂不写入。"; return false; }
                var accountProposal = _control.ProposeChanges(_snapshot, values);
                var accountRisky = accountProposal.Changes.Where(ParameterRules.RequiresConfirmation).ToList();
                if (accountRisky.Count > 0)
                {
                    if (!interactive) return false;
                    if (MessageBox.Show("所选设置可能消耗不可退回的资源或分解遗器。确认保存到此账号档案？",
                            "VIvian · 确认账号设置", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    { Status = "已取消保存，账号设置保持原值。"; return false; }
                }
                var merged = new Dictionary<string, string>(_accountSettings.Values);
                foreach (var (key, value) in values) merged[key] = value;
                _accountSettings = Task.Run(() => _accounts.SaveAccountSettingsAsync(_accountSettings with { Values = merged }))
                    .GetAwaiter().GetResult();
                _snapshot = OverlayAccount(_snapshot, _accountSettings);
                Show(_snapshot, null);
                Status = "已保存所选账号的独立设置；尚未写入原项目。";
                return true;
            }
            var proposal = _control.ProposeChanges(_snapshot, values);
            if (proposal.Changes.Count == 0) return true;
            var check = _control.ValidateChanges(proposal, _isRunning());
            if (!check.Valid)
            {
                Status = check.Deferred ? $"{values.Count} 项修改已排队：{check.Reason}" : check.Reason;
                return false;
            }
            string Title(string path) => Parameters.FirstOrDefault(p => p.Path == path)?.Title ?? path;
            var risky = proposal.Changes.Where(ParameterRules.RequiresConfirmation).ToList();
            if (risky.Count > 0)
            {
                if (!interactive) return false;
                var text = string.Join("\n", risky.Select(c => $"· {Title(c.Path)}：{c.OldValue} → {c.NewValue}"));
                if (MessageBox.Show("这些修改会消耗不可退回的资源或分解遗器：\n\n" + text + "\n\n确认写入星铁助手？", "VIvian · 确认修改",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    foreach (var row in Parameters.Where(p => risky.Any(r => r.Path == p.Path))) row.Current = row.Original;
                    Status = "已取消，这些项保持原值。";
                    return PendingCount == 0;
                }
            }
            var result = _control.ApplyChanges(proposal, _auditDirectory, _isRunning());
            _snapshot = result.Snapshot;
            Show(result.Snapshot, null);
            var note = check.Reason.Contains("提示：", StringComparison.Ordinal) ? " " + check.Reason[check.Reason.IndexOf("提示：", StringComparison.Ordinal)..] : "";
            Status = $"已写入星铁助手并读回核对（{DateTime.Now:HH:mm:ss}）：" +
                string.Join("、", proposal.Changes.Select(c => $"{Title(c.Path)} → {c.NewValue}")) + "。" + note;
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            Status = "没有写入：" + ex.Message;
            return false;
        }
        finally { _applying = false; Raise(nameof(PendingCount)); }
    }

    private System.Windows.Threading.DispatcherTimer? _externalDebounce;
    private int _externalRetries;

    /// <summary>Saves arrive as bursts of change events (and a half-written file in between): wait until the file
    /// has been quiet briefly, then read once.</summary>
    private void OnExternalChange(object sender, FileSystemEventArgs e) =>
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _externalRetries = 0;
            _externalDebounce ??= CreateExternalDebounce();
            _externalDebounce.Stop();
            _externalDebounce.Start();
        });

    private System.Windows.Threading.DispatcherTimer CreateExternalDebounce()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!Reconcile() && ++_externalRetries < 6) timer.Start(); // still being written: try again shortly
        };
        return timer;
    }

    /// <summary>
    /// Brings the page in line with the real file after the original software changed it. Untouched rows show
    /// the new values; pending edits are kept. An edit whose field was also changed elsewhere is reported.
    /// </summary>
    public bool Reconcile()
    {
        if (_snapshot is null || _applying) return true;
        if (_accountSettings is not null && _selectedAccount()?.Id != Guid.Parse("00000000-0000-4000-8000-000000000002"))
            return true; // The selected account's stored values must never be replaced by another account's live file.
        ConfigurationSnapshot latest;
        try { latest = _control.ReadConfiguration(_game, _installPath(_game)); }
        catch (Exception ex) { Status = "原软件修改了配置，重新读取失败：" + ex.Message; return false; }
        if (latest.Revision == _snapshot.Revision) return true;
        var changed = latest.Values.Where(v => _snapshot.Values.SingleOrDefault(o => o.Schema.Path == v.Schema.Path)?.Value != v.Value)
            .Select(v => Describe(v)).ToList();
        var edits = Parameters.Where(p => p.IsChanged).ToDictionary(p => p.Path, p => (p.Original, p.Current));
        var conflicts = edits.Where(e => latest.Values.SingleOrDefault(v => v.Schema.Path == e.Key)?.Value != e.Value.Original)
            .Select(e => Parameters.First(p => p.Path == e.Key).Title).ToList();
        if (conflicts.Count > 0)
        {
            Status = $"原软件也改了“{string.Join("”“", conflicts)}”。点“重新读取”查看最新值后再改；不会互相覆盖。";
            return true;
        }
        _snapshot = latest;
        Show(latest, edits.ToDictionary(e => e.Key, e => e.Value.Current));
        var what = changed.Count == 0 ? "" : "：" + string.Join("；", changed.Take(4)) + (changed.Count > 4 ? $" 等 {changed.Count} 项" : "");
        Status = $"已从星铁助手同步（{DateTime.Now:HH:mm:ss}）" + what
            + (edits.Count > 0 ? $"。{edits.Count} 项修改等待写入。" : "。");
        return true;
    }

    private static string Describe(ConfigurationValue value)
    {
        var raw = value.Value ?? "（空）";
        var shown = raw.Length > 24 ? raw[..24] + "…" : raw;
        shown = shown switch { "true" => "开", "false" => "关", _ => shown };
        return $"{value.Schema.Title} → {shown}";
    }

    private void OpenSettings()
    {
        var path = _snapshot?.Path;
        if (path is null || !File.Exists(path)) { Status = "请先读取配置。"; return; }
        Clipboard.SetText(path);
        Status = "已复制星铁助手设置文件路径。";
    }
    private void Error(Exception ex) => Status = ex.Message;
    public void Dispose() { _watcher?.Dispose(); _timer?.Stop(); _debounce?.Stop(); }
}
