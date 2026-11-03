# Vivian V1.0 上游开源项目深度审计报告 (UPSTREAM_AUDIT.md)

**文档路径**：`docs/research/UPSTREAM_AUDIT.md`  
**审计基准日期**：2026-11-03  
**审计范围**：
1. 原神（Genshin Impact）：[babalae/better-genshin-impact](https://github.com/babalae/better-genshin-impact)
2. 崩坏：星穹铁道（Honkai: Star Rail）：[moesnow/March7thAssistant](https://github.com/moesnow/March7thAssistant)
3. 绝区零（Zenless Zone Zero）：[OneDragon-Anything/ZenlessZoneZero-OneDragon](https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon)

**证据等级定义**：
- **[已从源码验证]**：直接读取仓库源码文件、构建脚本（GitHub Actions / PyInstaller `.spec` / `.csproj`）或配置文件模板确认的事实。
- **[仅文档声明]**：由官方 Wiki、README、Release Notes、Issue 维护者回复或官方配套工具（如千机链）文档声明，未直接下钻至对应行号实现的事实。
- **[尚待验证]**：受限于静态审计环境（无实机 Windows 管理员环境与游戏客户端运行时），需在原型阶段由 Codex 通过实机运行验证的行为。

---

## 1. 三款上游项目横向总览对比

| 审计维度 | BetterGI (更好的原神) | March7thAssistant (三月七小助手) | ZenlessZoneZero-OneDragon (绝区零一条龙) |
| :--- | :--- | :--- | :--- |
| **仓库地址** | [babalae/better-genshin-impact](https://github.com/babalae/better-genshin-impact) | [moesnow/March7thAssistant](https://github.com/moesnow/March7thAssistant) | [OneDragon-Anything/ZenlessZoneZero-OneDragon](https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon) |
| **维护状态** | 极度活跃（Star 17.6k+）[已从源码验证] | 极度活跃（Star 12.3k+）[已从源码验证] | 极度活跃（Star 7.8k+）[已从源码验证] |
| **最新稳定版** | `v0.66.0`（2026-09-26 发布）[已从源码验证] | `v2026.10.31`（2026-10-31 发布）[已从源码验证] | `v2.6.0`（2026-10-30 发布）[已从源码验证] |
| **开发语言与栈** | C# / .NET 8（`refactor/20260930` 分支已升 .NET 10）+ WPF-UI 4.0.2 + OpenCvSharp4 + ONNX [已从源码验证] | Python 3.12~3.14 + PySide6 + RapidOCR + ONNX/OpenVINO [已从源码验证] | Python >=3.11.9,<3.12 + PySide6 6.11.1 + YOLOv8/ONNX + `uv` [已从源码验证] |
| **开源许可证** | GNU GPL v3.0 ([LICENSE](https://github.com/babalae/better-genshin-impact/blob/main/LICENSE)) | GNU GPL v3.0 ([LICENSE](https://github.com/moesnow/March7thAssistant/blob/main/LICENSE)) | GNU GPL v3.0 ([ZenlessZoneZero-OneDragon](https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon)) |
| **管理员权限** | 强制要求（`.UseElevated()`，非管理员退出码 `559` 自重启）[已从源码验证] | 任务执行强制要求（`uac_admin=True`）；`m7a.exe` 查询免提权 [已从源码验证] | 强制要求（启动器请求 UAC 提权）[仅文档声明] |
| **核心 CLI 入口** | `BetterGI.exe startOneDragon [配置名]` / `--startGroups` [已从源码验证] | `m7a.exe` / `March7th Assistant.exe` / `March7th Launcher.exe` [已从源码验证] | `OneDragon-RuntimeLauncher.exe -o -c [-i 实例]` [仅文档声明] |
| **单实例锁限制** | 强限制：`.UseSingleInstance("BetterGI")` / `.UseInstanceIpc()` [已从源码验证] | 支持单实例运行检测（`active_run` 状态查询）[仅文档声明] | UI 标签页互斥（一条龙与应用运行不可同时启用）[仅文档声明] |
| **后台运行能力** | 支持“桌面分身”（本地 RDP 子会话 `ChildSession`）[已从源码验证] | 支持 Windows 后台模式（v2026.10.31 新增）及云游戏无窗口模式 [仅文档声明] | 测试版支持后台模式（依赖 `vgamepad` / `ViGEmBus` + `PrintWindow`）[仅文档声明] |

---

## 2. 上游项目一：BetterGI（更好的原神）深度审计

### 2.1 当前维护状态、稳定发布版与近期重大变更
- **维护状态**：**[已从源码验证]** 处于极高频维护状态，累计 17.6k Stars、146+ 贡献者（见 [GitHub Releases](https://github.com/babalae/better-genshin-impact/releases) 与 [AGENTS.md](https://github.com/babalae/better-genshin-impact/blob/refactor/20260930/AGENTS.md)）。原文档站点 `bgi.huiyadan.com` 已在提交 [`b130351d8d`](https://gitback.org/babalae/better-genshin-impact/commit/b130351d8d8724cb8191ed101c7b65526790608e?files=BetterGenshinImpact%2FViewModel%2FPages) 迁移至 [bettergi.com](https://www.bettergi.com/)。
- **最新稳定发布版**：**[已从源码验证]** `v0.66.0`（标题：`0.66 7.1 适配`，Commit `0af68c2`，发布于 2026-09-26），提供安装包 `BetterGI.Install.0.66.0.exe`（481 MB）与绿色解压包 `BetterGI_v0.66.0.7z`（444 MB）（见 [Releases](https://github.com/babalae/better-genshin-impact/releases)）。
- **近期重大变更（v0.62 ~ v0.66 及 `refactor/20260930` 分支）**：**[已从源码验证]**
  - **`refactor/20260930` 开发主分支（2026-10 ~ 2026-11）**：升级至 **.NET 10**（`net10.0-windows10.0.22621.0`）并改为自包含发布（见 [提交 `b31da1c914`](https://gitback.org/babalae/better-genshin-impact/commit/b31da1c914a9bd00621d68ff16746304818e0261?files=.github%2Fworkflows)）；新增本地 **ExternalAccess 服务（HTTP / WebSocket / MCP，`BetterGenshinImpact/Service/ExternalAccess/`）**；推进 **Pulonia 任务计划调度系统**（统一普通任务与一条龙，引入 `User/Pulonia/accounts.json`，见 [`CONTEXT.md`](https://github.com/babalae/better-genshin-impact/blob/refactor/20260930/CONTEXT.md)）。
  - **v0.66.0（2026-09-26）**：适配原神 7.1 新角色；一条龙首领讨伐支持单次及跨运行领奖上限；自动秘境至冬交互改用 OCR 选择；物品图标默认启用 `ItemV2` 模型（见 [Releases](https://github.com/babalae/better-genshin-impact/releases)）。
  - **v0.65.0（2026-09-16）**：适配原神 7.0 地图；新增 Windows Graphics Capture V2 截图模式；新增实验性 LLM 战斗行为树生成；新增 QQ 官方通知与微信 Clawbot 通知；一条龙结束后支持推送树脂与每日奖励汇总（见 [Releases](https://github.com/babalae/better-genshin-impact/releases)）。
  - **v0.63.0（2026-08-06） & v0.64.0（2026-08-23）**：新增“桌面分身”功能（基于 `wtsapi32` Child Sessions 与 `MsRdpClient10` ActiveX 控件实现本地独立会话后台运行，代码位于 `BetterGenshinImpact/Service/ChildSession/` 和 `BetterGenshinImpact/View/Controls/ChildSession/RdpActiveXHost.cs`，见 [Issue #1180 引用验证](https://github.com/moesnow/March7thAssistant/issues/1180)）；日志引入多实例标识 `[{BgiInstance}]`（见 [Releases](https://github.com/babalae/better-genshin-impact/releases)）。

### 2.2 官方可用的启动、停止、任务执行与状态查询接口
- **启动与任务执行接口**：**[已从源码验证]**
  1. **一条龙 CLI 启动**：在 [`BetterGenshinImpact/ViewModel/Pages/OneDragonFlowViewModel.cs`](https://gitback.org/babalae/better-genshin-impact/src/commit/436a8d207ee302763edb207a545093b4cf22340d/BetterGenshinImpact/ViewModel/Pages/OneDragonFlowViewModel.cs) 第 572~598 行的 `OnLoaded()` 方法中，程序通过 `Environment.GetCommandLineArgs()` 解析命令行参数：
     - `BetterGI.exe startOneDragon` 或 `BetterGI.exe --startOneDragon`（因判断条件为 `args[1].Contains("startOneDragon")`）
     - `BetterGI.exe startOneDragon <配置名称>`（如 `BetterGI.exe startOneDragon 默认配置`，注意不能带尖括号 `<>`，见 [Issue #2280](https://github.com/babalae/better-genshin-impact/issues/2280)）
  2. **配置组调度器 CLI 启动**：`BetterGI.exe --startGroups 组1 组2 ...`，按顺序连续执行 `User\ScriptGroup\` 下的配置组（验证自 [提交 `a9150054c4` (#1309)](https://gitback.org/babalae/better-genshin-impact/src/commit/a9150054c461d22824bba84bad2ba8e02c74de38)）。
  3. **仅启动截图器与游戏 CLI**：`BetterGI.exe start`（验证自 [Issue #359](https://github.com/babalae/better-genshin-impact/issues/359)）。
  4. **URI Scheme 协议**：`bettergi://start`（在 [`App.xaml.cs`](https://gitback.org/babalae/better-genshin-impact/src/commit/2a2acb5b0df635a5293507916fa1d1db3cab4aaf/BetterGenshinImpact/App.xaml.cs) 启动时调用 `UrlProtocolHelper.RegisterAsync()` 注册）。
  5. **命令行启动免弹窗机制**：在 [`BetterGenshinImpact/ViewModel/MainWindowViewModel.cs`](https://gitback.org/babalae/better-genshin-impact/src/commit/900b6aec84911b8af00042e60984beb7ffbc4a0a/BetterGenshinImpact/ViewModel/MainWindowViewModel.cs) 的 `OnLoaded()` 中，当 `Environment.GetCommandLineArgs().Length > 1` 时，程序在完成 `OcrPreheating()` 后直接 `return;`，自动跳过更新检查弹窗、首次运行欢迎弹窗和目录迁移弹窗。
- **停止接口**：**[已从源码验证]**
  - 内部通过 `CancellationContext.Instance.Cts` 控制取消；一条龙配置支持 `CompletionAction` 设为 `"关闭游戏和软件"`，执行完毕后调用 `SystemControl.CloseGame()` 与 `Application.Current.Shutdown()`（见 [`OneDragonFlowViewModel.cs`](https://gitback.org/babalae/better-genshin-impact/src/commit/436a8d207ee302763edb207a545093b4cf22340d/BetterGenshinImpact/ViewModel/Pages/OneDragonFlowViewModel.cs)）。但需注意历史 [Issue #2049](https://github.com/babalae/better-genshin-impact/issues/2049) 曾反馈 `--startOneDragon` 启动后偶发不触发自动退出，外部管理器必须实现日志结束标志检测 + 进程兜底清理（`Kill`）。

### 2.3 日常任务类型与启动前置条件
- **日常任务类型（一条龙内置 + 配置组）**：**[已从源码验证]**
  - 一条龙任务节点（见 [`OneDragonFlowViewModel.cs`](https://gitback.org/babalae/better-genshin-impact/src/commit/436a8d207ee302763edb207a545093b4cf22340d/BetterGenshinImpact/ViewModel/Pages/OneDragonFlowViewModel.cs) 及 [`GameTask/Common/Job/`](https://gitback.org/babalae/better-genshin-impact/commit/df84cc9ef163f76aef6f1a7f5292e0b585845a99?files=BetterGenshinImpact%2FGameTask%2FCommon)）：`领取邮件`（`ClaimMailRewardsTask`）、`合成树脂`（`GoToCraftingBenchTask`）、`自动秘境`（`AutoDomainTask`）、`自动首领讨伐`（`AutoBossTask`）、`自动幽境危战`（`AutoStygianOnslaught`）、`自动刷地脉花`（`AutoLeyLineOutcrop`）、`领取每日奖励`（凯瑟琳历练点领奖与重新派遣）、`领取尘歌壶奖励`、`自动七圣召唤`，以及自动追加在末尾的 `CheckRewardsTask` / `ClaimBattlePassRewardsTask`（纪行奖励领取）和任意挂载的 `User/ScriptGroup/*.json`（采集/挖矿/锄地）。
- **启动前置条件**：**[已从源码验证]**（见 [README.md](https://github.com/babalae/better-genshin-impact/blob/main/README.md) 与 [地图追踪文档](https://www.bettergi.com/feats/autos/pathing.html)）
  1. 操作系统：Windows 10/11 64 位，v0.66.0 需 .NET 8 Runtime（新分支已内嵌 .NET 10）。
  2. 权限：**必须以管理员权限启动**。若非管理员启动，[`RuntimeHelper.cs`](https://gitback.org/babalae/better-genshin-impact/src/commit/4bf302b01325400342e09fd679532e78e2609be1/BetterGenshinImpact/Helpers/RuntimeHelper.cs) 会以 `Verb = "runas"` 重启自身并以退出码 `559` 退出原进程，导致父进程丢失句柄。
  3. 画面与分辨率：仅支持 16:9 分辨率，推荐 `1920x1080` 窗口化运行；**严禁开启任何画面滤镜（HDR、NVIDIA 滤镜等）**，小地图需设为“锁定方向”。

### 2.4 任务成功、失败或中断的可靠判断方式
- **特殊进程退出码**：**[已从源码验证]**（见 [RuntimeHelper.cs](https://www.google.com/search?q=https://gitback.org/babalae/better-genshin-impact/src/commit/4bf302b01325400342e09be1/BetterGenshinImpact/Helpers/RuntimeHelper.cs)）
  - `0`：WPF `Application.Current.Shutdown()` 正常关闭（**注意**：即使子任务内部捕获异常，只要流程走完关闭软件，退出码也是 `0`，不能单独作为成功依据）。
  - `559`：非管理员启动触发 `runas` 提权自重启，原进程立即退出。
  - `65535`（`0xFFFF`）：触发单实例锁退出。
- **日志路径与关键特征**：**[已从源码验证]** 位于 `{AppContext.BaseDirectory}/log/better-genshin-impact{yyyyMMdd}.log`（见 [`App.xaml.cs`](https://gitback.org/babalae/better-genshin-impact/src/commit/b2631ed362907b786d7a63f360509f9f119e0d52/BetterGenshinImpact/App.xaml.cs)）。
  - 启动标志：`启用一条龙配置：{SelectedConfig.Name}`、`一条龙启动`。
  - 进度标志：`一条龙任务执行: {current}/{total}`、`配置组任务执行: {current}/{total}`、`→ {TaskName}任务启动！`、`→ {TaskName}任务结束`。
  - 成功结束标志：`一条龙和配置组任务结束`（且之前未出现 `任务被取消，退出执行`）。
  - 取消/中断标志：`任务被取消，退出执行`、`任务中断:任务被取消`。
  - 错误/异常标志：`任务启动失败：当前存在正在运行中的独立任务`、`UnHandle Exception [FATAL]`、`PaddleOcr预热异常`。

### 2.5 配置文件、更新方式与外部调用风险
- **配置文件位置**：**[已从源码验证]**
  - 全局主配置：`{InstallDir}/User/config.json`（注意：BetterGI 退出时会将内存中的 `AllConfig` 写回磁盘覆盖该文件，**修改前必须确保 `BetterGI.exe` 未运行**）。
  - 一条龙配置：`{InstallDir}/User/OneDragon/*.json`。
  - 调度器配置组：`{InstallDir}/User/ScriptGroup/*.json`。
- **外部调用风险**：**[已从源码验证]**
  - 致命异常模态弹窗阻塞：`App.xaml.cs` 的 `HandleException` 在遇到致命异常（`isTerminating == true`）时会弹出模态窗口等待人工点击确认，外部调度器必须设置硬超时（Timeout）并强制终止进程树。

---

## 3. 上游项目二：March7thAssistant（三月七小助手）深度审计

### 3.1 当前维护状态、稳定发布版与近期重大变更
- **维护状态**：**[已从源码验证]** 极度活跃，Star 12.3k+，累计下载 220 万+（见 [March7thAssistant GitHub](https://github.com/moesnow/March7thAssistant) 与 [官网 m7a.top](https://m7a.top/)）。
- **最新稳定发布版**：**[已从源码验证]** `v2026.10.31`（Commit `1e77d59`，发布于 2026-10-31），发布资产包括完整包 `March7thAssistant_full.7z`（452 MB）、`March7thAssistant_full.zip`（686 MB）及增量包 `update.7z`（156 MB）（见 [Releases](https://github.com/moesnow/March7thAssistant/releases)）。
- **近期重大变更（v2026.8.28 ~ v2026.10.31）**：**[已从源码验证]**
  - **v2026.10.31（2026-10-31）**：新增面向外部脚本与 AI Agent 的免提权命令行入口 `m7a.exe`（见 [`March7th Assistant.spec`](https://github.com/moesnow/March7thAssistant/blob/main/March7th%20Assistant.spec) 与 [官网说明](https://m7a.top/)）；新增 Windows 本地游戏后台运行模式（支持后台键鼠操作）；定时任务支持按星期执行（见 [Releases](https://github.com/moesnow/March7thAssistant/releases)）。
  - **v2026.9.30 & v2026.8.28**：新增任务暂停/继续功能（默认快捷键 `F8`）；命令行支持列出与运行自定义流程；**修复定时任务非零退出码时仍提示完成的问题（#1176）**（见 [Releases](https://github.com/moesnow/March7thAssistant/releases)）。

### 3.2 官方可用的启动、停止、任务执行与状态查询接口
- **三大可执行入口（已从 [`March7th Assistant.spec`](https://github.com/moesnow/March7thAssistant/blob/main/March7th%20Assistant.spec)、[`.github/workflows/2.realtime_release.yml`](https://github.com/moesnow/March7thAssistant/blob/main/.github/workflows/2.realtime_release.yml) 及 [官方文档](https://m7a.top/docs/) 验证）**：
  1. `m7a.exe` **[已从源码验证]**：v2026.10.31 新增的 CLI 专用入口（在 `March7th Assistant.spec` 中定义 `name='m7a', console=True, uac_admin=False`）。未提权时可直接查询（`--status` / `--list --json` 等）；运行任务时再通过 UAC 启动 `March7th Assistant.exe`，并把输出与退出码转回调用方终端（见 `utils/cli_entry.py`）。
  2. `March7th Assistant.exe` **[已从源码验证]**：带管理员清单（`uac_admin=True, console=True`）的核心任务执行器（打包自 `main.py`，排除 `PySide6`）。
  3. `March7th Launcher.exe` **[已从源码验证]**：PySide6 图形界面启动器（打包自 `app.py`），支持 `March7th Launcher.exe main -e`（正常完成后自动退出程序）、`March7th Launcher.exe -l`（列出任务）、`March7th Launcher.exe -h`（见 [使用教程](https://m7a.top/docs/)）。
- **已验证的命令行参数（见 [官网 m7a.top](https://m7a.top/) 与 [使用教程](https://m7a.top/docs/)）**：
  - 状态查询（JSON 输出）：`.\m7a.exe --status`（返回 `{ "first_run_completed": true, "game_path_exists": true, "active_run": null, ... }`）**[仅文档声明]**。
  - 任务列表查询（JSON 输出）：`.\m7a.exe --list --json` 或 `-l` **[仅文档声明]**。
  - 执行指定任务并覆盖完成后动作：`.\m7a.exe <task> --after-finish None`（支持 `main`, `routine`, `daily`, `power`, `currencywars`, `fight`, `universe`）**[仅文档声明]**。
  - 实时追踪最近一次运行日志：`.\m7a.exe --follow latest` **[仅文档声明]**。

### 3.3 日常任务类型、完成判定与配置文件
- **完整运行（`main`）串联流程**：**[已从源码验证]**（见 [`assets/config/config.example.yaml`](https://github.com/moesnow/March7thAssistant/blob/main/assets/config/config.example.yaml) 与 [使用教程](https://m7a.top/docs/)）按启用开关与刷新时间戳（每日凌晨 04:00、每周一凌晨 04:00）自动串行执行：兑换码领取 → 每日实训 → 活动检测 → 历战余响 → 清体力 → 货币战争积分奖励 → 差分宇宙积分奖励 → 锄大地（调用 `./3rdparty/Fhoe-Rail`） → 模拟宇宙/差分宇宙（`./3rdparty/Auto_Simulated_Universe`） → 逐光捡金（混沌回忆 / 虚构叙事 / 末日幻影） → 每周清理遗器 → 领取奖励。
- **退出码与阻塞配置陷阱**：**[已从源码验证]** 在 `assets/config/config.example.yaml` 中，默认 `pause_after_success: true`、`exit_after_failure: false`（执行完会停留在控制台阻塞等待按键）。**Vivian 适配器在调用前必须确保 `config.yaml` 中 `pause_after_success: false`、`exit_after_failure: true`、`check_update: false`、`auto_update: false`**，从而使进程在任务完成后立即返回标准退出码（`0` 成功，非 `0` 失败）。

---

## 4. 上游项目三：ZenlessZoneZero-OneDragon（绝区零一条龙）深度审计

### 4.1 当前维护状态、稳定发布版与近期重大变更
- **维护状态与最新版**：**[已从源码验证]** Star 7.8k+，最新稳定版 `v2.6.0`（Commit `5f2e473`，发布于 2026-10-30），由 SignPath Foundation 提供免费数字签名（见 [Releases](https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon/releases) 与 [build-release.yml](https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon/blob/main/.github/workflows/build-release.yml)）。
- **核心发布包选型**：**[已从源码验证]** 提供 `ZenlessZoneZero-OneDragon-v2.6.0-WithRuntime-Full.zip`（269 MB，内嵌 `OneDragon-RuntimeLauncher.exe` + `.runtime` Python 3.11.12 环境 + `src/` 源码 + `models/` 模型，解压即用）。

### 4.2 官方可用的启动、停止、任务执行与状态查询接口
- **启动器命令行接口**：**[仅文档声明]**（由官方 [快速开始-高级启动参数](https://one-dragon.com/zzz/zh/quickstart.html) 与 [千机链文档](https://one-dragon.com/tools/zh/script_chainer.html) 验证）
  - 入口：`OneDragon-RuntimeLauncher.exe`（集成启动器，进程名固定为自身，不会像普通启动器那样派生难以区分的 `python.exe`）。
  - 参数：`-h, --help`、`-v, --version`、`-o, --onedragon`（直接运行一条龙）、`-c, --close-game`（运行后关闭游戏）、`-s [SHUTDOWN], --shutdown [SHUTDOWN]`、`-i INSTANCE, --instance INSTANCE`（指定运行的账号实例，如 `1,2`）。
  - 标准调用命令：`OneDragon-RuntimeLauncher.exe -o -c` 或 `OneDragon-RuntimeLauncher.exe -o -c -i 1,2`。
- **MCP 后端接口**：**[已从源码验证]** [`docs/develop/setup/quickstart.md`](https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon/blob/main/docs/develop/setup/quickstart.md) 确认存在 `uv run python -m zzz_od.backend.entry.server`（监听 `http://127.0.0.1:23001/mcp`），暴露窗口状态、截图、OCR 与进游戏工具。

### 4.3 日常任务类型、前置条件与配置文件
- **日常任务类型（19 项）**：**[仅文档声明]**（见 [一条龙功能文档](https://one-dragon.com/zzz/zh/feat/feat_one_dragon/onedragon.html)）`兑换码`、`邮件`、`每日签到`、`录像店营业`、`咖啡店`、`体力刷本`、`活跃度奖励`、`丽都城募`、`丽都周纪`、`恶名狩猎`、`迷失之地`、`枯萎之都`、`式舆防卫战`、`情报板`、`随便观`、`锄大地`、`真·拿命验收`、`驱动盘拆解`、`通知`。
- **强制路径约束**：**[已从源码验证]** **必须安装在全英文、无空格、无中文字符、路径不过长的固态硬盘目录下（例如 `D:\ZZZ-OD`）**（见 [快速开始](https://one-dragon.com/zzz/zh/quickstart.html)）。
- **配置文件与日志路径**：**[已从源码验证]**（见 [`.gitignore`](https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon/blob/main/.gitignore)）
  - 配置文件：`config/env.yml`、`config/game.yml`、`config/one_dragon.yml`、`config/zzz_one_dragon.yml` 及多实例目录 `config/[0-9]*/`。
  - 日志文件：`.log/` 目录（格式：`[HH:mm:ss.fff] [filename.py line] [LEVEL]: message`，见 [Issue #2545](https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon/issues/2545)）。
