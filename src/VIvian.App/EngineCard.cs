using System.Collections.ObjectModel;
using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Core.Execution;

namespace VIvian.App;

public sealed record CheckRow(string Title, string State, string Brush, string Message, string Action);
public sealed record CapabilityRow(string Title, string Status, string Brush, string Note);

/// <summary>One engine as shown on the overview and the engine management page.</summary>
public sealed class EngineCard(GameId id, string name, string engine, string glyph) : ObservableObject
{
    public GameId Id { get; } = id;
    public string Name { get; } = name;
    public string Engine { get; } = engine;
    public string Glyph { get; } = glyph;
    public EngineProfile Profile => EngineCatalog.For(Id);
    /// <summary>The game's own colour; used only next to that game.</summary>
    public string Accent => Id switch { GameId.Genshin => "#2BA89A", GameId.StarRail => "#7B61FF", _ => "#E0A800" };
    public string Version => $"{Profile.EngineName} {Profile.PinnedVersion}";
    /// <summary>Short engine label for narrow rows (OneDragon instead of ZenlessZoneZero-OneDragon).</summary>
    public string ShortVersion => $"{Profile.EngineName.Replace("ZenlessZoneZero-", "")} {Profile.PinnedVersion}";
    public string Tagline => Id switch { GameId.Genshin => "Genshin Impact", GameId.StarRail => "Honkai: Star Rail", _ => "Zenless Zone Zero" };
    /// <summary>Three daily/weekly task names from the versioned catalog, shown as chips.</summary>
    public IReadOnlyList<string> Chips => Profile.Tasks.Where(t => t.Frequency != TaskFrequency.OnDemand)
        .Select(t => t.Title.Split('（')[0]).Distinct().Take(3).ToList();
    /// <summary>True only when this build may run the engine for real and its installation is verified.</summary>
    internal bool GenshinDailyWired { get; set; }
    internal bool HomeRewardWired { get; set; }
    internal bool CanOperateWorkspace { get; set; } = true;
    public bool CanRunForReal => CanOperateWorkspace && (Id switch
    {
        GameId.StarRail => RealExecutionPolicy.IsM5DailyTest && Report?.Compatibility == CompatibilityClass.IdentityVerified,
        GameId.Genshin => GenshinDailyWired && InstallPath == SavedInstallPath &&
            Report?.Compatibility is CompatibilityClass.PendingValidation or CompatibilityClass.IdentityVerified,
        GameId.ZenlessZoneZero => RealExecutionPolicy.IsM5DailyTest && RealExecutionPolicy.IsUnifiedInstall
            && InstallPath == SavedInstallPath && Report is { Compatibility: CompatibilityClass.IdentityVerified } zenless
            && !zenless.Checks.Any(check => check.State == CheckState.Failed && check.Id != "od:instances"),
        _ => false,
    });
    public bool CanPlayFromHome => CanRunForReal || (CanOperateWorkspace && HomeRewardWired &&
        InstallPath == SavedInstallPath && Report?.Compatibility == CompatibilityClass.PendingValidation);
    public string HomePlayHint => HomeRewardWired ? "原神日常 · 按勾选顺序执行已保存计划"
        : Id == GameId.ZenlessZoneZero && !CanRunForReal ? Evidence : "运行";
    public string RunHint => !CanOperateWorkspace ? "隔离验收 · 只读"
        : HomeRewardWired ? (CanPlayFromHome ? "按顺序执行保存计划" : "日常 · 待安装核验")
        : CanRunForReal ? "可以执行"
        : Id == GameId.ZenlessZoneZero && RealExecutionPolicy.IsUnifiedInstall ? Compatibility
        : Id == GameId.StarRail || GenshinDailyWired ? "待安装核验" : "接入中";
    /// <summary>Banner gradient when the user supplied no banner image (white text stays above 5.9:1).</summary>
    public string BannerFrom => Id switch { GameId.Genshin => "#1F6F86", GameId.StarRail => "#4B3BA8", _ => "#7A4F00" };
    public string BannerTo => Id switch { GameId.Genshin => "#2B4C9A", GameId.StarRail => "#2A2F7A", _ => "#3A2A10" };
    private ArtSlot _banner = new(null);
    public ArtSlot Banner { get => _banner; set => Set(ref _banner, value); }
    public string PinnedVersion => $"支持基准：{Profile.EngineName} {Profile.PinnedVersion}（证据待桌面验证）";
    public long Revision { get; set; }
    /// <summary>The install path as last loaded from or written to the database (to detect unsaved edits).</summary>
    public string SavedInstallPath { get; set; } = "";
    private string _installPath = "";
    public string InstallPath
    {
        get => _installPath;
        set
        {
            if (_installPath == value) return;
            Set(ref _installPath, value);
            Report = null;
            Compatibility = "路径已改变，请重新检查";
            Raise(nameof(CanRunForReal));
            Raise(nameof(CanPlayFromHome));
            Raise(nameof(RunHint));
            Raise(nameof(Execution));
        }
    }
    private string _compatibility = "尚未检查";
    /// <summary>Short state shown on the card.</summary>
    public string Compatibility
    {
        get => _compatibility;
        set { Set(ref _compatibility, value); Raise(nameof(RunHint)); }
    }
    private string _evidence = "保存安装目录后，VIvian 会只读检查引擎文件。";
    public string Evidence
    {
        get => _evidence;
        set { Set(ref _evidence, value); Raise(nameof(HomePlayHint)); }
    }
    private string _stateBrush = StateColors.Neutral;
    public string StateBrush { get => _stateBrush; set => Set(ref _stateBrush, value); }
    private string _detectedVersion = "检测到的版本：—";
    public string DetectedVersion { get => _detectedVersion; private set => Set(ref _detectedVersion, value); }
    private string _lastRun = "最近运行：无记录";
    public string LastRun { get => _lastRun; set => Set(ref _lastRun, value); }
    private string _pendingWarning = "";
    public string PendingWarning { get => _pendingWarning; private set => Set(ref _pendingWarning, value); }
    public ObservableCollection<CheckRow> Checks { get; } = [];
    public IReadOnlyList<CapabilityRow> Capabilities { get; } = EngineCatalog.For(id).Capabilities.Select(ToRow).ToList();
    public EngineReadinessReport? Report { get; private set; }

    /// <summary>What this build lets the user do with the engine for real.</summary>
    public string Execution => !CanOperateWorkspace ? "隔离验收中，此游戏卡片仅供查看。"
        : HomeRewardWired ? "按账号管理的勾选顺序执行已保存的任务和消耗设置；启用合成时先合成树脂，再领取每日奖励。"
        : GenshinDailyWired
        ? (CanRunForReal
            ? "按已保存的账号任务和消耗设置执行；开始前核对固定安装及账号，结束后确认清理和配置恢复。"
            : "真实任务未开放。请先保存 BetterGI 安装目录并完成核验。")
        : Id == GameId.ZenlessZoneZero && RealExecutionPolicy.IsUnifiedInstall
        ? (CanRunForReal
            ? "导入登录截图并勾选账号后，Vivian 会在登录界面核对账号，再由 OneDragon 执行源档案中的任务。"
            : "真实任务未开放。请先保存 OneDragon 安装目录并完成核验。")
        : Id == GameId.StarRail && RealExecutionPolicy.IsM5DailyTest
        ? "可以执行：日常（清体力 + 每日实训 + 领奖），每次由你确认后启动；其他任务未开放。"
        : Id == GameId.StarRail && RealExecutionPolicy.IsReusableManualTest
        ? "真实任务：仅 screen_test，可在“真实任务测试”页逐次由本人确认启动；其他任务未开放。"
        : Id == GameId.StarRail && RealExecutionPolicy.BuildPermitsRealExecution
        ? "仅单次 screen_test 可在“星铁实测”页由本人确认启动；其他真实任务未开放。"
        : RealExecutionPolicy.BuildPermitsRealExecution
        ? "真实任务执行：未开放（此引擎尚未完成实机验证）"
        : "真实任务执行：未开放（此开发版只做只读检查与模拟运行）";

    public void Show(string state, string brush, string detail)
    {
        Compatibility = state; StateBrush = brush; Evidence = detail;
    }

    public void Show(EngineReadinessReport report)
    {
        Report = report;
        foreach (var name in new[] { nameof(CanRunForReal), nameof(CanPlayFromHome), nameof(RunHint), nameof(HomePlayHint), nameof(Execution) }) Raise(name);
        var (state, brush) = report.Compatibility switch
        {
            CompatibilityClass.NotConfigured => ("未设置路径", StateColors.Neutral),
            CompatibilityClass.NotInstalled => ("未安装", StateColors.Neutral),
            CompatibilityClass.Unavailable => ("无法访问", StateColors.Error),
            CompatibilityClass.UnsafeLayout => ("目录不安全", StateColors.Error),
            CompatibilityClass.UnsupportedVersion => ("不支持（版本）", StateColors.Warning),
            CompatibilityClass.UnknownVersion => ("版本未知", StateColors.Warning),
            CompatibilityClass.PendingValidation => ("待桌面验证", StateColors.Warning),
            CompatibilityClass.IdentityVerified => ("文件已核对 · 待桌面验证", StateColors.Verified),
            _ => ("未知", StateColors.Error),
        };
        var primary = report.Checks.FirstOrDefault(c => c.State == CheckState.Failed)
            ?? report.Checks.FirstOrDefault(c => c.Id == "identity")
            ?? report.Checks.First();
        Show(state, brush, primary.Message + (primary.Action is { } a ? " 建议：" + a : ""));
        DetectedVersion = "检测到的版本：" + (report.DetectedVersion ?? "—");
        PendingWarning = report.Compatibility is CompatibilityClass.PendingValidation or CompatibilityClass.IdentityVerified
            ? "安装看起来符合固定版本，但启动方式、进程归属与完成信号都还没有在 Windows 桌面上实测。"
            : "";
        Checks.Clear();
        foreach (var check in report.Checks) Checks.Add(ToRow(check));
    }

    public void ShowLastRun(RunRecord? run) =>
        LastRun = run is null ? "最近运行：无记录" : $"最近运行（{run.UpdatedAt.ToLocalTime():MM-dd HH:mm}）：{RunPresentation.Summary(run)}";

    private static CheckRow ToRow(ReadinessCheck check)
    {
        var (state, brush) = check.State switch
        {
            CheckState.Passed => ("通过", StateColors.Verified),
            CheckState.Warning => ("注意", StateColors.Warning),
            CheckState.Failed => ("未通过", StateColors.Error),
            CheckState.Pending => ("待验证", StateColors.Warning),
            _ => ("不适用", StateColors.Neutral),
        };
        return new(check.Title, state, brush, check.Message, check.Action is null ? "" : "建议：" + check.Action);
    }

    private static CapabilityRow ToRow(EngineCapability c)
    {
        var (status, brush) = (c.Status, c.EnabledInVivian) switch
        {
            (CapabilityStatus.Verified, _) => ("已验证", StateColors.Verified),
            (CapabilityStatus.Unsupported, _) => ("不支持", StateColors.Neutral),
            (_, true) => ("已提供（只读）· 待实包确认", StateColors.Warning),
            _ => ("待验证 · 已禁用", StateColors.Warning),
        };
        return new(c.Title, status, brush, $"证据级别 {c.Evidence}：{c.Note}");
    }
}

public static class StateColors
{
    public const string Verified = "#17693B";
    public const string Warning = "#8A5A00";
    public const string Error = "#B3261E";
    public const string Neutral = "#47528A";
}
