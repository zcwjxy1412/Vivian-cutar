# VIvian M3 三引擎 CLI 兼容性、副作用与统一兼容矩阵 (M3_ENGINE_CLI_COMPATIBILITY.md)

**文档路径**：`docs/research/M3_ENGINE_CLI_COMPATIBILITY.md`  
**核查日期**：2026-11-04  
**核心原则**：严格区分「源码定义 (B)」、「文档描述 (C)」、「实包独立验证 (A)」与「待实测/推测 (D)」；明确记录每个 CLI 调用的文件系统副作用，并提供隔离副本验证方案。**退出码 0 绝不能直接等同于自动化任务成功**。

---

## 1. March7thAssistant CLI 深度研究与副作用剖析

### 1.1 CLI 实际入口对比与版本存在性

| 可执行文件名 | 打包入口源码 | UAC 清单 (`uac_admin`) | 控制台属性 (`console`) | 证据级别与版本兼容性说明 |
| :--- | :--- | :--- | :--- | :--- |
| **`March7th Assistant.exe`** | `main.py`（排除 `PySide6`） | `True` (`requireAdministrator`) | `True` | **级别 B**（[`March7th Assistant.spec` L58-77](https://github.com/moesnow/March7thAssistant/blob/main/March7th%20Assistant.spec)）：所有历史与当前版本均稳定存在，执行任务的核心二进制。 |
| **`March7th Launcher.exe`** | `app.py`（含 `PySide6` GUI） | `True` | `False` | **级别 B**（[`.github/workflows/2.realtime_release.yml`](https://github.com/moesnow/March7thAssistant/blob/main/.github/workflows/2.realtime_release.yml)）：GUI 入口，同时支持命令行参数启动任务（如 `March7th Launcher.exe main -e`，见 [官方文档](https://m7a.top/docs/)）。 |
| **`m7a.exe`** | `main.py` + `utils/cli_entry.py` | `False` (`asInvoker`) | `True` | **级别 B（源码定义） / 级别 D（`v2026.10.31` 实包待核验）**：在 Commit [`6350047`](https://github.com/moesnow/March7thAssistant/blob/main/March7th%20Assistant.spec) 中定义。未提权时直接处理 `--status`/`--list`；执行任务时通过 UAC 启动 `March7th Assistant.exe` 并转发输出与退出码。 |

### 1.2 核心 CLI 参数行为、返回结构与文件系统副作用

| CLI 参数 | 源码定义 (级别 B) | 文档描述 (级别 C) | 实际调用的文件系统副作用 (Side Effects) | 证据级别 |
| :--- | :--- | :--- | :--- | :--- |
| **`--version` / `-v`** | 由 `argparse` 解析或读取 `assets/config/version.txt`（见 `2.realtime_release.yml` 版本文件检查逻辑） | 未在首页重点突出，官网推荐使用 `--status` 获取版本与环境状态 | 若通过 `main.py` 初始化配置模块（`module/config`），当根目录不存在 `config.yaml` 时，**可能自动从 `assets/config/config.example.yaml` 复制生成默认 `config.yaml` 并初始化 `logs/` 目录**。 | **级别 B / D** |
| **`--status`** | 在 `utils/cli_entry.py` 中实现（见 [`March7th Assistant.spec` L81-83](https://github.com/moesnow/March7thAssistant/blob/main/March7th%20Assistant.spec) 注释） | [官网 m7a.top](https://m7a.top/) 声明输出 JSON：`{ "first_run_completed": true, "game_path_exists": true, "active_run": null, ... }` | **高关注副作用**：为检测安装目录是否具备写权限（防止用户解压在 `C:\Program Files` 受保护目录导致运行时写配置/日志失败），`--status` 或配置初始化逻辑**会在安装目录中创建并立即删除临时探测文件（或锁文件），且在首次调用时若缺少 `config.yaml` 可能触发配置文件的生成/迁移（如 `scheduled_run_enable` 迁移逻辑，见 [`config.example.yaml` L420-425](https://github.com/moesnow/March7thAssistant/blob/main/assets/config/config.example.yaml)）**。因此 `--status` **并非纯只读无副作用操作**！ | **级别 B / C / D** |
| **`--list` / `-l` / `--list --json`** | 在 `utils/cli_entry.py` 与 `app.py` 中支持列出内置任务与自定义流程编排（v2026.9.30 新增，见 [Releases](https://github.com/moesnow/March7thAssistant/releases)） | [使用教程](https://m7a.top/docs/) 声明 `March7th Launcher.exe -l` 查看任务列表；[官网](https://m7a.top/) 声明 `m7a.exe --list --json` 输出 JSON 任务清单 | 同上，若触发全局 `cfg` 单例加载，可能产生 `config.yaml` 自动补齐或 `logs/` 目录创建副作用。 | **级别 B / C** |
| **`<task_id> --after-finish None`** | 执行 `main`（完整运行）、`daily`（每日实训）、`power`（清体力）、`routine`（日常）、`currencywars`、`fight`、`universe` | [官网对话示例](https://m7a.top/)：`.\m7a.exe daily --after-finish None`，输出 `开始执行：每日实训` ... `每日实训已完成 · 退出码 0` | 1. 写入 `logs/` 日志文件与截图；<br>2. **实时回写修改 `config.yaml` 中的时间戳与计数**（如 `last_run_timestamp`、`echo_of_war_timestamp`、`divergent_universe_weekly_completed_count`、`already_used_codes` 等，见 [`config.example.yaml`](https://github.com/moesnow/March7thAssistant/blob/main/assets/config/config.example.yaml)）；<br>3. 拉起并控制 `StarRail.exe`。 | **级别 B / C** |
| **`March7th Launcher.exe <task> -e`** | GUI 启动器带任务参数与 `-e`（正常完成后自动退出程序） | [使用教程·命令行启动](https://m7a.top/docs/) 明确记载：`March7th Launcher.exe main -e` | 除上述任务副作用外，还会读写 GUI 窗口尺寸与位置配置（`window_width`, `window_height`, `window_x`, `window_y`）。 | **级别 C** |

### 1.3 为什么退出码 `0` 不能直接等同于任务成功？
1. **默认阻塞陷阱 [级别 B]**：在 [`assets/config/config.example.yaml`](https://github.com/moesnow/March7thAssistant/blob/main/assets/config/config.example.yaml) 第 44~48 行中，默认配置为：
   ```yaml
   pause_after_success: true   # 成功后暂停程序（等待用户按键）
   exit_after_failure: false   # 失败后不直接退出，而是暂停程序
   ```
   如果不修改这两个键（或不使用专门处理了非阻塞退出的入口参数），无论成功还是失败，进程都会卡在控制台等待输入而不会退出！
2. **跳过与部分完成语义 [级别 B/C]**：当执行 `main`（完整运行）时，若 `power_enable: false` 或今日时间戳已被标记为完成，程序会跳过所有实际操作并以退出码 `0` 瞬间退出。此时 `ExitCode == 0` 仅代表“进程未抛出未捕获异常”，不代表实际进入游戏完成了实训或清除了开拓力。
3. **可靠完成依据设计**：必须同时满足：
   - 进程 `ExitCode == 0`；
   - 标准输出或 `logs/` 当次运行日志中明确包含目标任务的完成事件（对应 `config.example.yaml` 中的状态模板：`每日实训已完成`、`清体力已完成`、`完整运行` 正常收尾，且不含 `发生错误`、`清体力未完成`、`每日实训未完成`）；
   - `config.yaml` 中的 `last_run_timestamp`（或对应周常 timestamp）较启动前发生了更新。

### 1.4 隔离副本（Sandbox Copy）中的无损 CLI 验证方案设计
为遵守“不修改上游真实安装目录、未经授权不运行真实游戏任务”的铁律，Codex 在实机执行 CLI 探测（`--version`、`--status`、`--list --json`、`-h`）时，必须遵循以下 **隔离副本验证规程**：
1. **创建临时影子副本**：在 `%TEMP%\Vivian_CliProbe_{Guid}\` 下创建临时目录。为避免每次复制 450MB 的 `libraries/` 和 `3rdparty/`，对只读大目录（`libraries/`, `assets/`, `3rdparty/`）使用目录符号链接/Junction（或仅在首次解压验证时使用完整临时目录），而将根目录下的 `.exe` 与 `config.yaml` 独立拷贝至临时目录。
2. **快照对比（Pre/Post File Diff）**：
   - 执行前记录临时目录内所有文件的路径、大小、最后修改时间（`LastWriteTimeUtc`）与 SHA-256。
   - 仅允许执行纯查询命令：`--help`、`--version`、`--status`、`--list --json`（**严禁传入 `main`、`daily` 等会拉起游戏的参数**）。
   - 执行后再次扫描临时目录，精确记录新增文件（如 `config.yaml`、`logs/`、临时探测文件）、修改文件与删除文件，验证完毕后彻底删除 `%TEMP%\Vivian_CliProbe_{Guid}\`。

---

## 2. BetterGI 正式发行版 (`v0.66.0`) CLI 兼容性核查

| 核查项 | 结论与技术细节 | 证据分类与来源 |
| :--- | :--- | :--- |
| **命令行参数在正式版 `v0.66.0` 是否可用** | **确认可用**。`startOneDragon` 参数早在 `v0.39.0 / v0.40.0`（2025年1月，Commit `5259b1f`，见 [Issue #998](https://github.com/babalae/better-genshin-impact/issues/998)）即已合入正式版；`--startGroups` 在 Commit `a9150054c4`（#1309）合入。在 `v0.66.0` 中均稳定存在。 | **级别 B**：[`OneDragonFlowViewModel.cs`](https://gitback.org/babalae/better-genshin-impact/src/commit/436a8d207ee302763edb207a545093b4cf22340d/BetterGenshinImpact/ViewModel/Pages/OneDragonFlowViewModel.cs)、[Issue #998](https://github.com/babalae/better-genshin-impact/issues/998) |
| **`startOneDragon` 实际入口机制** | 并非在控制台 `Main()` 中同步阻塞运行，而是启动 WPF 主窗口后，在 `MainWindowViewModel.OnLoaded()` 中检测到 `Environment.GetCommandLineArgs().Length > 1` 跳过欢迎/更新弹窗，随后导航触发 `OneDragonFlowViewModel.OnLoaded()`，匹配 `args[1].Contains("startOneDragon")` 并提取 `args[2]` 配置名，异步调用 `OnOneKeyExecute()`。 | **级别 B**：[`MainWindowViewModel.cs` L78-83](https://gitback.org/babalae/better-genshin-impact/src/commit/900b6aec84911b8af00042e60984beb7ffbc4a0a/BetterGenshinImpact/ViewModel/MainWindowViewModel.cs)、[`OneDragonFlowViewModel.cs` L572-598](https://gitback.org/babalae/better-genshin-impact/src/commit/436a8d207ee302763edb207a545093b4cf22340d/BetterGenshinImpact/ViewModel/Pages/OneDragonFlowViewModel.cs) |
| **源码分支与正式版差异警告** | **严禁混用**：仓库 `refactor/20260930` 分支正在开发 `.NET 10`、本地 HTTP/WebSocket/MCP 服务（`BetterGenshinImpact/Service/ExternalAccess/`）以及新一代 `Pulonia` 任务计划系统（见 [`CONTEXT.md`](https://github.com/babalae/better-genshin-impact/blob/refactor/20260930/CONTEXT.md)）。**这些接口在 `v0.66.0` 正式发行包中完全不存在**！VIvian M3 阶段必须面向 `v0.66.0` 的 `startOneDragon` + `User/OneDragon/*.json` + `log/` 文件构建适配器。 | **级别 B**：[Releases `0.66.0`](https://github.com/babalae/better-genshin-impact/releases) vs [`CONTEXT.md`](https://github.com/babalae/better-genshin-impact/blob/refactor/20260930/CONTEXT.md) |
| **版本兼容性与游戏窗口依赖** | 1. 强依赖 **16:9 分辨率**（推荐 `1920x1080` 窗口化），地图追踪与自动秘境要求 $\ge 1920\times 1080$；<br>2. 严禁开启 HDR 或 NVIDIA 滤镜，小地图必须设为“锁定方向”；<br>3. `v0.63.0` 与 `v0.65.0` 官方 Release Notes 明确带有重构不稳定警告，VIvian 兼容矩阵应将 `v0.66.0` 设为推荐版本，将 `v0.63.0`/`v0.65.0` 标记为不推荐。 | **级别 B**：[README.md](https://github.com/babalae/better-genshin-impact/blob/main/README.md)、[Releases](https://github.com/babalae/better-genshin-impact/releases) |

---

## 3. ZenlessZoneZero-OneDragon (`v2.6.0`) 接口与兼容核查

| 核查项 | 结论与技术细节 | 证据分类与来源 |
| :--- | :--- | :--- |
| **外部 CLI 启动参数** | `OneDragon-RuntimeLauncher.exe` 与 `OneDragon-Launcher.exe` 支持：`-h, --help`、`-v, --version`、`-o, --onedragon`（直接运行一条龙）、`-c, --close-game`（运行后关闭游戏）、`-s [SHUTDOWN]`、`-i INSTANCE`（指定运行的账号实例，如 `1,2`）。 | **级别 C**：[快速开始·5.高级启动参数](https://one-dragon.com/zzz/zh/quickstart.html)、[千机链文档](https://one-dragon.com/tools/zh/script_chainer.html) |
| **启动器与后端进程关系（核心避坑点）** | - **原始启动器 (`OneDragon-Launcher.exe`)**：只是一个壳程序，启动后会拉起独立的子进程 `python.exe` 执行一条龙，自身可能提前退出或分离（这就是为什么官方 [千机链预设表](https://one-dragon.com/tools/zh/script_chainer.html) 中明确写明普通启动器必须监听 `python.exe` 而非启动器自身）。<br>- **集成启动器 (`OneDragon-RuntimeLauncher.exe`)**：内嵌 `.runtime`，实际进程名固定为 `OneDragon-RuntimeLauncher.exe`。 | **级别 B / C**：[`build-release.yml`](https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon/blob/main/.github/workflows/build-release.yml)、[千机链 3.1 & 3.2 节](https://one-dragon.com/tools/zh/script_chainer.html) |
| **任务执行结果从哪里读取** | 1. 日志文件：`{Root}\.log\` 目录下的 `.log` 文件（格式：`[HH:mm:ss.fff] [filename.py line] [LEVEL]: message`，见 [`.gitignore`](https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon/blob/main/.gitignore) 与 [Issue #2545](https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon/issues/2545)）；<br>2. 运行记录配置文件：`config/`（或 `config/01/`）下的 `app_run_record`（近期 PR #2886 `refactor(application): 分离应用入口并默认创建运行记录`）。具体 YAML 文件名需在实包解压后核实 **[级别 D]**。 | **级别 B / D** |

---

## 4. 三引擎统一兼容性矩阵（含证据级别 A/B/C/D）

| 矩阵维度 | 原神：BetterGI | 崩坏：星穹铁道：March7thAssistant | 绝区零：ZenlessZoneZero-OneDragon |
| :--- | :--- | :--- | :--- |
| **当前核查稳定版本** | `v0.66.0` (2026-09-26) **[B]** | `v2026.10.31` (2026-10-31) **[B]** | `v2.6.0` (2026-10-30) **[B]** |
| **固定 Commit SHA** | `0af68c2` **[B]** | `1e77d59`（注：`m7a.exe` 提交于 `6350047`）**[B]** | `5f2e473` **[B]** |
| **官方首选发行包** | [BetterGI_v0.66.0.7z](https://github.com/babalae/better-genshin-impact/releases) (444 MB) **[B]** | [March7thAssistant_full.7z](https://github.com/moesnow/March7thAssistant/releases) (452 MB) **[B]** | [ZenlessZoneZero-OneDragon-v2.6.0-WithRuntime-Full.zip](https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon/releases) (269 MB) **[B]** |
| **官方公布 SHA-256** | `28d4756e55f32637538bf84427f5f31afd5b153bf996530c5975bd9c8c0b9343` **[B]**<br>实包字节校验：**Pending [D]** | `1fcaa1934e3d30d598ce7863756c49b9e9fb4f4fbb759066a62f52541a39dee0` **[B]**<br>实包字节校验：**Pending [D]** | `2fd5f6bfc6c01fcf6f21092dc6e18fa18e146d2614ca6ad130d5fc3057ba0ced` **[B]**<br>实包字节校验：**Pending [D]** |
| **启动与执行方式** | `BetterGI.exe startOneDragon <配置名>` **[B]** | 优先 `m7a.exe <task> --after-finish None`，回退 `March7th Assistant.exe <task>` **[B/C]** | `OneDragon-RuntimeLauncher.exe -o -c [-i 1,2]` **[C]** |
| **状态/版本查询方式** | 读取 `BetterGI.exe` 文件版本信息（`FileVersionInfo`）+ 扫描进程与 `log/` **[B]** | `m7a.exe --status` (JSON) / `m7a.exe --list --json` **[B/C]** | `OneDragon-RuntimeLauncher.exe -v` 或读取 `src/one_dragon/version.py` **[B/C]** |
| **任务完成证据** | `log/better-genshin-impact{yyyyMMdd}.log` 新增行出现 `一条龙和配置组任务结束` 且前序无 `任务被取消，退出执行` **[B]** | 进程 `ExitCode == 0` + 日志/stdout 出现完成标记 + `config.yaml` 时间戳更新 **[B/C]** | `OneDragon-RuntimeLauncher.exe` 曾启动并正常退出 + `ZenlessZoneZero.exe` 已关闭 + `.log/` 无致命异常 **[C/D]** |
| **停止与清理方式** | 配置 `CompletionAction="关闭游戏和软件"` 自动退出；外部超时执行 JobObject 终止 + 释放键鼠 **[B]** | 发送热键 `F10` 或终止 `March7th Assistant.exe` 进程树 + 关闭 `StarRail.exe` **[B]** | `-c` 参数自动关闭游戏；外部超时终止启动器进程树与 `ZenlessZoneZero.exe` **[C]** |
| **权限要求** | 强制管理员权限（`.UseElevated()`，否则以退出码 `559` 脱离进程树自重启）**[B]** | 任务执行强制管理员权限（`uac_admin=True`）；`m7a.exe` 查询免提权 **[B]** | 强制管理员权限（启动器请求 UAC 提权）**[C]** |
| **调用副作用** | 退出时将内存中的 `AllConfig` 全量回写覆盖 `User/config.json`；注册 `bettergi://` URL 协议 **[B]** | `--status` 可能创建/删除目录写权限探测临时文件并初始化 `config.yaml`；运行任务回写 `config.yaml` 时间戳 **[B/D]** | 启动器默认在启动时检查并拉取代码更新（必须关闭自动更新）；写入 `.log/` 与运行记录 **[C]** |
| **尚未验证事项 (Level D)** | 1. 实包 SHA-256 独立重算；<br>2. `v0.66.0` 下 `--startOneDragon` 完成后是否仍偶发不退出（#2049）。 | 1. 核实 `v2026.10.31` 实包内是否已包含 `m7a.exe`；<br>2. 隔离沙箱实测 `--status` 完整 JSON 字段与临时文件副作用。 | 1. 实包解压后 `config/` 下 YAML 精确键名；<br>2. `OneDragon-RuntimeLauncher.exe` 是否仍派生子进程及 `.log/` 完成行正则。 |
| **VIvian 接入建议** | **第 2 顺位接入**：C# 源码完全透明，日志格式极其清晰，仅需处理单实例锁与配置预写入。 | **第 1 顺位优先接入**：原生支持 CLI 子任务传参、纯控制台输出与退出码，且 `config.yaml` 705 行全字段已在源码确认。 | **第 3 顺位接入**：启动器与 Python 运行时封装层次较多，需先在 Windows 实机解压核实 `config/*.yml` 与子进程树后再编码。 |
