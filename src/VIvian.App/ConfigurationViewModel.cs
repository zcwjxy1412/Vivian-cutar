using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using VIvian.Core;
using VIvian.Core.Engines;

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
    private ConfigurationSnapshot? _snapshot;
    private FileSystemWatcher? _watcher;
    private System.Windows.Threading.DispatcherTimer? _timer;
    private GameId _game = GameId.StarRail;
    private string _status = "选择游戏后读取配置。";
    public string Status { get => _status; private set => Set(ref _status, value); }
    public ObservableCollection<ParameterRow> Parameters { get; } = [];
    public AsyncCommand ReadCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public ConfigurationViewModel(IConfigurationControl control, Func<GameId, string> installPath,
        Func<bool> isRunning, string auditDirectory)
    {
        _control = control; _installPath = installPath; _isRunning = isRunning; _auditDirectory = auditDirectory;
        ReadCommand = new(ReadAsync, Error);
        SaveCommand = new(SaveAsync, Error);
        OpenSettingsCommand = new(OpenSettings);
    }
    public void SelectGame(GameId game)
    {
        _game = game;
        _watcher?.Dispose(); _watcher = null;
        _snapshot = null; Parameters.Clear();
        Status = "已切换游戏；点击“读取上游配置”。";
    }
    public Task ReadAsync()
    {
        var path = _installPath(_game);
        if (string.IsNullOrWhiteSpace(path)) { Status = "请先在“引擎管理”填写安装目录。"; return Task.CompletedTask; }
        var snapshot = _control.ReadConfiguration(_game, path);
        _snapshot = snapshot;
        Parameters.Clear();
        foreach (var value in snapshot.Values) Parameters.Add(new(value));
        Status = $"已读取 {snapshot.PinnedVersion}；{snapshot.ReadAt.ToLocalTime():HH:mm:ss} 同步。";
        _watcher?.Dispose();
        if (File.Exists(snapshot.Path))
        {
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(snapshot.Path)!, Path.GetFileName(snapshot.Path))
            { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size, EnableRaisingEvents = true };
            _watcher.Changed += OnExternalChange;
            _watcher.Renamed += OnExternalChange;
        }
        if (_timer is null && Application.Current is not null)
        {
            _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _timer.Tick += (_, _) => Reconcile();
            _timer.Start();
        }
        return Task.CompletedTask;
    }
    private void OnExternalChange(object sender, FileSystemEventArgs e) =>
        Application.Current?.Dispatcher.BeginInvoke(Reconcile);

    /// <summary>
    /// Brings the page in line with the real file after the original software (or anything else) changed it.
    /// Rows the user has not touched show the new values; unsaved edits are kept. An edit whose field was also
    /// changed elsewhere is reported now, and saving it is refused until the user re-reads.
    /// Called by the file watcher and every few seconds (watchers can miss whole-file replacements).
    /// </summary>
    public void Reconcile()
    {
        if (_snapshot is null) return;
        ConfigurationSnapshot latest;
        try { latest = _control.ReadConfiguration(_game, _installPath(_game)); }
        catch (Exception ex) { Status = "原软件修改了配置，重新读取失败：" + ex.Message; return; }
        if (latest.Revision == _snapshot.Revision) return;
        var edits = Parameters.Where(p => p.IsChanged).ToDictionary(p => p.Path, p => (p.Original, p.Current));
        var conflicts = edits.Where(e => latest.Values.SingleOrDefault(v => v.Schema.Path == e.Key)?.Value != e.Value.Original)
            .Select(e => Parameters.First(p => p.Path == e.Key).Title).ToList();
        if (conflicts.Count > 0)
        {
            Status = $"原软件也改了你正在编辑的“{string.Join("”“", conflicts)}”。点“重新读取”查看最新值后再决定；保存会被拒绝，不会互相覆盖。";
            return;
        }
        _snapshot = latest;
        Parameters.Clear();
        foreach (var value in latest.Values)
        {
            var row = new ParameterRow(value);
            if (edits.TryGetValue(row.Path, out var edit)) row.Current = edit.Current;
            Parameters.Add(row);
        }
        Status = edits.Count > 0
            ? $"已同步原软件的修改（{DateTime.Now:HH:mm:ss}）；你未保存的 {edits.Count} 项改动仍保留。"
            : $"已同步原软件的修改（{DateTime.Now:HH:mm:ss}）。";
    }
    private Task SaveAsync()
    {
        if (_snapshot is null) { Status = "请先读取配置。"; return Task.CompletedTask; }
        var values = Parameters.Where(p => p.Editable && p.Current != p.Original)
            .ToDictionary(p => p.Path, p => p.Current);
        var proposal = _control.ProposeChanges(_snapshot, values);
        var running = _isRunning();
        var check = _control.ValidateChanges(proposal, running);
        if (!check.Valid) { Status = check.Reason; return Task.CompletedTask; }
        string Title(string path) => Parameters.FirstOrDefault(p => p.Path == path)?.Title ?? path;
        var diff = string.Join("\n", proposal.Changes.Select(c => $"· {Title(c.Path)}：{c.OldValue} → {c.NewValue}"));
        var note = check.Reason.Contains("提示：", StringComparison.Ordinal) ? "\n\n" + check.Reason[check.Reason.IndexOf("提示：", StringComparison.Ordinal)..] : "";
        if (MessageBox.Show("将写入星铁助手的配置：\n\n" + diff + note + "\n\n写入前自动备份，写入后读回核对。", "VIvian · 保存配置",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return Task.CompletedTask;
        running = _isRunning();
        var result = _control.ApplyChanges(proposal, _auditDirectory, running);
        _snapshot = result.Snapshot; Parameters.Clear();
        foreach (var value in result.Snapshot.Values) Parameters.Add(new(value));
        Status = $"已写入星铁助手的配置并读回核对（{proposal.Changes.Count} 项）。原软件开着时会自动刷新它的设置页。备份：{result.BackupPath}";
        return Task.CompletedTask;
    }
    private void OpenSettings()
    {
        var path = _snapshot?.Path;
        if (path is null || !File.Exists(path)) { Status = "请先读取配置。"; return; }
        Clipboard.SetText(path);
        Status = "已复制上游设置文件路径；请在原软件中打开设置，或将路径粘贴到文件管理器。";
    }
    private void Error(Exception ex) => Status = ex.Message;
    public void Dispose() { _watcher?.Dispose(); _timer?.Stop(); }
}
