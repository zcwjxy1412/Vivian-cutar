using Microsoft.Win32;
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
    private int? _zenlessInstance;
    private long _zenlessRead;
    private string _zenlessReadiness = "请先导入登录截图。";
    private const string KnownZenlessClient = @"E:\ZenlessZoneZero Game\ZenlessZoneZero.exe";

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
            ClearZenlessTaskEditor();
            ZenlessReadiness = value is null ? "请先导入登录截图。" : "正在核对这个账号的运行配置…";
            Raise(nameof(HasZenlessAccount));
            if (value is not null) _ = RefreshZenlessSourceAsync();
        }
    }

    public string ZenlessName { get => _zenlessName; set => Set(ref _zenlessName, value); }
    public string ZenlessMasked { get => _zenlessMasked; set => Set(ref _zenlessMasked, value); }
    public bool ZenlessAutoRun { get => _zenlessAutoRun; set => Set(ref _zenlessAutoRun, value); }
    public int? ZenlessInstance { get => _zenlessInstance; set => Set(ref _zenlessInstance, value); }
    public string ZenlessReadiness { get => _zenlessReadiness; private set => Set(ref _zenlessReadiness, value); }
    public bool HasZenlessAccount => SelectedZenless is not null;
    public bool CanEditZenlessTasks => _zenlessSource is not null;
    public ObservableCollection<int> ZenlessInstances { get; } = [];
    public IReadOnlyList<ZenlessTaskOption> ZenlessTasks { get; } = OneDragonTaskPlans.Catalog
        .Select(task => new ZenlessTaskOption(task.Id, task.DisplayName)).ToArray();
    public IReadOnlyList<GameAccount> SelectedZenlessForDaily => ZenlessRows
        .Where(a => a.AutoRun && a.Enabled && !a.IsLegacyPlaceholder)
        .OrderBy(a => a.Order).ThenBy(a => a.Id).ToArray();

    public AsyncCommand ImportZenlessScreenshotCommand { get; private set; } = null!;
    public AsyncCommand AddZenlessCommand { get; private set; } = null!;
    public AsyncCommand DeleteZenlessCommand { get; private set; } = null!;
    public AsyncCommand PrepareZenlessCommand { get; private set; } = null!;
    public AsyncCommand SaveZenlessCommand { get; private set; } = null!;
    public AsyncCommand MoveZenlessUpCommand { get; private set; } = null!;
    public AsyncCommand MoveZenlessDownCommand { get; private set; } = null!;
    public AsyncCommand SaveZenlessTaskCommand { get; private set; } = null!;
    public AsyncCommand OpenZenlessSourceCommand { get; private set; } = null!;

    private void InitializeZenlessCommands()
    {
        AsyncCommand Command(Func<Task> task) => new(() => _serializeZenless(task), ex => ZenlessStatus = ex.Message);
        ImportZenlessScreenshotCommand = Command(ImportZenlessScreenshotAsync);
        AddZenlessCommand = Command(async () =>
        {
            var account = await _database.AddAccountAsync(GameId.ZenlessZoneZero, "新绝区零账号", "国服", null);
            await ReloadZenlessAsync();
            SelectedZenless = ZenlessRows.Single(row => row.Id == account.Id);
        });
        SaveZenlessCommand = Command(SaveZenlessAsync);
        DeleteZenlessCommand = Command(DeleteZenlessAsync);
        PrepareZenlessCommand = Command(PrepareZenlessAsync);
        MoveZenlessUpCommand = Command(() => MoveZenlessAsync(-1));
        MoveZenlessDownCommand = Command(() => MoveZenlessAsync(1));
        SaveZenlessTaskCommand = Command(async () =>
        {
            if (_zenlessBinding is not { } binding || _zenlessSource is not { } source)
                throw new InvalidOperationException("请先为所选账号补齐运行配置。");
            if (!string.Equals(binding.Root, RequireZenlessRoot(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("OneDragon 安装路径已经更改，请重新核对绑定。");
            using var lease = DesktopOperationLease.Acquire(_zenlessProfileDirectory);
            await OneDragonSourceProfile.SaveTasksAsync(binding, source.Revision,
                ZenlessTasks.ToDictionary(task => task.Id, task => task.Enabled));
            await RefreshZenlessSourceAsync();
            ZenlessStatus = "已保存到该账号的 OneDragon 档案。";
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
                ZenlessReadiness = "导入登录截图可自动建立账号和运行配置。";
                return;
            }
            var settings = await _database.LoadAccountSettingsAsync(account.Id);
            var profile = (await _database.LoadLoginProfilesAsync(GameId.ZenlessZoneZero)).SingleOrDefault(row => row.AccountId == account.Id);
            if (read != _zenlessRead) return;
            var binding = OneDragonProfileBinding.FromSettings(settings.Values);
            if (!keepDraft)
            {
                ZenlessInstance = binding?.Instance;
                ZenlessMasked = profile?.MaskedPhone ?? "";
            }
            if (binding is null || !string.Equals(root, binding.Root, StringComparison.OrdinalIgnoreCase))
            {
                ClearZenlessTaskEditor();
                ZenlessReadiness = profile is null
                    ? "待登记登录账号。请导入截图，或填写脱敏手机号后点击「准备运行配置」。"
                    : "待配置。点击「准备运行配置」为这个账号建立独立档案。";
                return;
            }
            var source = await OneDragonSourceProfile.ReadAsync(binding);
            if (read != _zenlessRead) return;
            _zenlessBinding = binding;
            if (_zenlessSource?.Revision != source.Revision || !keepDraft)
                foreach (var task in ZenlessTasks)
                    task.Enabled = source.Tasks.Any(saved => saved.Id == task.Id && saved.Enabled);
            _zenlessSource = source;
            Raise(nameof(CanEditZenlessTasks));
            var enabled = source.Tasks.Count(task => task.Enabled);
            var readiness = !System.IO.File.Exists(source.GamePath) ? "找不到游戏程序，请打开 OneDragon 更新路径。"
                : profile is null ? "请在账号管理保存登录卡片上的脱敏手机号。"
                : enabled == 0 ? "请启用至少一项任务并保存。"
                : account.AutoRun ? "配置就绪，已加入批次。可从首页启动。"
                : "配置就绪。在账号管理勾选「加入日常批次」并保存。";
            ZenlessReadiness = $"OneDragon 实例 {binding.Instance:D2} · {enabled} 项启用 · {readiness}";
        }
        catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException
            or TimeoutException or Microsoft.Data.Sqlite.SqliteException or System.Text.DecoderFallbackException)
        {
            if (read != _zenlessRead) return;
            ClearZenlessTaskEditor();
            ZenlessReadiness = ex.Message + " 可点击「准备运行配置」补齐缺失的档案。";
        }
    }

    private string RequireZenlessRoot()
    {
        var root = _oneDragonRoot();
        if (!System.IO.Path.IsPathFullyQualified(root))
            throw new InvalidOperationException("请先在引擎与环境保存 OneDragon 安装目录。");
        return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
    }

    private async Task ImportZenlessScreenshotAsync()
    {
        var root = RequireZenlessRoot();
        var screenshot = new OpenFileDialog
        {
            Title = "导入绝区零登录截图",
            Filter = "图片 (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
            CheckFileExists = true,
        };
        if (screenshot.ShowDialog() != true) return;
        ZenlessStatus = "正在本机识别绝区零登录截图…";
        var masks = await ZenlessAccountImport.ReadScreenshotAsync(root, screenshot.FileName);

        var gamePath = ChooseZenlessClient();
        if (gamePath is null) return;

        IReadOnlyList<Guid> ids;
        try
        {
            ids = await ZenlessAccountImport.RegisterAsync(_database, root, _zenlessProfileDirectory, gamePath, masks);
        }
        finally
        {
            await ReloadZenlessAsync();
        }
        SelectedZenless = ZenlessRows.Single(row => row.Id == ids[0]);
        await RefreshZenlessSourceAsync();
        ZenlessStatus = $"已核对 {ids.Count} 个账号及对应的 OneDragon 源档案。请确认名称和顺序，勾选「加入日常批次」并保存。";
    }

    private void ClearZenlessTaskEditor()
    {
        _zenlessBinding = null;
        _zenlessSource = null;
        foreach (var task in ZenlessTasks) task.Enabled = false;
        Raise(nameof(CanEditZenlessTasks));
    }

    private static string? ChooseZenlessClient()
    {
        if (System.IO.File.Exists(KnownZenlessClient)) return KnownZenlessClient;
        var executable = new OpenFileDialog
        {
            Title = "选择国服绝区零客户端",
            Filter = "ZenlessZoneZero.exe|ZenlessZoneZero.exe",
            CheckFileExists = true,
        };
        return executable.ShowDialog() == true ? executable.FileName : null;
    }

    private async Task PrepareZenlessAsync()
    {
        var account = SelectedZenless ?? throw new InvalidOperationException("请先选择要配置的账号。");
        var masked = ZenlessMasked;
        if (string.IsNullOrWhiteSpace(masked))
            throw new InvalidOperationException("请先导入登录截图，或填写该账号的脱敏手机号。");
        var root = RequireZenlessRoot();
        var gamePath = ChooseZenlessClient();
        if (gamePath is null) return;
        await ZenlessAccountImport.PrepareAsync(_database, account, root, _zenlessProfileDirectory, gamePath, masked);
        await ReloadZenlessAsync();
        ZenlessStatus = "已补齐并核对 OneDragon 运行配置。";
    }

    private async Task DeleteZenlessAsync()
    {
        if (SelectedZenless is not { } account) return;
        if (System.Windows.MessageBox.Show($"删除 Vivian 中的「{account.Name}」档案？\nOneDragon 实例和游戏登录记录会保留。",
                "删除绝区零档案", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.No) != System.Windows.MessageBoxResult.Yes) return;
        await _database.DeleteAccountAsync(account.Id);
        await ReloadZenlessAsync();
        ZenlessStatus = "已删除该 Vivian 账号档案。";
    }

    private async Task SaveZenlessAsync()
    {
        var selected = SelectedZenless ?? throw new InvalidOperationException("请先选择或新增一个绝区零账号。");
        var root = RequireZenlessRoot();
        var id = ZenlessInstance;
        var masked = ZenlessMasked;
        var catalog = await OneDragonInstanceCatalog.ReadAsync(root);
        if (id is null || !catalog.Instances.Any(row => row.Index == id))
            throw new InvalidOperationException("请先点击「准备运行配置」，或选择已有的 OneDragon 实例。");
        if (string.IsNullOrWhiteSpace(ZenlessName)) throw new InvalidOperationException("请输入账号显示名称。");
        var binding = new OneDragonProfileBinding(root, id.Value);
        if (ZenlessAutoRun)
        {
            var source = await OneDragonSourceProfile.ReadAsync(binding);
            if (!System.IO.File.Exists(source.GamePath) || !source.Tasks.Any(task => task.Enabled))
                throw new InvalidOperationException("先核对游戏路径并启用至少一项任务，再保存批次选择。");
        }
        await _database.SaveOneDragonBindingAsync(selected with { Name = ZenlessName.Trim(), AutoRun = ZenlessAutoRun },
            binding, masked);
        await ReloadZenlessAsync();
        ZenlessStatus = "已保存账号和批次选择。";
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


public sealed class ZenlessTaskOption(string id, string title) : ObservableObject
{
    private bool _enabled;
    public string Id { get; } = id;
    public string Title { get; } = title;
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
}
