using System.Collections.ObjectModel;
using VIvian.Core;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Engines.OneDragon;
using VIvian.Infrastructure.Processes;

namespace VIvian.App;

public sealed partial class AccountViewModel
{
    private Func<string> _oneDragonRoot = () => "";
    private Func<Func<Task>, Task> _serializeZenless = action => action();
    private string _zenlessProfileDirectory = "";
    private GameAccount? _selectedZenless;
    private OneDragonSourceProfile? _zenlessSource;
    private OneDragonProfileBinding? _zenlessBinding;
    private string _zenlessName = "";
    private string _zenlessMasked = "";
    private bool _zenlessAutoRun;
    private bool _zenlessTaskEnabled;
    private int? _zenlessInstance;
    private OneDragonSourceTask? _zenlessTask;
    private long _zenlessRead;

    public GameAccount? SelectedZenless
    {
        get => _selectedZenless;
        set
        {
            if (_selectedZenless == value) return;
            Set(ref _selectedZenless, value);
            _zenlessRead++;
            ZenlessName = value?.Name ?? "";
            ZenlessAutoRun = value?.AutoRun ?? false;
            ZenlessMasked = "";
            ZenlessInstance = null;
            _zenlessSource = null;
            _zenlessBinding = null;
            ZenlessTasks.Clear();
            SelectedZenlessTask = null;
            if (value is not null) _ = RefreshZenlessSourceAsync();
        }
    }

    public string ZenlessName { get => _zenlessName; set => Set(ref _zenlessName, value); }
    public string ZenlessMasked { get => _zenlessMasked; set => Set(ref _zenlessMasked, value); }
    public bool ZenlessAutoRun { get => _zenlessAutoRun; set => Set(ref _zenlessAutoRun, value); }
    public int? ZenlessInstance { get => _zenlessInstance; set => Set(ref _zenlessInstance, value); }
    public bool ZenlessTaskEnabled { get => _zenlessTaskEnabled; set => Set(ref _zenlessTaskEnabled, value); }
    public OneDragonSourceTask? SelectedZenlessTask
    {
        get => _zenlessTask;
        set { Set(ref _zenlessTask, value); ZenlessTaskEnabled = value?.Enabled ?? false; }
    }
    public ObservableCollection<int> ZenlessInstances { get; } = [];
    public ObservableCollection<OneDragonSourceTask> ZenlessTasks { get; } = [];
    public IReadOnlyList<GameAccount> SelectedZenlessForDaily => ZenlessRows
        .Where(a => a.AutoRun && a.Enabled && !a.IsLegacyPlaceholder)
        .OrderBy(a => a.Order).ThenBy(a => a.Id).ToArray();

    public AsyncCommand AddZenlessCommand { get; private set; } = null!;
    public AsyncCommand SaveZenlessCommand { get; private set; } = null!;
    public AsyncCommand MoveZenlessUpCommand { get; private set; } = null!;
    public AsyncCommand MoveZenlessDownCommand { get; private set; } = null!;
    public AsyncCommand SaveZenlessTaskCommand { get; private set; } = null!;
    public AsyncCommand OpenZenlessSourceCommand { get; private set; } = null!;

    private void InitializeZenlessCommands()
    {
        AsyncCommand Command(Func<Task> task) => new(() => _serializeZenless(task), ex => ZenlessStatus = ex.Message);
        AddZenlessCommand = Command(async () =>
        {
            var account = await _database.AddAccountAsync(GameId.ZenlessZoneZero, "新绝区零账号", "国服", null);
            await ReloadZenlessAsync();
            SelectedZenless = ZenlessRows.Single(row => row.Id == account.Id);
        });
        SaveZenlessCommand = Command(SaveZenlessAsync);
        MoveZenlessUpCommand = Command(() => MoveZenlessAsync(-1));
        MoveZenlessDownCommand = Command(() => MoveZenlessAsync(1));
        SaveZenlessTaskCommand = Command(async () =>
        {
            if (_zenlessBinding is not { } binding || _zenlessSource is not { } source || SelectedZenlessTask is not { CanEdit: true } task)
                throw new InvalidOperationException("请先绑定 OneDragon 实例，并选择可修改的任务。");
            if (!string.Equals(binding.Root, RequireZenlessRoot(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("OneDragon 安装路径已经更改，请重新核对绑定。");
            using var lease = DesktopOperationLease.Acquire(_zenlessProfileDirectory);
            await OneDragonSourceProfile.SaveTaskAsync(binding, source.Revision, task.Id, ZenlessTaskEnabled);
            await RefreshZenlessSourceAsync();
            ZenlessStatus = "已保存并读回 OneDragon 源档案。";
        });
        OpenZenlessSourceCommand = Command(async () =>
        {
            var root = RequireZenlessRoot();
            using var lease = DesktopOperationLease.Acquire(_zenlessProfileDirectory);
            await OneDragonAccountBatch.ValidateInstallationAsync(root);
            if (new OneDragonProcessProbe().CountRunning(System.IO.Path.Combine(root, OneDragonContract.EntryProgram)) > 0)
                throw new InvalidOperationException("OneDragon 已打开，请切换到现有窗口。");
            WindowsProcessSupervisor.OpenUserInterface(root, OneDragonContract.EntryProgram);
            ZenlessStatus = ZenlessInstance is { } instance
                ? $"请在 OneDragon 选择实例 {instance:D2} 编辑详细任务。关闭后 Vivian 会重新读取源档案。"
                : "请在 OneDragon 创建或选择实例，设置游戏路径和应用组。";
        });
    }

    internal void ConfigureZenless(Func<string> root, string profileDirectory, Func<Func<Task>, Task> serialize)
    {
        _oneDragonRoot = root;
        _zenlessProfileDirectory = profileDirectory;
        _serializeZenless = serialize;
    }

    internal async Task RefreshZenlessSourceAsync(bool keepDraft = false)
    {
        var read = ++_zenlessRead;
        try
        {
            var account = SelectedZenless;
            var root = RequireZenlessRoot();
            var catalog = await OneDragonInstanceCatalog.ReadAsync(root);
            if (read != _zenlessRead) return;
            var prior = ZenlessInstance;
            if (!ZenlessInstances.SequenceEqual(catalog.Instances.Select(row => row.Index)))
            {
                ZenlessInstances.Clear();
                foreach (var row in catalog.Instances) ZenlessInstances.Add(row.Index);
                ZenlessInstance = prior;
            }
            if (account is null)
            {
                ZenlessStatus = "在 OneDragon 保存实例后，在此新增或选中 Vivian 账号进行绑定。";
                return;
            }
            var settings = await _database.LoadAccountSettingsAsync(account.Id);
            var profile = (await _database.LoadLoginProfilesAsync(GameId.ZenlessZoneZero)).SingleOrDefault(row => row.AccountId == account.Id);
            if (read != _zenlessRead) return;
            var binding = OneDragonProfileBinding.FromSettings(settings.Values);
            if (binding is null || !string.Equals(root, binding.Root, StringComparison.OrdinalIgnoreCase))
            {
                ZenlessStatus = "请选中 OneDragon 实例，填写登录确认卡片显示的脱敏手机号，然后保存。";
                return;
            }
            if (!keepDraft)
            {
                ZenlessInstance = binding.Instance;
                ZenlessMasked = profile?.MaskedPhone ?? "";
            }
            var source = await OneDragonSourceProfile.ReadAsync(binding);
            if (read != _zenlessRead) return;
            _zenlessBinding = binding;
            if (_zenlessSource?.Revision != source.Revision || !keepDraft)
            {
                var selected = SelectedZenlessTask?.Id;
                ZenlessTasks.Clear();
                foreach (var task in source.Tasks) ZenlessTasks.Add(task);
                SelectedZenlessTask = ZenlessTasks.FirstOrDefault(task => task.Id == selected)
                    ?? ZenlessTasks.FirstOrDefault(task => task.CanEdit);
            }
            _zenlessSource = source;
            ZenlessStatus = $"OneDragon 实例 {binding.Instance:D2} · {source.Tasks.Count(task => task.Enabled)} 项启用 · 源档案已读回";
        }
        catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException
            or TimeoutException or Microsoft.Data.Sqlite.SqliteException or System.Text.DecoderFallbackException)
        {
            if (read != _zenlessRead) return;
            _zenlessBinding = null;
            _zenlessSource = null;
            ZenlessTasks.Clear();
            SelectedZenlessTask = null;
            ZenlessStatus = ex.Message;
        }
    }

    private string RequireZenlessRoot()
    {
        var root = _oneDragonRoot();
        if (!System.IO.Path.IsPathFullyQualified(root))
            throw new InvalidOperationException("请先在引擎与环境保存 OneDragon 安装目录。");
        return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
    }

    private async Task SaveZenlessAsync()
    {
        var selected = SelectedZenless ?? throw new InvalidOperationException("请先选择或新增一个绝区零账号。");
        var root = RequireZenlessRoot();
        var id = ZenlessInstance;
        var masked = ZenlessMasked;
        var catalog = await OneDragonInstanceCatalog.ReadAsync(root);
        if (id is null || !catalog.Instances.Any(row => row.Index == id))
            throw new InvalidOperationException("请在 OneDragon 创建并选择对应的源档案。");
        if (string.IsNullOrWhiteSpace(ZenlessName)) throw new InvalidOperationException("请输入账号显示名称。");
        await _database.SaveOneDragonBindingAsync(selected with { Name = ZenlessName.Trim(), AutoRun = ZenlessAutoRun },
            new(root, id.Value), masked);
        await ReloadZenlessAsync();
    }

    private async Task MoveZenlessAsync(int direction)
    {
        if (SelectedZenless is not { } selected) return;
        var index = ZenlessRows.IndexOf(selected) + direction;
        if (index < 0 || index >= ZenlessRows.Count) return;
        await _database.SwapAccountOrderAsync(selected, ZenlessRows[index]);
        await ReloadZenlessAsync();
    }
}
