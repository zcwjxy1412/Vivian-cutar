# furina 品牌迁移：AI 兼容交接

2026-11-14。依据 integration/furina-vnext 的 **4a46ad0**，只读检索该提交，以下位置均指该基线。总控决定项目、程序集、namespace 和显示统一 furina；旧数据、DPAPI 格式、数据库名、跨版本锁及外部协议显式兼容。本交接不实施迁移。

## 可改名，但须同步的引用

| 范围 | 基线源码位置 | 同步要求 |
|---|---|---|
| AI 品牌提示 | `src/VIvian.Infrastructure/Assistant/AssistantSession.cs:46` | 两处 VIvian 显示名改为 furina；保留工具规则 |
| 应用文字 | `src/VIvian.App/App.xaml.cs:66`、`:171`、`:355`；`MainViewModel.cs:106`、`:1202`、`:1311`、`:1474` | 弹窗、版本和错误文字同步；进程文件名提示跟最终可执行文件一致 |
| 工程与程序集 | `VIvian.slnx`；`src/VIvian.App/VIvian.App.csproj:11`；各 csproj 的 ProjectReference | 工程路径、AssemblyName、namespace、packages.lock.json 项目依赖键一并更新，验证锁定 restore |
| friend assembly | App/Core/Infrastructure csproj 的 InternalsVisibleTo；`src/VIvian.Infrastructure/Engines/BetterGi/BetterGiAdapter.cs:10` | 同步测试程序集及 VIvian.BetterGiProbe，避免 internal 测试/探针失效 |
| WPF 类型与资源 | App/MainWindow/AssistantWindow/Mascot 的 x:Class；AssistantWindow.xaml:2；两个 app manifest:3 | CLR namespace 与 x:Class 同步；程序集 URI 跟 AssemblyName 一致 |
| 硬编码资源 URI | `tests/VIvian.Tests/App/FurinaWindowLayoutTests.cs:56`；`tests/VIvian.Tests/FurinaThemeVisualTests.cs:38`、`:62`；`tools/CourtPreview/PreviewMarkup.cs:28` | 更新 `/VIvian;component/`，否则编译通过仍可能运行时找不到资源 |
| 类型名筛选 | `src/VIvian.App/MainViewModel.cs:1052` | FullName.StartsWith("VIvian.") 随 namespace 更新，否则故障栈筛选丢失 |
| 测试路径与宿主 | FurinaWindowLayoutTests.cs:40、:52、:74；FurinaThemeVisualTests.cs:31、:35；ProcessSafetyGuardTests.cs:17、:39、:56、:68；EngineFrameworkTests.cs:223；IsolatedCliTests.cs:300–380 | 更新源码根定位、允许路径、FakeEngine/CrashHost 程序集和可执行文件名 |
| 构建部署 | `scripts/Build.ps1:21–32`；Build-M5_2Test.ps1:19–52；Update-UnifiedInstall.ps1:73、:116；`.github/workflows/release.yml:75` | 同步发布、烟测、快捷方式目标及 exe/dll 检查 |

## 必须保留的兼容边界

- **现有 AI 设置**：`AssistantCore.cs:401–430` 使用当前 dataDirectory 下 `assistant.json`，字段 BaseUrl/Model/ProtectedKey。密钥为 UTF-8 → DPAPI CurrentUser、null entropy → Base64；不绑定程序集名或 namespace。重命名无需解密重加密，不改文件名、字段、编码或保护作用域。MainViewModel.cs:529 注入数据目录。不要为盘点调用 Load，它会解密。
- **旧用户数据路径**：`UnifiedProfilePreparation.cs:11`、`:17–18` 的 VIvian-UserData 与 App.xaml.cs:140–147 的目录选择必须兼容。不能因显示改名创建空配置目录。`CopyPreparedProfileAsync`（约 :75–82）只快照 vivian.db，不会顺带复制 assistant.json，不能当完整设置迁移。
- **数据库身份与恢复**：UnifiedProfilePreparation.cs:13 的 vivian.db、:14 的 PreparedApplicationId=0x5649564E，以及 Update-UnifiedInstall.ps1:77、:92、:115 的数据库、WAL/SHM/journal 和恢复检查属于持久化兼容值，不能按品牌替换。
- **跨版本排他**：SingleInstanceGuard.cs:49 的 Local\\VIvian-；WindowActivation.cs:36–37 的 Local\\VIvian-Activate-/ActivateAck-；DesktopOperationLease.cs:23 的 `.vivian-m6/desktop-operation.lock`。换名会破坏旧版冲突识别或跨配置桌面锁，须显式保留兼容。
- **启动与安装兼容**：App.xaml.cs:72–90 的旧安装 marker、:143–147 的旧 profile、VIVIAN_* 环境入口保留兼容；MainViewModel.cs:1262 的既有 `E:\VIvianEngines` 引擎路径不能替换成不存在的目录。
- **八工具协议**：AssistantCore.cs:66–99 的 list_games、list_tasks、read_configuration、propose_changes、get_run_status、list_accounts、get_batch_progress、propose_run 不带品牌，不改名。保留 game=genshin/starrail/zzz、account_id/plan_id/batch_id/items/revision、routine/saved_account_settings、AutoRun 旧键和 saved_auto_run。
- **临时连接探针**：ChatCompletionsClient.cs:68、:71、:80 的 vivian_probe 是单次动态声明，可改为 furina_probe，但须与 AssistantConnectionTests.cs:119 同步；它不是持久化的八工具协议，历史验收证据不回写。
- **仓库外凭据与旧探针**：`C:/Users/胡桃/.codex/tools/deepseekhelper-launcher/Read-Key.ps1`、api-key.dpapi 不属于品牌迁移。旧固定 Config/Probe 的 DLL 路径、类型及哈希会因改名失效，只作历史证据，不修改凭据、不重跑旧入口。

## 未验证事项

本次没有改生产代码、读密钥、配置 API、部署或运行测试；未验证改名后的构建、WPF 资源、旧账号数据继续可读、现有 DeepSeek 设置解密和联网、旧版进程冲突拒绝。现有源码检索未发现 VIvian 专用 Type.GetType/Assembly.Load 字符串加载，但不能替代改名后的动态资源测试。总控应在视觉批次固定后统一改名，覆盖旧数据与设置保留、跨版本锁、friend assembly、发布脚本及锁定 restore。
