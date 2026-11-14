using System.Dynamic;
using System.Windows;
using VIvian.App;
using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Infrastructure.Engines.BetterGi;

namespace CourtPreview;

// Display-only property bags: no setters with effects, commands, services or engine instances.
internal static class DemoData
{
    internal static object Node(params (string Key, object? Value)[] values)
    {
        IDictionary<string, object?> node = new ExpandoObject();
        foreach (var (key, value) in values) node[key] = value;
        return node;
    }
    internal static object MainData(string root)
    {
        var theme = new ThemeAssets(root); // Empty unique folder; only packaged artwork can be used.
        object[] Accounts(string game) => Enumerable.Range(1, 3).Select(i => Node(
            ("Name", $"DEMO · {game} 账号 {i}"), ("DailyEnabled", false), ("IsSelected", false), ("Enabled", false), ("AutoRun", false),
            ("LoginStatus", "未绑定 · 合成展示"), ("BindingDetail", "未读取登录档案，不可执行。"),
            ("LastResult", "仅排版样例"), ("Verification", "未核对"), ("HasGenshinLoginBinding", false))).ToArray();
        var star = Accounts("星铁"); var genshin = Accounts("原神"); var zenless = Accounts("绝区零");
        object[] Fields(IEnumerable<EngineParameterDefinition> schemas) => schemas.Select(f => Node(
            ("Title", f.Title), ("Description", f.Description), ("Group", f.Group), ("Current", f.DefaultValue),
            ("BoolValue", f.DefaultValue == "true"), ("Editable", false), ("IsBoolean", f.DataType == "boolean"),
            ("IsChoice", f.DataType == "enum"), ("Options", ParameterRules.LabelledOptions(f)),
            ("IsTextual", f.DataType is "integer" or "text"), ("IsReadOnlyDisplay", f.DataType is not ("boolean" or "enum" or "integer" or "text")),
            ("State", "DEMO"), ("Dependencies", ""), ("Rule", ""))).ToArray();
        var tasks = new[] { "日常任务", "咖啡店", "录像店", "电量任务" }.Select(t => Node(("Title", t), ("Enabled", false))).ToArray();
        var accountVm = Node(("Rows", star), ("SelectedRow", star[0]), ("ConfigurationRow", star[0]),
            ("ZenlessRows", zenless), ("SelectedZenless", zenless[0]), ("SelectedStarRail", star[0]),
            ("RecentBatches", Array.Empty<object>()), ("BatchResults", Array.Empty<object>()), ("SelectedBatch", null),
            ("ZenlessAutoRun", false), ("ZenlessInstances", new[] { "DEMO" }), ("ZenlessInstance", "DEMO"),
            ("Status", "DEMO · 仅显示合成账号"), ("ZenlessName", "DEMO · 绝区零账号"), ("ZenlessMasked", ""),
            ("ZenlessTasks", tasks), ("CanEditZenlessTasks", false), ("HasZenlessAccount", false),
            ("ZenlessReadiness", "DEMO · 未连接源档案"), ("ZenlessStatus", "仅合成任务展示"));
        var logs = Enumerable.Range(1, 12).Select(i => $"DEMO 12:00:{i:00} · 仅用于检查长日志与滚动，不是真实运行记录。").ToArray();
        var recent = new[] { Node(("Game", "DEMO · 星铁"), ("When", "11-14 12:00"), ("Result", "未知 · 仅展示"), ("Duration", "—")) };
        return Node(("Theme", theme), ("CharacterArt", new ArtSlot(theme.Character)), ("AssistantArt", new ArtSlot(theme.Assistant)), ("Assistant", Node(("Name", "DEMO 助手"))),
            ("LegacyRealTestVisibility", Visibility.Collapsed), ("TemporaryConfigurationVisibility", Visibility.Collapsed),
            ("TemporaryConfigurationText", "DEMO · 不写配置"), ("AllowTemporaryConfigurationThisRun", false), ("March7thConfirmedThisRun", false),
            ("March7thConsentNote", "DEMO · 不执行"), ("StartButtonText", "运行（禁用）"), ("LastRealResultText", "未运行"),
            ("ManualTestStateText", "DEMO · 未验收"), ("ReleaseStatus", "DEMO · 未核对发行包"),
            ("HasStatusAction", false), ("StatusActionText", ""), ("DailyStartHint", "DEMO · 所有业务入口禁用"),
            ("PendingRestoreVisibility", Visibility.Collapsed), ("PendingRestoreText", "无真实恢复记录"),
            ("IsIdle", true), ("CanEditWorkspace", true), ("CanOperateWorkspace", true),
            ("ExecutionBanner", "DEMO · 交互验收宿主\n所有业务入口禁用"), ("TodayText", "DEMO · 隔离展示"),
            ("RunnableText", "0"), ("SelectedAccountsText", "0"), ("EstimateText", "—"), ("RunStateText", "DEMO"),
            ("Status", "DEMO · 临时合成数据；业务按钮禁用；助手不发送"), ("BatchStatus", "未运行 · DEMO"),
            ("VersionText", "独立交互验收工具 · 非生产启动"), ("DataDirectoryText", "临时目录：" + root),
            ("LogRetentionDays", 14), ("IncludeRunHistory", false), ("RecentRuns", recent),
            ("History", new[] { Node(("StartedAt", DateTime.UnixEpoch), ("Summary", "DEMO · 自检展示记录")) }),
            ("Accounts", accountVm), ("Configuration", Node(("Parameters", Fields(EngineCatalog.March7th.Parameters)), ("Status", "DEMO · 默认字段，无源配置"))),
            ("GenshinConfiguration", Node(("Accounts", genshin), ("SelectedAccount", genshin[0]),
                ("CanEdit", true), ("CanEditWorkspace", true), ("CanOperateWorkspace", true), ("IsIdle", true),
                ("NewAccountName", ""), ("SelectedForDaily", Array.Empty<object>()), ("IsLoginVerificationAvailable", false), ("IsVerifyingLogin", false),
                ("LoginStatus", "DEMO · 登录验收禁用"), ("GameExecutable", ""), ("SecondLoginAccount", null), ("Status", "DEMO · 无源配置"),
                ("VisiblePlanParameters", Fields(BetterGiAccountPlans.Parameters.Where(f => f.Path is not ("DailyRewardPartyName" or "PartyName")))),
                ("DetailedPlanParameters", Fields(BetterGiAccountPlans.Parameters.Where(f => f.Path is "DailyRewardPartyName" or "PartyName"))),
                ("Source", Node(("SharedRows", Array.Empty<object>()), ("HasBinding", false), ("CanConnect", false), ("TaskOrder", "DEMO"), ("Status", "未连接"))))),
            ("Queue", Node(("CanOperateWorkspace", true), ("Current", "DEMO · 无运行任务"), ("PauseButtonText", "暂停"), ("HomeCleanupHint", "DEMO"), ("LogCleanupHint", "DEMO"), ("IsIdle", true), ("Games", Array.Empty<object>()), ("LogLines", logs), ("HasUnresolved", false), ("Status", "DEMO"),
                ("Runs", logs.Select(l => Node(("Title", l), ("Summary", l), ("RunId", Guid.Empty), ("Status", "未运行"), ("When", "DEMO"))).ToArray()))),
            ("ConfigSyncText", "DEMO"), ("InstalledText", "无引擎"), ("PrivilegeText", "普通权限"), ("PauseText", "暂停"),
            ("StopKeyText", "宿主未注册全局热键"), ("ExportStatus", "宿主禁用导出"), ("ReleaseArchivePath", ""),
            ("Engines", new[] { ("原神", theme.BannerGenshin), ("星铁", theme.BannerStarRail), ("绝区零", theme.BannerZenless) }.Select(n => Node(
                ("Name", n.Item1), ("Evidence", "DEMO · 无真实检测"), ("DetectedVersion", "未检测"), ("InstallPath", ""), ("Compatibility", "DEMO · 无引擎"), ("Version", "—"),
                ("Banner", new ArtSlot(n.Item2)), ("BannerFrom", System.Windows.Media.Colors.DarkBlue), ("BannerTo", System.Windows.Media.Colors.SteelBlue),
                ("Tagline", "DEMO"), ("RunHint", "未运行"), ("ShortVersion", "仅展示"), ("Chips", new[] { "业务禁用", "未连接" }),
                ("StateBrush", System.Windows.Media.Brushes.SlateGray))).ToArray()));
    }
    internal static object Assistant(string root)
    {
        var theme = new ThemeAssets(root);
        return Node(("Theme", theme), ("Name", "芙宁娜 · DEMO"), ("Avatar", new ArtSlot(theme.Assistant)),
            ("StatusLine", "纯合成聊天 · 不联网、不发送"), ("SettingsVisibility", Visibility.Collapsed),
            ("QuickCommands", new[] { "DEMO 快捷指令（禁用）" }), ("Input", "可编辑本地文字；不会保存或发送。"),
            ("Messages", new[] {
                new ChatItem(false, "DEMO · 这是独立交互验收宿主，所有业务按钮均已禁用。"),
                new ChatItem(true, "检查长消息、滚动、输入框和小窗口换行。"),
                new ChatItem(false, string.Join("\n", Enumerable.Repeat("DEMO 长消息：窗口缩放时应正常换行。这里只展示合成文字，不读取账号或接口配置，也不会提交请求。", 12))) }));
    }
}
