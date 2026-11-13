using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using VIvian.Core.Engines;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Engines.BetterGi;
using VIvian.Infrastructure.Processes;

namespace VIvian.App;

/// <summary>Resolves and edits the selected account's BetterGI fields.</summary>
public sealed class GenshinSourceConfigurationViewModel : ObservableObject
{
    private readonly string _bindingPath;
    private readonly Func<bool> _hostIdle;
    private readonly Func<bool> _editorIdle;
    private readonly Func<string> _installRoot;
    private readonly Func<Func<Task>, Task> _serializeWrite;
    private readonly DispatcherTimer _refreshTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _saveTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(900) };
    private Guid? _accountId;
    private Guid? _restoreAttempt;
    private BetterGiSourceSettings? _binding;
    private BetterGiSourceSettingsSnapshot? _snapshot;
    private string _lookupRoot = "";
    private string _status = "请选择原神账号。";
    private string _taskOrder = "";
    private bool _canConnect;
    private bool _polling;
    private bool _busy;
    private bool _readFailed;
    private bool _conflicted;

    public GenshinSourceConfigurationViewModel(string profileDirectory, Func<bool> hostIdle, Func<bool> editorIdle,
        Func<string> installRoot, Func<Func<Task>, Task> serializeWrite)
    {
        _bindingPath = Path.Combine(profileDirectory, "genshin-source-bindings.json");
        _hostIdle = hostIdle;
        _editorIdle = editorIdle;
        _installRoot = installRoot;
        _serializeWrite = serializeWrite;
        OpenCommand = new(OpenAsync, ReportError);
        _refreshTimer.Tick += async (_, _) => await PollAsync();
        _saveTimer.Tick += async (_, _) =>
        {
            _saveTimer.Stop();
            if (_editorIdle()) await SaveAsync();
        };
    }

    public ObservableCollection<ParameterRow> ProfileRows { get; } = [];
    public ObservableCollection<ParameterRow> SharedRows { get; } = [];
    public AsyncCommand OpenCommand { get; }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string TaskOrder { get => _taskOrder; private set => Set(ref _taskOrder, value); }
    public bool CanConnect => _canConnect && !_busy && IsCurrentRoot(_lookupRoot);
    public bool HasBinding => _binding is not null;
    public bool CanOpen => !_busy && _binding is { } binding && IsCurrentRoot(binding.InstallRoot);
    public bool CanSave => CanOpen && !_readFailed && !_conflicted && HasDraft;
    public bool HasDraft => ProfileRows.Any(row => row.IsChanged);
    public bool IsIdle => !_busy;
    internal event Action? FieldsChanged;
    internal bool Manages(string path) => ProfileRows.Any(row => row.Path == path);

    internal void SetAccount(Guid? accountId)
    {
        if (_accountId == accountId) return;
        _saveTimer.Stop();
        _accountId = accountId;
        _restoreAttempt = null;
        _binding = null;
        _canConnect = false;
        TaskOrder = "";
        Show(null);
        Status = accountId is null ? "请选择原神账号。" : "等待核对账号的 BetterGI 档案。";
        UpdateAvailability();
        if (_refreshTimer.IsEnabled) _ = RestoreBindingAsync();
    }

    internal void SetVisible(bool visible)
    {
        if (!visible) { _refreshTimer.Stop(); _saveTimer.Stop(); return; }
        _refreshTimer.Start();
        _ = PollAsync();
    }

    private Task RestoreBindingAsync() => RunAsync(async () =>
    {
        if (_accountId is not { } id) return;
        _restoreAttempt = id;
        var root = _lookupRoot = _installRoot();
        _canConnect = false;
        _readFailed = true;
        BetterGiSourceSettings? binding = null;
        if (root.Length > 0)
        {
            binding = await BetterGiSourceSettings.FindAccountProfileAsync(root, id);
            if (binding is null)
            {
                var saved = await LoadBindingsAsync();
                if (saved.TryGetValue(id, out var old) && SameRoot(old.InstallRoot, root))
                {
                    if (old.ProfileName.StartsWith("VIvian ", StringComparison.OrdinalIgnoreCase) &&
                        Guid.TryParseExact(old.ProfileName.AsSpan(7), "N", out var owner) && owner != id)
                        throw new InvalidDataException("旧绑定指向另一个原神账号的专属 BetterGI 档案。");
                    if (saved.Any(entry => entry.Key != id && SameRoot(entry.Value.InstallRoot, root) &&
                        string.Equals(entry.Value.ProfileName, old.ProfileName, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("多个原神账号曾绑定同一 BetterGI 档案，请核对旧绑定。");
                    binding = await BetterGiSourceSettings.BindAsync(root, old.ProfileName);
                }
            }
        }
        if (_accountId != id || !IsCurrentRoot(root)) return;
        if (binding is not null)
        {
            await ShowBindingAsync(id, binding);
            return;
        }
        _binding = null;
        TaskOrder = "";
        Show(null);
        _canConnect = root.Length > 0;
        Status = _canConnect ? "此账号尚未连接 BetterGI。保存任务设置后可连接。"
            : "请先在「引擎与环境」保存 BetterGI 安装目录。";
    });

    internal Task ReloadAsync() => RestoreBindingAsync();

    internal Task ConnectAsync(Guid id, IReadOnlyDictionary<string, string> savedSettings, CancellationToken ct)
    {
        if (!CanConnect || _accountId != id) return Task.CompletedTask;
        return RunAsync(() => _serializeWrite(async () =>
        {
            if (!_hostIdle() || !IsCurrentRoot(_lookupRoot))
            {
                Status = "任务运行、恢复待确认或安装路径已变化。请核对后重试。";
                return;
            }
            var binding = await BetterGiSourceSettings.ConnectAccountProfileAsync(_lookupRoot, id, savedSettings, ct);
            await ShowBindingAsync(id, binding);
        }));
    }

    private async Task ShowBindingAsync(Guid id, BetterGiSourceSettings binding)
    {
        var snapshot = await binding.ReadAsync();
        if (_accountId != id || !IsCurrentRoot(binding.InstallRoot)) return;
        _binding = binding;
        _canConnect = false;
        Show(snapshot);
        Status = "已连接此账号的 BetterGI 档案。";
        await RefreshTaskOrderAsync(binding, snapshot);
    }

    internal Task SaveAsync() => RunAsync(() => _serializeWrite(async () =>
    {
        _saveTimer.Stop();
        if (_binding is not { } binding || _snapshot is not { } snapshot || !HasDraft) return;
        if (!IsCurrentRoot(binding.InstallRoot) || _readFailed || _conflicted)
        {
            Status = "BetterGI 档案暂不能写入。请重新读取后核对保留的草稿。";
            return;
        }
        if (!_hostIdle())
        {
            Status = "任务运行或恢复待确认。草稿已保留，可以稍后重试写入。";
            return;
        }
        var changes = ProfileRows.Where(row => row.IsChanged).ToDictionary(row => row.Path, row => row.Current);
        if (changes.Count == 0) return;
        var updated = await binding.UpdateProfileAsync(snapshot.ProfileRevision, changes);
        if (!ReferenceEquals(binding, _binding)) return;
        Show(updated);
        Status = $"BetterGI 已写入并读回 · {DateTime.Now:HH:mm:ss}";
        await RefreshTaskOrderAsync(binding, updated);
    }));

    private Task OpenAsync() => RunAsync(async () =>
    {
        if (!_hostIdle() || _binding is not { } binding || !IsCurrentRoot(binding.InstallRoot)) return;
        if (HasDraft || !_editorIdle())
        {
            Status = "请先保存当前修改，再打开 BetterGI 详细设置。";
            return;
        }
        await binding.ReadAsync();
        if (!_hostIdle() || !ReferenceEquals(binding, _binding) || !IsCurrentRoot(binding.InstallRoot)) return;
        using var desktop = DesktopOperationLease.Acquire(Path.GetDirectoryName(_bindingPath)!);
        WindowsProcessSupervisor.OpenUserInterface(binding.InstallRoot, BetterGiContract.ProgramName);
        Status = $"已打开 BetterGI。请在「一条龙」选择「{binding.ProfileName}」调整队伍等详细设置；保存后本页自动读回。";
    });

    private async Task PollAsync()
    {
        if (_busy || _polling || !_editorIdle()) return;
        if (_accountId is not null && (_accountId != _restoreAttempt || !IsCurrentRoot(_lookupRoot)))
        {
            if (HasDraft)
            {
                _conflicted = true;
                Status = "BetterGI 安装路径已变化。草稿已保留，请核对后重新读取。";
                UpdateAvailability();
                return;
            }
            await RestoreBindingAsync();
            return;
        }
        if (_binding is not { } binding || _snapshot is not { } baseline) return;
        _polling = true;
        try
        {
            var latest = await binding.ReadAsync();
            if (_busy || !_editorIdle() || !IsCurrentRoot(binding.InstallRoot) ||
                !ReferenceEquals(binding, _binding) || !ReferenceEquals(baseline, _snapshot)) return;
            var changed = latest.ProfileRevision != baseline.ProfileRevision;
            if (changed && HasDraft)
            {
                _conflicted = true;
                Status = "BetterGI 源档案已在外部修改。草稿已保留，请核对后重新读取。";
            }
            else if (!_conflicted && (changed || _readFailed))
            {
                if (changed)
                {
                    Show(latest);
                    await RefreshTaskOrderAsync(binding, latest);
                    if (!ReferenceEquals(binding, _binding) || !ReferenceEquals(latest, _snapshot)) return;
                }
                Status = "已读回 BetterGI 源档案。";
            }
            if (latest.SharedDomainRevision != _snapshot?.SharedDomainRevision)
            {
                ShowShared(latest);
                _snapshot = _snapshot! with { SharedDomainRevision = latest.SharedDomainRevision };
            }
            _readFailed = false;
            UpdateAvailability();
        }
        catch (Exception ex) when (IsSourceError(ex))
        {
            if (_busy || !ReferenceEquals(binding, _binding) || !ReferenceEquals(baseline, _snapshot)) return;
            _readFailed = true;
            Status = "暂时无法读取 BetterGI 源档案。待恢复后会自动重试，草稿保留。";
            UpdateAvailability();
        }
        finally { _polling = false; }
    }

    private async Task RefreshTaskOrderAsync(BetterGiSourceSettings binding, BetterGiSourceSettingsSnapshot snapshot)
    {
        string order;
        try
        {
            var plan = await BetterGiConfiguration.InspectOneDragonAsync(binding.InstallRoot, binding.ProfileName);
            order = plan.EnabledTasks.Count == 0 ? "一条龙档案尚未启用任务。"
                : "BetterGI 任务顺序 · " + string.Join(" → ", plan.EnabledTasks);
        }
        catch (Exception ex) when (IsSourceError(ex))
        {
            order = "暂时无法核对 BetterGI 任务顺序。";
        }
        if (ReferenceEquals(binding, _binding) && ReferenceEquals(snapshot, _snapshot)) TaskOrder = order;
    }

    private void Show(BetterGiSourceSettingsSnapshot? snapshot)
    {
        _snapshot = snapshot;
        _conflicted = _readFailed = false;
        foreach (var row in ProfileRows) row.PropertyChanged -= OnDraftChanged;
        ProfileRows.Clear();
        if (snapshot is not null)
        {
            foreach (var schema in BetterGiAccountPlans.Parameters.Where(p => snapshot.ProfileValues.ContainsKey(p.Path)))
            {
                var readOnly = schema.Path == "autoDomain" || snapshot.WeeklyDomainEnabled && schema.Path is ("DomainName" or "PartyName");
                var field = schema with
                {
                    Description = readOnly ? "请在 BetterGI 中核对并调整秘境设置。" : "写入当前绑定的 BetterGI 一条龙档案。",
                    Scope = "BetterGI 一条龙档案",
                    Access = readOnly ? ParameterAccess.UpstreamManaged : ParameterAccess.VerifiedReadWrite,
                };
                var row = new ParameterRow(new(field, snapshot.ProfileValues[field.Path], readOnly ? "在原软件管理" : "可写入源档案"));
                row.PropertyChanged += OnDraftChanged;
                ProfileRows.Add(row);
            }
        }
        ShowShared(snapshot);
        UpdateAvailability();
        FieldsChanged?.Invoke();
    }

    private void ShowShared(BetterGiSourceSettingsSnapshot? snapshot)
    {
        SharedRows.Clear();
        if (snapshot is null) return;
        foreach (var schema in BetterGiTaskCatalog.Parameters.Where(p => snapshot.SharedDomainValues.ContainsKey(p.Path)))
            SharedRows.Add(new(new(schema, snapshot.SharedDomainValues[schema.Path], "安装级共享 · 只读")));
    }

    private void OnDraftChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ParameterRow.IsChanged)) return;
        Raise(nameof(CanSave));
        _saveTimer.Stop();
        if (_refreshTimer.IsEnabled && CanSave) _saveTimer.Start();
    }

    internal bool ConfirmDiscard()
    {
        if (!HasDraft) return true;
        _saveTimer.Stop();
        return MessageBox.Show("继续会丢弃当前未写入 BetterGI 的草稿。", "BetterGI 源档案", MessageBoxButton.YesNo,
            MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private async Task<Dictionary<Guid, SourceBinding>> LoadBindingsAsync()
    {
        try
        {
            await using var file = File.OpenRead(_bindingPath);
            var bindings = await JsonSerializer.DeserializeAsync<Dictionary<Guid, SourceBinding>>(file);
            if (bindings is null || bindings.Values.Any(binding => binding is null ||
                string.IsNullOrWhiteSpace(binding.InstallRoot) || string.IsNullOrWhiteSpace(binding.ProfileName)))
                throw new InvalidDataException("原神源档案绑定文件无效。");
            return bindings;
        }
        catch (FileNotFoundException) { return []; }
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        var id = _accountId;
        _busy = true;
        UpdateAvailability();
        try { await action(); }
        catch (Exception ex) when (IsSourceError(ex))
        {
            if (id == _accountId) ReportError(ex);
        }
        finally { _busy = false; UpdateAvailability(); }
    }

    private void UpdateAvailability()
    {
        Raise(nameof(IsIdle));
        Raise(nameof(CanConnect));
        Raise(nameof(HasBinding));
        Raise(nameof(CanOpen));
        Raise(nameof(CanSave));
    }

    private void ReportError(Exception ex)
    {
        if (ex is BetterGiSourceSettingsConflictException) _conflicted = true;
        Status = ex switch
        {
            BetterGiSourceSettingsConflictException or InvalidOperationException or InvalidDataException => ex.Message,
            TimeoutException => "读取 BetterGI 源档案超时，请稍后重试。",
            JsonException => "BetterGI 配置或本机绑定文件的格式无效。",
            Win32Exception => "暂时无法打开 BetterGI。",
            IOException { HResult: unchecked((int)0x80070020) } => "源档案正在被占用。草稿已保留，可以稍后重试写入。",
            _ => UserFacingError.Describe(ex),
        };
    }

    private static bool IsSourceError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or
        ArgumentException or InvalidOperationException or JsonException or TimeoutException or Win32Exception;

    private bool IsCurrentRoot(string root) => SameRoot(root, _installRoot());

    private static bool SameRoot(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(left), Path.TrimEndingDirectorySeparator(right), StringComparison.OrdinalIgnoreCase);

    private sealed record SourceBinding(string InstallRoot, string ProfileName);
}
