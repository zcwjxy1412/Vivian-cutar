# VIvian M3 面向 Codex 的适配器开发技术规约与验证清单 (M3_ADAPTER_IMPLEMENTATION_REQUIREMENTS.md)

**文档路径**：`docs/research/M3_ADAPTER_IMPLEMENTATION_REQUIREMENTS.md`  
**核查日期**：2026-11-04  
**目标读者**：Codex（主开发负责人）与 DeepSeek（独立代码审查负责人）  
**文档定位**：基于已证实的上游技术证据，直接回答「哪些接口已证实可用」、「哪些接口禁止使用」、「哪些操作有副作用」、「哪些版本纳入兼容矩阵」、「哪些功能需 Windows 实机验证」以及「三引擎推荐接入顺序」，作为 M3 生产适配器编码与 Code Review 的验收基准。

---

## 1. 已证实可直接用于 Codex 开发的接口清单（Confirmed Ready）

| 引擎 | 接口类别 | 已证实接口与调用契约 | 证据级别与依据 |
| :--- | :--- | :--- | :--- |
| **March7thAssistant (`v2026.10.31`)** | **核心任务 CLI** | `{Root}\March7th Assistant.exe <task>`（或 `{Root}\m7a.exe <task> --after-finish None`）<br>支持 `<task>`：`main`（完整运行）、`daily`（每日实训）、`power`（清体力）、`routine`（日常）、`currencywars`（货币战争）、`fight`（锄大地）、`universe`（模拟/差分宇宙）。 | **级别 B / C**：[`March7th Assistant.spec`](https://github.com/moesnow/March7thAssistant/blob/main/March7th%20Assistant.spec)、[README.md](https://github.com/moesnow/March7thAssistant/blob/main/README.md)、[m7a.top](https://m7a.top/) |
| **March7thAssistant (`v2026.10.31`)** | **GUI 带参启动** | `{Root}\March7th Launcher.exe <task> -e`（启动 GUI 执行指定任务，正常完成后 `-e` 自动退出程序） | **级别 C**：[使用教程·命令行启动](https://m7a.top/docs/) |
| **March7thAssistant (`v2026.10.31`)** | **配置补丁契约** | `{Root}\config.yaml`（UTF-8 扁平 YAML）。执行前必须确保：<br>- `pause_after_success: false`（防成功后控制台 `input()` 挂起）<br>- `exit_after_failure: true`（失败立即非零退出）<br>- `check_update: false` & `auto_update: false`（禁用内置更新弹窗）<br>- `genshin_starRail_fps_unlocker_allow: false`（强制禁用内存解锁帧率） | **级别 B**：[`assets/config/config.example.yaml`](https://github.com/moesnow/March7thAssistant/blob/main/assets/config/config.example.yaml) |
| **BetterGI (`v0.66.0`)** | **一条龙与配置组 CLI** | 1. `{Root}\BetterGI.exe startOneDragon`（执行当前选中配置）<br>2. `{Root}\BetterGI.exe startOneDragon <ConfigName>`（执行 `User\OneDragon\<ConfigName>.json`，不带 `<>`）<br>3. `{Root}\BetterGI.exe --startGroups <Group1> <Group2>`（顺序执行 `User\ScriptGroup\*.json`） | **级别 B**：[`OneDragonFlowViewModel.cs` L572-598](https://gitback.org/babalae/better-genshin-impact/src/commit/436a8d207ee302763edb207a545093b4cf22340d/BetterGenshinImpact/ViewModel/Pages/OneDragonFlowViewModel.cs)、[Commit `a9150054c4`](https://gitback.org/babalae/better-genshin-impact/src/commit/a9150054c461d22824bba84bad2ba8e02c74de38) |
| **BetterGI (`v0.66.0`)** | **配置与日志契约** | 1. 配置文件：`{Root}\User\OneDragon\<ConfigName>.json`（含 `TaskEnabledList` 字典与 `CompletionAction: "关闭游戏和软件"`）<br>2. 日志文件：`{Root}\log\better-genshin-impact{yyyyMMdd}.log`（UTF-8，共享只读流读取）<br>3. 完成证据：增量日志顺序出现 `启用一条龙配置：` $\rightarrow$ `一条龙任务执行: X/Y` $\rightarrow$ `一条龙和配置组任务结束`（且无 `任务被取消，退出执行`）。 | **级别 B**：[`App.xaml.cs`](https://gitback.org/babalae/better-genshin-impact/src/commit/2a2acb5b0df635a5293507916fa1d1db3cab4aaf/BetterGenshinImpact/App.xaml.cs)、[`OneDragonFlowViewModel.cs`](https://gitback.org/babalae/better-genshin-impact/src/commit/436a8d207ee302763edb207a545093b4cf22340d/BetterGenshinImpact/ViewModel/Pages/OneDragonFlowViewModel.cs) |
| **ZZZ-OneDragon (`v2.6.0`)** | **集成启动器 CLI** | `{Root}\OneDragon-RuntimeLauncher.exe -o -c`（或带 `-i 1,2` 指定多账号实例；`-v` 查版本；`-h` 查帮助）。<br>前置强校验：`{Root}` 路径必须匹配 `^[a-zA-Z]:\\[a-zA-Z0-9_\-\\]+$`（全英文、无空格、无中文）。 | **级别 B / C**：[快速开始文档](https://one-dragon.com/zzz/zh/quickstart.html)、[千机链文档](https://one-dragon.com/tools/zh/script_chainer.html) |

---

## 2. 尚不能使用或明确禁止使用的接口清单（Prohibited / Not Ready）

1. **禁止在 `BetterGI v0.66.0` 适配器中调用 `ExternalAccess` (HTTP/WS/MCP) 或 `Pulonia` 接口**：
   - **原因**：`BetterGenshinImpact/Service/ExternalAccess/` 与 `User/Pulonia/` 仅存在于尚未发布正式版的 `refactor/20260930` 开发分支（见 [`CONTEXT.md`](https://github.com/babalae/better-genshin-impact/blob/refactor/20260930/CONTEXT.md)），在当前稳定版 `v0.66.0` 中完全不存在。
2. **禁止在 `BetterGI` 运行期间修改 `User/config.json`**：
   - **原因**：`BetterGI` 进程持有内存态 `AllConfig` 单例，并在运行期和退出时回写磁盘，运行期外部修改会被直接覆盖丢失。所有配置修改必须在确认 `BetterGI.exe` 未运行的前提下进行。
3. **禁止使用 `ZZZ-OneDragon` 的原始启动器 (`OneDragon-Launcher.exe`) 作为默认受管入口**：
   - **原因**：原始启动器会派生独立的 `python.exe` 子进程并可能提前退出，导致 `ProcessRunner` 误判任务完成并丢失子进程控制权（见 [千机链 3.2 节](https://one-dragon.com/tools/zh/script_chainer.html)）。M3 阶段统一要求绑定 `OneDragon-RuntimeLauncher.exe`。
4. **禁止将 `ZZZ-OneDragon` 的开发调试 MCP Server (`zzz_od.backend.entry.server :23001`) 作为日常任务触发器**：
   - **原因**：该 MCP Server 面向开发调试（截图/OCR/单步操作，见 [`docs/develop/setup/quickstart.md`](https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon/blob/main/docs/develop/setup/quickstart.md)），未封装完整的一条龙生命周期与完成后关游戏逻辑。
5. **禁止任何仅凭 `ExitCode == 0` 判定任务成功的逻辑**：
   - **原因**：BetterGI 内部子任务异常被 `catch` 后关闭软件仍返回 `0`；March7th 所有任务被跳过时也返回 `0`。必须执行「退出码 + 增量日志完成事件 + 无致命错误事件」复合判定。

---

## 3. 已知调用副作用与防护规约（Side-Effect Mitigation）

| 操作动作 | 产生的具体副作用 | Codex 适配器实现防护要求 |
| :--- | :--- | :--- |
| 调用 `m7a.exe --status` 或 `--list` | 可能在安装目录创建并删除写权限测试临时文件；若首次解压尚未生成 `config.yaml`，可能触发默认配置文件生成与旧定时字段迁移。 | 1. 不要在只读目录或未完成备份前对生产目录高频轮询；<br>2. 自动化集成测试必须在 `%TEMP%` 隔离副本中运行并校验前后文件差异。 |
| 启动 `BetterGI.exe` | 每次启动时 `OnStartup` 自动调用 `UrlProtocolHelper.RegisterAsync()` 修改 Windows 注册表 `HKCR\bettergi` 协议指向当前启动路径。 | 若机器上存在多个 BetterGI 副本（如测试副本与生产副本），运行测试副本会篡改用户的 `bettergi://` 注册表指向。因此**严禁在非用户正式目录上随意启动 `BetterGI.exe`**。 |
| 启动 `OneDragon-RuntimeLauncher.exe` | 默认配置下启动器会在启动时自动联网拉取最新 Git 代码并覆盖本地 `src/`。 | 在启动前必须读取并确保 `config/env.yml` 中已关闭自动代码同步，防止未经兼容性验证的代码在无人值守时被拉取执行。 |

---

## 4. 纳入 M3 兼容性矩阵的基准版本清单

| 引擎名称 | M3 首选基准稳定版 (`Verified`) | 兼容历史版本范围 | 明确标记不兼容/高风险版本 (`Broken`) |
| :--- | :--- | :--- | :--- |
| **March7thAssistant** | **`v2026.10.31`** (Commit `1e77d59`) | `v2026.9.30` ~ `v2026.10.31`（若使用 `March7th Assistant.exe` 可向下兼容至 `v2026.8.28`） | `< v2026.8.28`（PR #1176 之前存在非零退出码仍提示完成的缺陷） |
| **BetterGI** | **`v0.66.0`** (Commit `0af68c2`) | `v0.64.0`, `v0.66.0` | `v0.63.0`, `v0.65.0`（官方明确标注底层重构 BUG 较多）；`refactor/20260930` 分支预览版（Pulonia 配置结构不兼容） |
| **ZZZ-OneDragon** | **`v2.6.0`** (Commit `5f2e473`) | `v2.5.1` ~ `v2.6.0` (`WithRuntime-Full` 系列) | `< v2.4.4`（早期启动器存在工作路径设置错误 Bug，见 Release `v2.4.4` 说明） |

---

## 5. 需要 Codex 在 Windows 实机执行的 5 项最小验证清单（Level D $\rightarrow$ Level A）

1. **验证 1（发行包 SHA-256 实包校验）**：运行 `M3_UPSTREAM_RELEASE_VERIFICATION.md` 第 4 节的 PowerShell 脚本，核对三款官方发行包真实字节的 SHA-256 与 GitHub Release 公布值完全一致。
2. **验证 2（`v2026.10.31` 包内 `m7a.exe` 存在性及 `--status` 副作用测试）**：
   - 解压 `March7thAssistant_full.7z` 至临时隔离目录，确认根目录是否包含 `m7a.exe`（若 `v2026.10.31` 打标早于 Commit `6350047`，则记录 `m7a.exe` 仅在 `6350047+` 存在，并在 `v2026.10.31` 适配器中默认使用 `March7th Assistant.exe`）。
   - 在隔离目录对 `--status`、`--list --json`、`-h` 执行前后做文件树快照对比，记录所有临时文件创建/删除与 `config.yaml` 写入行为。
3. **验证 3（`BetterGI v0.66.0` 完成退出闭环验证）**：在实机检验 `CompletionAction = "关闭游戏和软件"` 下日志输出 `一条龙和配置组任务结束` 后，`BetterGI.exe` 进程是否在 10 秒内自行退出。
4. **验证 4（`ZZZ-OneDragon v2.6.0` 实包 `config/` 模板与进程树采集）**：解压 `ZenlessZoneZero-OneDragon-v2.6.0-WithRuntime-Full.zip`，记录 `config/` 下默认 YAML 文件名与键名，并运行 `OneDragon-RuntimeLauncher.exe -v` / `-h` 观察是否派生子进程。
5. **验证 5（Job Object 权限继承验证）**：在管理员权限下通过 `WindowsProcessSupervisor` 启动三款引擎的 `--help` / `-v`，确认退出码不为 `559` 且无脱离 Job Object 的子进程。

---

## 6. 三款引擎推荐接入顺序与工程理由

### 第一顺位：崩坏：星穹铁道 — `March7thAssistant`（最优先接入）
- **核心技术证据**：
  1. **原生无 GUI 控制台模式**：`March7th Assistant.exe`（及 `m7a.exe`）打包时显式排除了 `PySide6`（[`March7th Assistant.spec` L37](https://github.com/moesnow/March7thAssistant/blob/main/March7th%20Assistant.spec)），支持纯正的 `StandardOutput` / `StandardError` UTF-8 流式捕获，无模态 MessageBox 阻塞风险。
  2. **配置模型 100% 透明**：705 行 [`assets/config/config.example.yaml`](https://github.com/moesnow/March7thAssistant/blob/main/assets/config/config.example.yaml) 已在源码中完整核实，扁平键值对结构极易通过 `YamlDotNet` 进行非阻塞补丁注入。
  3. **CLI 直接支持细分子任务**：支持在命令行直接传入 `main`, `daily`, `power` 等子任务 ID，无需复杂的配置组文件拼接。

### 第二顺位：原神 — `BetterGI`（次优先接入）
- **核心技术证据**：
  1. **C# 源码级白盒确定性**：[`App.xaml.cs`](https://gitback.org/babalae/better-genshin-impact/src/commit/2a2acb5b0df635a5293507916fa1d1db3cab4aaf/BetterGenshinImpact/App.xaml.cs)、[`MainWindowViewModel.cs`](https://gitback.org/babalae/better-genshin-impact/src/commit/900b6aec84911b8af00042e60984beb7ffbc4a0a/BetterGenshinImpact/ViewModel/MainWindowViewModel.cs) 与 [`OneDragonFlowViewModel.cs`](https://gitback.org/babalae/better-genshin-impact/src/commit/436a8d207ee302763edb207a545093b4cf22340d/BetterGenshinImpact/ViewModel/Pages/OneDragonFlowViewModel.cs) 已 100% 证实 `startOneDragon` 启动流、JSON 配置路径（`User/OneDragon/*.json`）以及 Serilog 日志完成标志（`一条龙和配置组任务结束`）。
  2. **排在第二的原因**：它是 WPF GUI 进程（不写父进程 stdout 管道），需要依赖 Codex 的通用日志文件尾随器（Log File Tailer）以及启动前单实例互斥清理逻辑。

### 第三顺位：绝区零 — `ZenlessZoneZero-OneDragon`（第三顺位接入）
- **核心技术证据**：
  1. 虽然官方提供了集成启动器 `OneDragon-RuntimeLauncher.exe -o -c [-i 1,2]`，但其运行期生成的 `config/*.yml` 被 `.gitignore` 排除在源码仓之外，且存在纯英文无空格路径硬约束与潜在的 PyInstaller 子进程拓扑，需先完成实包解压探查后再接入。
