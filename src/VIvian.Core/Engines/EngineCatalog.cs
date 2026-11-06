using static VIvian.Core.Engines.CapabilityStatus;
using static VIvian.Core.Engines.EvidenceLevel;
using Cap = VIvian.Core.Engines.EngineCapabilityId;

namespace VIvian.Core.Engines;

/// <summary>
/// Version-specific engine profiles. Every fact cites its source (docs/research, docs/evidence, or the pinned upstream
/// commit) and is Pending unless independently verified. Nothing here was observed on a real release or a real
/// desktop: task identifiers, launch arguments and completion signals are candidates only and never sent to an engine.
/// </summary>
public static class EngineCatalog
{
    private const string M7Research = "docs/research/M3_MARCH7TH_ANALYSIS.md; docs/research/M3_ENGINE_CLI_COMPATIBILITY.md";
    private const string M7Cli = "March7thAssistant@2166028 utils/cli_entry.py（见 docs/reviews/M3_GEMINI_RESEARCH_VALIDATION.md）";
    private const string M7Audit = "docs/evidence/M3_MARCH7TH_RELEASE_ASSET_AUDIT.md";
    private const string BgiResearch = "docs/research/M3_BETTERGI_ANALYSIS.md; docs/research/M3_ADAPTER_IMPLEMENTATION_REQUIREMENTS.md";
    private const string BgiCli = "better-genshin-impact@9e3a0b0 Helpers/CommandLineOptions.cs（见 docs/evidence/EVIDENCE_INDEX.md）";
    private const string OdResearch = "docs/research/M3_ONEDRAGON_ANALYSIS.md; docs/research/M3_ENGINE_CLI_COMPATIBILITY.md";
    private const string OdCli = "ZenlessZoneZero-OneDragon@6fd95f1 launcher_base.py（见 docs/evidence/EVIDENCE_INDEX.md）";
    private const string Releases = "docs/research/M3_UPSTREAM_RELEASE_VERIFICATION.md（研究报告给出的摘要，未独立核对）";

    private const string ExecutionNote = "会启动上游程序：此开发版一律禁用，需在隔离副本中经用户授权实测后才可能开放。";

    public static EngineProfile March7th { get; } = new(
        GameId.StarRail, "March7thAssistant", "v2026.10.31", "1e77d59", "m7a-v2026.10.31-r1",
        ExpectedPrograms: ["m7a.exe"],
        KnownPaths:
        [
            new("m7a.exe", false, true, "命令行入口（固定版本源码中的构建定义）"),
            new("March7th Assistant.exe", false, false, "主程序（任务执行时经 UAC 启动）"),
            new(@"assets\config\version.txt", false, true, "版本文件（只读取，不超过 64 字节）"),
            new("config.yaml", false, false, "上游配置（只检查是否存在；首次运行前可能不存在；VIvian 不读取、不修改）"),
            new("logs", true, false, "上游日志目录（只检查是否存在）"),
        ],
        OfficialAssets:
        [
            new("March7thAssistant_full.zip", 719711875, "e03aaad4ea27c70f30bf5241ab3bfac8894de7675ddd30a9aef7f1e551af9985", Verified, M7Audit),
            new("March7thAssistant_full.7z", 474333248, "1fcaa1934e3d30d598ce7863756c49b9e9fb4f4fbb759066a62f52541a39dee0", Verified, M7Audit),
            new("update.7z", 163523697, "4f9ba139ee485aac770e7498551885aa1ae821c3db019962c0c3e4dc84e097c6", Verified, M7Audit),
        ],
        Capabilities:
        [
            new(Cap.InstallationDiscovery, "安装发现（只读）", Pending, B, false, true, M7Research, "按固定版本的目录结构查找 m7a.exe 与版本文件；尚未用真实官方包确认目录结构。"),
            new(Cap.ExecutableIdentity, "可执行文件身份（SHA-256）", Pending, B, false, true, M7Audit, "与已核对官方 zip 中 m7a.exe 的摘要比对；真实官方包的实测尚未完成。"),
            new(Cap.VersionMetadata, "版本信息（版本文件 + 文件版本）", Pending, B, false, true, M7Research, "读取 assets\\config\\version.txt 与文件版本资源，不运行程序。"),
            new(Cap.OfficialReleaseVerification, "官方发行包核对", Pending, C, false, true, M7Audit, "摘要来自官方 Release 元数据；用真实下载文件的核对尚未完成。"),
            new(Cap.ConfigurationPathCheck, "配置路径检查（只检查存在）", Pending, B, false, true, M7Research, "不读取、不修改 config.yaml。"),
            new(Cap.VersionQuery, "--version 查询", Pending, B, true, false, M7Cli, "可能触发配置初始化。" + ExecutionNote),
            new(Cap.StatusQuery, "--status 查询", Pending, B, true, false, M7Cli, "不是严格只读：可能创建/删除写权限探测文件或生成 config.yaml。" + ExecutionNote),
            new(Cap.TaskListQuery, "--list --json 查询", Pending, B, true, false, M7Cli, "JSON 结构未经实包确认。" + ExecutionNote),
            new(Cap.TaskExecution, "真实任务执行", Pending, B, true, false, M7Cli, "任务会经 UAC 启动提权进程并控制游戏。" + ExecutionNote),
            new(Cap.GracefulStop, "正常停止", Unsupported, D, true, false, "docs/reviews/M3_GEMINI_RESEARCH_VALIDATION.md", "没有可绑定到本次运行的已验证停止通道；全局热键被禁止。"),
            new(Cap.EmergencyStop, "紧急停止（Job 终止）", Pending, D, true, false, "docs/architecture/ADR-007-M3-PRIVILEGE-BOUNDARY.md", "经 UAC 启动的提权进程可能脱离 VIvian 的 Job，需桌面实测。"),
            new(Cap.CompletionEvidence, "完成证据", Pending, C, true, false, M7Research, "退出码 0 不足以证明成功（全部跳过时也为 0）。"),
            new(Cap.ProcessOwnership, "进程归属", Pending, D, true, false, "docs/research/M3_ENGINE_PROCESS_AND_PRIVILEGE_RISKS.md", "跨权限进程的归属无法确认时，清理保持“未确认”。"),
        ],
        Tasks:
        [
            new("main", "完整运行", Pending, B, M7Research, "候选任务标识，未在真实发行包上实测。"),
            new("daily", "每日实训", Pending, B, M7Cli, "固定版本 CLI 有此入口；实际完成与跨 UAC 清理待实测。", TaskFrequency.Daily,
                "m7a.exe daily --after-finish None --non-interactive --json", "本次 RunId 日志须有 500 分图像核验后的领奖完成通知", [],
                ["固定版本与安装身份核验", "上游配置可读取", "领取奖励（reward_enable）须开启，可经本次授权临时开启并自动恢复", "任务前后完成证据可核对"], ["reward_enable", "daily_tasks"]),
            new("power", "清体力", Pending, B, M7Research, "候选任务标识，未在真实发行包上实测。", TaskFrequency.Daily),
            new("routine", "日常", Pending, B, M7Research, "候选任务标识，未在真实发行包上实测。"),
            new("currencywars", "货币战争", Pending, B, M7Research, "候选任务标识，未在真实发行包上实测。", TaskFrequency.Weekly),
            new("fight", "锄大地", Pending, B, M7Research, "候选任务标识，未在真实发行包上实测。"),
            new("universe", "模拟/差分宇宙", Pending, B, M7Research, "候选任务标识，未在真实发行包上实测。", TaskFrequency.Weekly),
            new("screen_test", "界面可切换性测试", Pending, B, M7Cli, "逐项切换游戏界面；只在用户单次授权后执行，仍会操作游戏。"),
        ],
        Launch: new("m7a.exe", WritesToStandardOutput: true, PrivilegeRequirement.ElevationRequested, ProcessTopology.LauncherWithWorker,
            PathConstraint.None, Pending,
            "m7a.exe 在 VIvian 的 Job 内启动；任务执行时它经 UAC 启动提权的主程序。提权进程是否仍在 Job 内未知：无法确认时清理为“未确认”并阻断后续任务。",
            "查询命令按源码不需要提权，任务执行需要提权（级别 B，未实测）。"),
        CompletionContract:
        [
            new(EvidenceOutcome.Success, "本 RunId 的进程输出中出现目标任务的完成行，且退出码为 0、没有失败行", Pending, C, M7Research),
            new(EvidenceOutcome.Failure, "非零退出码（v2026.8.28 起修复，PR #1176）或明确的失败输出", Pending, B, M7Research),
            new(EvidenceOutcome.Cancellation, "由 VIvian 发起停止，且 Job 内进程全部退出、提权进程归属已确认", Pending, D, "docs/architecture/ADR-007-M3-PRIVILEGE-BOUNDARY.md"),
            new(EvidenceOutcome.Unknown, "只有退出码 0 而没有完成行（全部任务被跳过时也会如此）", Pending, B, "docs/research/M3_ENGINE_CLI_COMPATIBILITY.md §1.3"),
        ],
        KnownSideEffects:
        [
            "--status / --list 可能在安装目录创建并删除写权限探测文件；缺少 config.yaml 时可能生成它（级别 B/D）。",
            "任务运行会回写 config.yaml 中的时间戳与计数，写入 logs\\ 与截图，并启动游戏（级别 B/C）。",
        ],
        UnsupportedVersions: ["< v2026.8.28（研究报告：非零退出码仍提示完成的缺陷）"])
    {
        Parameters =
        [
            new("daily_enable", "启用每日实训", "完整运行和日常任务中的每日实训开关；单独执行 daily 不受此开关影响（上游 main.py sub_tasks）。", "boolean", "true/false", "true", "日常与完整运行", "无", "每日实训", "config.yaml", ParameterAccess.VerifiedReadWrite, "v2026.10.31 assets/config/config.example.yaml:177"),
            new("power_enable", "启用清体力", "完整运行和日常任务中的清体力总开关；单独执行 power 不受影响。", "boolean", "true/false", "true", "日常与完整运行", "无", "清体力、历战余响", "config.yaml", ParameterAccess.VerifiedReadWrite, "v2026.10.31 assets/config/config.example.yaml:94"),
            new("reward_enable", "启用领取奖励", "奖励领取总开关；关闭时 daily 无法领取实训奖励，也就无法证明完成（上游 tasks/reward）。", "boolean", "true/false", "true", "领取奖励", "无", "每日实训奖励及其他奖励", "config.yaml", ParameterAccess.VerifiedReadWrite, "v2026.10.31 assets/config/config.example.yaml:166"),
            new("daily_tasks", "每日实训任务明细", "由原软件管理的任务列表；当前只读。", "list", "上游定义", "[]", "每日实训", "上游任务结构", "每日实训", "config.yaml", ParameterAccess.UpstreamManaged, "v2026.10.31 assets/config/config.example.yaml:190"),
            new("power_plan", "体力计划", "由原软件管理的副本计划；当前只读。", "list", "上游定义", "[]", "清体力", "副本类型和次数", "清体力", "config.yaml", ParameterAccess.UpstreamManaged, "v2026.10.31 assets/config/config.example.yaml:95"),
            new("pause_after_success", "成功后暂停", "仅影响交互式命令行；VIvian 以 --non-interactive 运行（上游 utils/console.py），此值不影响 VIvian 的任务。", "boolean", "true/false", "true", "命令行窗口", "无", "仅原软件命令行", "config.yaml", ParameterAccess.VerifiedReadOnly, "v2026.10.31 assets/config/config.example.yaml:28"),
            new("exit_after_failure", "失败后退出", "仅影响交互式命令行；VIvian 以 --non-interactive 运行（上游 utils/console.py），此值不影响 VIvian 的任务。", "boolean", "true/false", "false", "命令行窗口", "无", "仅原软件命令行", "config.yaml", ParameterAccess.VerifiedReadOnly, "v2026.10.31 assets/config/config.example.yaml:31"),
        ]
    };

    public static EngineProfile BetterGi { get; } = new(
        GameId.Genshin, "BetterGI", "v0.66.0", "0af68c2", "bgi-v0.66.0-r1",
        ExpectedPrograms: ["BetterGI.exe"],
        KnownPaths:
        [
            new("BetterGI.exe", false, true, "主程序（WPF 图形界面）"),
            new("User", true, false, "用户数据目录（首次启动后创建）"),
            new(@"User\config.json", false, false, "全局配置（只检查是否存在；BetterGI 运行时会整体回写，VIvian 不修改）"),
            new(@"User\OneDragon", true, false, "一条龙配置目录（只检查是否存在）"),
            new(@"User\ScriptGroup", true, false, "配置组目录（只检查是否存在）"),
            new("log", true, false, "日志目录（只检查是否存在）"),
        ],
        OfficialAssets:
        [
            new("BetterGI_v0.66.0.7z", null, "28d4756e55f32637538bf84427f5f31afd5b153bf996530c5975bd9c8c0b9343", Pending, Releases),
        ],
        Capabilities:
        [
            new(Cap.InstallationDiscovery, "安装发现（只读）", Pending, B, false, true, BgiResearch, "按研究报告的目录结构查找 BetterGI.exe；尚未用真实发行包确认。"),
            new(Cap.ExecutableIdentity, "可执行文件身份（SHA-256）", Pending, D, false, true, Releases, "只记录摘要；没有官方的程序文件摘要可比对，身份无法证实。"),
            new(Cap.VersionMetadata, "版本信息（文件版本资源）", Pending, B, false, true, BgiResearch, "只读取文件版本资源，不运行程序。"),
            new(Cap.OfficialReleaseVerification, "官方发行包核对", Pending, D, false, false, Releases, "官方包为 7z，VIvian 暂不读取 7z 内容；摘要仅来自研究报告。"),
            new(Cap.ConfigurationPathCheck, "配置路径检查（只检查存在）", Pending, B, false, true, BgiResearch, "不读取、不修改 User\\ 下的任何配置。"),
            new(Cap.VersionQuery, "命令行版本查询", Unsupported, B, true, false, BgiCli, "固定源码中没有版本查询参数。"),
            new(Cap.StatusQuery, "命令行状态查询", Unsupported, B, true, false, BgiCli, "固定源码中没有状态查询参数。"),
            new(Cap.TaskListQuery, "命令行任务列表", Unsupported, B, true, false, BgiCli, "没有通用任务列表接口；不假设存在通用命令行任务 API。"),
            new(Cap.TaskExecution, "真实任务执行（一条龙）", Pending, B, true, false, BgiCli, "固定源码可解析 startOneDragon；GUI 进程、提权自重启与配置回写均未实测。" + ExecutionNote),
            new(Cap.GracefulStop, "正常停止", Unsupported, D, true, false, "docs/reviews/M3_GEMINI_RESEARCH_VALIDATION.md", "WM_CLOSE 等通道未验证且可能作用到其他窗口。"),
            new(Cap.EmergencyStop, "紧急停止（Job 终止）", Pending, D, true, false, "docs/architecture/ADR-007-M3-PRIVILEGE-BOUNDARY.md", "提权自重启的进程可能已脱离 Job。"),
            new(Cap.CompletionEvidence, "完成证据（日志文件）", Pending, B, true, false, BgiResearch, "日志结束行来自源码；异常被捕获后仍可能以 0 退出。"),
            new(Cap.ProcessOwnership, "进程归属", Pending, D, true, false, "docs/research/M3_ENGINE_PROCESS_AND_PRIVILEGE_RISKS.md", "未提权启动时会以退出码 559 自重启并脱离进程树（研究报告，未实测）。"),
        ],
        Tasks:
        [
            new("startOneDragon", "一条龙（当前选中配置）", Pending, B, BgiCli, "候选入口，未在真实发行包上实测。", TaskFrequency.Daily),
            new("startOneDragon:<config>", "一条龙（指定配置）", Pending, B, BgiCli, "配置名不匹配时的行为未知。"),
            new("--startGroups", "配置组", Pending, B, BgiCli, "候选入口，未在真实发行包上实测。"),
        ],
        Launch: new("BetterGI.exe", WritesToStandardOutput: false, PrivilegeRequirement.ElevationRequested, ProcessTopology.GuiMayRestartElevated,
            PathConstraint.None, Pending,
            "BetterGI.exe 在 Job 内启动；若它以提权方式自重启，新进程不在 VIvian 的 Job 内，归属无法确认，清理为“未确认”。不得按进程名清理已有实例。",
            "图形界面程序，不写标准输出；单实例互斥。"),
        CompletionContract:
        [
            new(EvidenceOutcome.Success, "本次运行开始后新增的日志依次出现“启用一条龙配置” → 进度 → “一条龙和配置组任务结束”，且没有取消行", Pending, B, BgiResearch),
            new(EvidenceOutcome.Failure, "日志中的未捕获异常或明确的失败行", Pending, D, BgiResearch),
            new(EvidenceOutcome.Cancellation, "日志中出现“任务被取消”，且清理已确认", Pending, B, BgiResearch),
            new(EvidenceOutcome.Unknown, "只有进程退出（退出码 0）而没有完整的日志序列", Pending, B, BgiResearch),
        ],
        KnownSideEffects:
        [
            "每次启动都会把 bettergi:// 协议注册到当前路径（HKCR），多副本时会改写用户的协议指向（级别 B）。",
            "运行期和退出时整体回写 User\\config.json，外部修改会被覆盖（级别 B）。",
        ],
        UnsupportedVersions: ["v0.63.0、v0.65.0（研究报告标注的高风险版本）", "refactor/20260930 预览版（配置结构不兼容）"])
    {
        Parameters = [new("User/config.json", "原软件设置", "BetterGI 会整体回写配置，VIvian 只显示位置并交由原软件修改。", "json", "原软件管理", "", "当前安装", "上游进程必须退出", "一条龙", "User/config.json", ParameterAccess.UpstreamManaged, BgiResearch)]
    };

    public static EngineProfile OneDragon { get; } = new(
        GameId.ZenlessZoneZero, "ZenlessZoneZero-OneDragon", "v2.6.0", "5f2e473", "zzzod-v2.6.0-r1",
        ExpectedPrograms: ["OneDragon-RuntimeLauncher.exe", "OneDragon-Launcher.exe"],
        KnownPaths:
        [
            new("OneDragon-RuntimeLauncher.exe", false, false, "集成启动器（研究建议的受管入口）"),
            new("OneDragon-Launcher.exe", false, false, "原始启动器（会派生 python.exe，不作为受管入口）"),
            new(".runtime", true, false, "集成运行时（WithRuntime-Full 包）"),
            new("src", true, false, "脚本源码目录"),
            new("config", true, false, "配置目录（只检查是否存在）"),
            new(@"config\env.yml", false, false, "环境配置（含自动更新开关；VIvian 不读取、不修改）"),
            new(".log", true, false, "日志目录（只检查是否存在）"),
        ],
        OfficialAssets:
        [
            new("ZenlessZoneZero-OneDragon-v2.6.0-WithRuntime-Full.zip", null, "2fd5f6bfc6c01fcf6f21092dc6e18fa18e146d2614ca6ad130d5fc3057ba0ced", Pending, Releases),
            new("ZenlessZoneZero-OneDragon-RuntimeLauncher.zip", null, "07af12f484a4fccb511470a7ae57f27cc17582412546ee9f817a088dbbee146e", Pending, Releases),
        ],
        Capabilities:
        [
            new(Cap.InstallationDiscovery, "安装发现（只读）", Pending, C, false, true, OdResearch, "区分集成启动器、原始启动器与独立运行时；尚未用真实发行包确认目录结构。"),
            new(Cap.ExecutableIdentity, "可执行文件身份（SHA-256）", Pending, D, false, true, Releases, "只记录摘要；没有官方的程序文件摘要可比对。"),
            new(Cap.VersionMetadata, "版本信息（文件版本资源）", Pending, C, false, true, OdResearch, "只读取文件版本资源，不运行程序。"),
            new(Cap.OfficialReleaseVerification, "官方发行包核对", Pending, D, false, false, Releases, "摘要仅来自研究报告，尚未独立核对。"),
            new(Cap.ConfigurationPathCheck, "配置路径检查（只检查存在）", Pending, C, false, true, OdResearch, "不读取、不修改 config\\ 下的任何文件。"),
            new(Cap.VersionQuery, "-v 版本查询", Pending, B, true, false, OdCli, "启动器可能在启动时联网更新代码。" + ExecutionNote),
            new(Cap.StatusQuery, "命令行状态查询", Unsupported, B, true, false, OdCli, "固定源码中没有状态查询参数。"),
            new(Cap.TaskListQuery, "命令行任务列表", Unsupported, B, true, false, OdCli, "没有通用任务列表接口。"),
            new(Cap.TaskExecution, "真实任务执行（-o 一条龙）", Pending, B, true, false, OdCli, "-c、-i 与路径限制只见于文档（级别 C）。" + ExecutionNote),
            new(Cap.GracefulStop, "正常停止", Unsupported, D, true, false, "docs/reviews/M3_GEMINI_RESEARCH_VALIDATION.md", "没有已验证的停止通道。"),
            new(Cap.EmergencyStop, "紧急停止（Job 终止）", Pending, D, true, false, "docs/architecture/ADR-007-M3-PRIVILEGE-BOUNDARY.md", "启动器、工作进程与提权路径都需要实测。"),
            new(Cap.CompletionEvidence, "完成证据", Pending, D, true, false, OdResearch, "完成行格式未经实包确认；端口、窗口或显存变化都不能单独证明成功。"),
            new(Cap.ProcessOwnership, "进程归属", Pending, D, true, false, OdResearch, "原始启动器派生的 python.exe 可能在启动器退出后继续运行。"),
        ],
        Tasks:
        [
            new("-o", "一条龙", Pending, B, OdCli, "候选入口，未在真实发行包上实测。", TaskFrequency.Daily),
            new("-o -c", "一条龙并关闭游戏", Pending, C, OdResearch, "-c 只见于文档。"),
        ],
        Launch: new("OneDragon-RuntimeLauncher.exe", WritesToStandardOutput: false, PrivilegeRequirement.ElevationRequested, ProcessTopology.Unknown,
            PathConstraint.AsciiWithoutSpaces, Pending,
            "只考虑集成启动器作为受管入口；它是否派生子进程未知。原始启动器派生的 python.exe 可能脱离归属，不作为受管入口。",
            "官方文档要求安装路径全英文、无空格（级别 C）。"),
        CompletionContract:
        [
            new(EvidenceOutcome.Success, "本次运行新增的 .log 日志中出现一条龙结束行（格式待实包确认）", Pending, D, OdResearch),
            new(EvidenceOutcome.Failure, "日志中的 Traceback 或明确的失败行（格式待确认）", Pending, D, OdResearch),
            new(EvidenceOutcome.Cancellation, "由 VIvian 发起停止，且启动器与工作进程都已确认退出", Pending, D, OdResearch),
            new(EvidenceOutcome.Unknown, "启动器先于工作进程退出，或只有退出码", Pending, C, OdResearch),
        ],
        KnownSideEffects:
        [
            "启动器默认会联网拉取代码更新并覆盖 src\\（级别 C）。",
            "写入 .log\\ 与运行记录（级别 C）。",
        ],
        UnsupportedVersions: ["< v2.4.4（研究报告：早期启动器工作目录缺陷）"])
    {
        Parameters = [new("config", "账号实例配置", "多个账号实例并存；未绑定明确实例时只发现配置目录。", "yaml", "实例专属", "", "指定账号实例", "明确实例标识", "一条龙", "config/", ParameterAccess.UpstreamManaged, OdResearch)]
    };

    public static IReadOnlyList<EngineProfile> All { get; } = [BetterGi, March7th, OneDragon];

    public static EngineProfile For(GameId game) => game switch
    {
        GameId.Genshin => BetterGi,
        GameId.StarRail => March7th,
        GameId.ZenlessZoneZero => OneDragon,
        _ => throw new ArgumentOutOfRangeException(nameof(game)),
    };
}
