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
    public string Current { get => _current; set => Set(ref _current, value); }
    public string State => value.State;
}

public sealed class ConfigurationViewModel : ObservableObject, IDisposable
{
    private readonly IConfigurationControl _control;
    private readonly Func<GameId, string> _installPath;
    private readonly Func<bool> _isRunning;
    private readonly string _auditDirectory;
    private ConfigurationSnapshot? _snapshot;
    private FileSystemWatcher? _watcher;
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
        return Task.CompletedTask;
    }
    private void OnExternalChange(object sender, FileSystemEventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_snapshot is null) return;
            try
            {
                var latest = _control.ReadConfiguration(_game, _installPath(_game));
                if (latest.Revision == _snapshot.Revision) return;
                if (Parameters.Any(p => p.Current != p.Original))
                    Status = "上游已修改配置，且你有未保存的改动。请核对差异并重新读取；保存已阻止静默覆盖。";
                else
                {
                    _snapshot = latest; Parameters.Clear();
                    foreach (var value in latest.Values) Parameters.Add(new(value));
                    Status = "已自动同步上游配置修改。";
                }
            }
            catch (Exception ex) { Status = "外部配置发生变化，重新读取失败：" + ex.Message; }
        });
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
        var diff = string.Join("\n", proposal.Changes.Select(c => $"{c.Path}: {c.OldValue} → {c.NewValue}"));
        if (MessageBox.Show("确认保存到上游配置？\n" + diff + "\n保存前将自动备份。", "VIvian 配置变更",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return Task.CompletedTask;
        running = _isRunning();
        var result = _control.ApplyChanges(proposal, _auditDirectory, running);
        _snapshot = result.Snapshot; Parameters.Clear();
        foreach (var value in result.Snapshot.Values) Parameters.Add(new(value));
        Status = "已保存、备份并重新读取验证；审计记录：" + result.AuditPath;
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
    public void Dispose() => _watcher?.Dispose();
}
