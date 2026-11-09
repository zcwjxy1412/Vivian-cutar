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

/// <summary>Edits one existing BetterGI source profile, independently of VIvian's task plan.</summary>
public sealed class GenshinSourceConfigurationViewModel : ObservableObject
{
    private readonly string _bindingPath;
    private readonly Func<bool> _hostIdle;
    private readonly DispatcherTimer _refreshTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _saveTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(900) };
    private Guid? _accountId;
    private Guid? _restoreAttempt;
    private BetterGiSourceSettings? _binding;
    private BetterGiSourceSettingsSnapshot? _snapshot;
    private string _installRoot = "";
    private string _profileName = "";
    private string _status = "请选择原神账号。";
    private bool _polling;
    private bool _busy;
    private bool _readFailed;
    private bool _conflicted;

    public GenshinSourceConfigurationViewModel(string profileDirectory, Func<bool> hostIdle)
    {
        _bindingPath = Path.Combine(profileDirectory, "genshin-source-bindings.json");
        _hostIdle = hostIdle;
        BindCommand = new(BindAsync, ReportError);
        ReloadCommand = new(ReloadAsync, ReportError);
        SaveCommand = new(SaveAsync, ReportError);
        OpenCommand = new(OpenAsync, ReportError);
        _refreshTimer.Tick += async (_, _) => await PollAsync();
        _saveTimer.Tick += async (_, _) =>
        {
            _saveTimer.Stop();
            await SaveAsync();
        };
    }

    public ObservableCollection<ParameterRow> ProfileRows { get; } = [];
    public ObservableCollection<ParameterRow> SharedRows { get; } = [];
    public AsyncCommand BindCommand { get; }
    public AsyncCommand ReloadCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand OpenCommand { get; }
    public string InstallRoot { get => _installRoot; set => Set(ref _installRoot, value); }
    public string ProfileName { get => _profileName; set => Set(ref _profileName, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string BindingLabel => _binding is { } binding ? $"已绑定「{binding.ProfileName}」 · {binding.InstallRoot}" : "尚未读取已绑定档案";
    public bool CanBind => _accountId is not null && !_busy;
    public bool CanOpen => _binding is not null && !_busy;
    public bool CanSave => CanOpen && !_readFailed && !_conflicted && HasDraft;
    public bool HasDraft => ProfileRows.Any(row => row.IsChanged);
    public bool IsIdle => !_busy;
    public string? BoundInstallRoot => _binding?.InstallRoot;
    internal event Action? FieldsChanged;
    internal bool Manages(string path) => ProfileRows.Any(row => row.Path == path);

    internal void SetAccount(Guid? accountId)
    {
        if (_accountId == accountId) return;
        _saveTimer.Stop();
        _accountId = accountId;
        _restoreAttempt = null;
        _binding = null;
        InstallRoot = ProfileName = "";
        Show(null);
        Status = accountId is null ? "请选择原神账号。" : "请选择安装目录并填写已存在的一条龙配置名称。";
        UpdateAvailability();
        if (_refreshTimer.IsEnabled) _ = RestoreBindingAsync();
    }

    internal void SetVisible(bool visible)
    {
        if (!visible) { _refreshTimer.Stop(); _saveTimer.Stop(); return; }
        _refreshTimer.Start();
        if (_binding is null) _ = RestoreBindingAsync();
        else _ = PollAsync();
    }

    private Task RestoreBindingAsync() => RunAsync(async () =>
    {
        if (_accountId is not { } id) return;
        _restoreAttempt = id;
        if (!(await LoadBindingsAsync()).TryGetValue(id, out var saved) || _accountId != id) return;
        InstallRoot = saved.InstallRoot;
        ProfileName = saved.ProfileName;
        await LoadBindingAsync(id, saved.InstallRoot, saved.ProfileName, persist: false);
    });

    private Task BindAsync() => RunAsync(async () =>
    {
        if (!_hostIdle() || _accountId is not { } id || !ConfirmDiscard()) return;
        await LoadBindingAsync(id, InstallRoot.Trim(), ProfileName.Trim(), persist: true);
    });

    private async Task LoadBindingAsync(Guid id, string installRoot, string profileName, bool persist)
    {
        var binding = await BetterGiSourceSettings.BindAsync(installRoot, profileName);
        var snapshot = await binding.ReadAsync();
        if (_accountId != id) return;
        if (persist)
        {
            var saved = await LoadBindingsAsync();
            if (_accountId != id) return;
            if (saved.Any(entry => entry.Key != id
                && string.Equals(entry.Value.InstallRoot, binding.InstallRoot, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Value.ProfileName, binding.ProfileName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("此 BetterGI 源档案已绑定其他 VIvian 原神账号。");
            saved[id] = new(binding.InstallRoot, binding.ProfileName);
            var temp = _bindingPath + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(saved));
            File.Move(temp, _bindingPath, overwrite: true);
        }
        if (_accountId != id) return;
        _binding = binding;
        InstallRoot = binding.InstallRoot;
        ProfileName = binding.ProfileName;
        Show(snapshot);
        Status = "已读取源档案。此页打开时会定期检查 BetterGI 的外部修改。";
    }

    private Task ReloadAsync() => _binding is null ? RestoreBindingAsync() : RunAsync(async () =>
    {
        if (_binding is not { } binding || !ConfirmDiscard()) return;
        var latest = await binding.ReadAsync();
        if (!ReferenceEquals(binding, _binding)) return;
        Show(latest);
        Status = "已重新读取 BetterGI 源档案。";
    });

    internal Task SaveAsync() => RunAsync(async () =>
    {
        _saveTimer.Stop();
        if (_binding is not { } binding || _snapshot is not { } snapshot || _readFailed || _conflicted) return;
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
        Status = $"已写入并读回核对「{binding.ProfileName}」 · {DateTime.Now:HH:mm:ss}";
    });

    private Task OpenAsync() => RunAsync(async () =>
    {
        if (!_hostIdle() || _binding is not { } binding) return;
        await binding.ReadAsync();
        if (!_hostIdle() || !ReferenceEquals(binding, _binding)) return;
        if (WindowsProcessSupervisor.IsCurrentProcessElevated())
        {
            WindowsProcessSupervisor.RevealExecutable(binding.ProgramPath);
            Status = $"已定位 BetterGI。请打开所选程序，在「一条龙」选择「{binding.ProfileName}」。";
            return;
        }
        WindowsProcessSupervisor.OpenUserInterface(binding.InstallRoot, BetterGiContract.ProgramName);
        Status = $"请在 BetterGI 的「一条龙」选择「{binding.ProfileName}」。";
    });

    private async Task PollAsync()
    {
        if (!_busy && _binding is null && _accountId != _restoreAttempt)
        {
            await RestoreBindingAsync();
            return;
        }
        if (_busy || _polling || _binding is not { } binding || _snapshot is not { } baseline) return;
        _polling = true;
        try
        {
            var latest = await binding.ReadAsync();
            if (_busy || !ReferenceEquals(binding, _binding) || !ReferenceEquals(baseline, _snapshot)) return;
            var changed = latest.ProfileRevision != baseline.ProfileRevision;
            if (changed && HasDraft)
            {
                _conflicted = true;
                Status = "BetterGI 源档案已在外部修改。草稿已保留，请核对后重新读取。";
            }
            else if (!_conflicted && (changed || _readFailed))
            {
                if (changed) Show(latest);
                Status = "已读回 BetterGI 源档案。";
            }
            if (latest.SharedDomainRevision != baseline.SharedDomainRevision)
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

    internal bool ConfirmDiscard() => !HasDraft ||
        MessageBox.Show("继续会丢弃当前未写入 BetterGI 的草稿。", "BetterGI 源档案", MessageBoxButton.YesNo,
            MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private async Task<Dictionary<Guid, SourceBinding>> LoadBindingsAsync()
    {
        try
        {
            await using var file = File.OpenRead(_bindingPath);
            return await JsonSerializer.DeserializeAsync<Dictionary<Guid, SourceBinding>>(file)
                ?? throw new InvalidDataException("原神源档案绑定文件无效。");
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
        Raise(nameof(CanBind));
        Raise(nameof(CanOpen));
        Raise(nameof(CanSave));
        Raise(nameof(BindingLabel));
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

    private sealed record SourceBinding(string InstallRoot, string ProfileName);
}
