# Vivian V1.0 系统架构设计方案 (ARCHITECTURE_PROPOSAL.md)

**文档路径**：`docs/architecture/ARCHITECTURE_PROPOSAL.md`  
**设计定位**：面向 Windows 10/11 x64 的三游戏（原神 / 崩坏：星穹铁道 / 绝区零）自动化引擎统一编排、监控、配置保护与独立版本管理桌面系统。

---

## 1. 技术栈评估与选型论证

### 1.1 推荐技术栈：`C# / .NET 10 + WPF (WPF-UI) + SQLite` 合理性评估

| 评估维度 | C# / .NET 10 + WPF + SQLite 表现 | 架构结论 |
| :--- | :--- | :--- |
| **系统 API 与提权亲和性** | 原生支持 P/Invoke / `CsWin32` 调用 Windows Job Object（进程树生命周期绑定）、`WTS` 会话 API、窗口句柄控制（`User32.dll`）、命名互斥锁（`Mutex`）与 Windows 任务计划程序 COM 接口，且与管理员权限（`requireAdministrator`）无缝集成。 | **极佳** |
| **与上游技术栈同源性** | 上游 `BetterGI` 本身即采用 `C# + WPF-UI 4.0.2 + CommunityToolkit.Mvvm 8.2.2 + Microsoft.Extensions.Hosting`（已从 [AGENTS.md](https://github.com/babalae/better-genshin-impact/blob/refactor/20260930/AGENTS.md) 验证）。采用相同技术栈可直接复用 JSON/YAML 序列化、窗口句柄检测与 Fluent 视觉规范。 | **极佳** |
| **硬件资源占用** | 由于三款游戏及上游视觉推理引擎（ONNX Runtime / DirectML / OpenCV / PaddleOCR / RapidOCR）在运行时会高负载占用 GPU 显存与 CPU，管理器宿主必须保持极低开销。WPF 空闲内存仅 `40~70 MB`，后台托盘接近零 GPU 占用。 | **极佳** |
| **数据持久化能力** | `Microsoft.Data.Sqlite` + `Dapper`（或轻量 EF Core）单文件零配置存储任务历史、执行队列快照、版本兼容矩阵及审计日志，支持 WAL 并发读写。 | **极佳** |

### 1.2 备选技术栈对比与切换代价说明

| 备选方案 | 核心优势 | 为什么不作为首选（切换代价） |
| :--- | :--- | :--- |
| **WinUI 3 / Windows App SDK** | 微软最新原生 Fluent 控件库，云母（Mica）材质特效原生度最高。 | **代价高**：WinUI 3 在 `requireAdministrator`（管理员提权）模式下存在已知的 MSIX 打包限制、文件拖拽失效及托盘图标（NotifyIcon）缺少官方原生支持的问题，对于必须以管理员运行的自动化调度器而言踩坑成本高于 WPF。 |
| **Avalonia UI 11 (.NET 10)** | 跨平台 XAML 框架，现代化样式系统优秀，无传统 WPF 历史包袱。 | **代价中**：本项目明确限定 `Windows 10/11 x64`（且三款上游本地模式均强依赖 Win32 API），无需跨平台能力；Avalonia 生态中的系统托盘与原生窗口嵌入需额外编写平台特定互操作代码。 |
| **Tauri 2 (Rust + Web 前端)** | 前端 UI 开发效率高，动画表现力强。 | **代价极高**：WebView2 驻留内存高达 `150~300 MB` 且占用 GPU 合成器资源，在运行原神/绝区零时易与 DXGI/WGC 截图及 DirectML 推理争抢资源；Rust 侧编写复杂的 Windows COM/Win32 进程树与窗口焦点管控心智负担大。 |
| **Python 3.12 + PySide6-Fluent-Widgets** | 与 `March7thAssistant` 和 `ZZZ-OneDragon` 同源。 | **代价高**：Python 打包体积大（PyInstaller 解压慢），静态类型安全弱于 C#，长期维护多引擎适配器契约与多线程异步状态机的稳健性不如 .NET。 |

> **最终结论**：**坚定采用 `C# 13 / .NET 10 (或 .NET 8 LTS) + WPF (WPF-UI 4.x) + CommunityToolkit.Mvvm + SQLite`**，单文件自包含（或依赖框架）发布，兼具极低内存开销、一流 Win32 系统操控力与现代化 Fluent 界面体验。

---

## 2. 系统总体分层架构

```
+-----------------------------------------------------------------------------------+
|                              Presentation Layer (WPF)                             |
|  [首页概览/一键启动] [三游戏任务设置] [执行队列监控] [引擎与版本管理] [历史/日志] [系统设置] |
|  Fluent Dark/Light Theme | Tray Icon Service | Global Hotkey (Stop/Pause)         |
+-----------------------------------------+-----------------------------------------+
                                          | MVVM Binding / Messenger Events
+-----------------------------------------v-----------------------------------------+
|                           Application & Orchestration Layer                       |
|  +-----------------------+  +-----------------------+  +-----------------------+  |
|  | TaskSchedulerEngine   |  | WindowFocusGuard      |  | VersionCompatibility  |  |
|  | (严格串行互斥状态机)    |  | (窗口焦点与输入防冲突)  |  | Gatekeeper (版本门禁)  |  |
|  +-----------------------+  +-----------------------+  +-----------------------+  |
|  +-----------------------+  +-----------------------+  +-----------------------+  |
|  | ConfigBackupManager   |  | UnifiedLogAggregator  |  | ProcessJobGuard       |  |
|  | (配置快照/事务回滚)    |  | (实时流+文件尾随解析)   |  | (Win32 JobObject守护) |  |
|  +-----------------------+  +-----------------------+  +-----------------------+  |
+-----------------------------------------+-----------------------------------------+
                                          | IGameAutomationEngine Contract
+-----------------------------------------v-----------------------------------------+
|                             Engine Adapter Layer (ACL)                            |
|  +-----------------------+  +-----------------------+  +-----------------------+  |
|  | BetterGiAdapter       |  | March7thAdapter       |  | ZzzOneDragonAdapter   |  |
|  | - CLI: startOneDragon |  | - CLI: m7a.exe / main |  | - CLI: -o -c -i       |  |
|  | - JSON Config Patcher |  | - YAML Config Patcher |  | - YAML Config Patcher |  |
|  | - Serilog File Tailer |  | - Stdout/Logs Parser  |  | - .log/ File Tailer   |  |
|  +-----------------------+  +-----------------------+  +-----------------------+  |
+-----------------------------------------+-----------------------------------------+
                                          | Independent OS Process (Admin Token)
+-----------------------------------------v-----------------------------------------+
|                        External Upstream Engines & Games                          |
|  [BetterGI.exe + YuanShen]  [March7th Assistant + StarRail]  [OneDragon + ZZZ]    |
+-----------------------------------------------------------------------------------+
```

---

## 3. 核心模块详细设计

### 3.1 统一引擎适配器契约（`IGameAutomationEngine`）与防腐层（ACL）
为隔离上游升级导致的 CLI 参数或配置文件格式变更，所有引擎交互必须通过防腐层（Anti-Corruption Layer）与**声明式清单文件（`engine_manifest.json`）**驱动，而非硬编码在 C# 逻辑中：

```csharp
public interface IGameAutomationEngine
{
    GameType Game { get; } // Genshin, StarRail, ZenlessZoneZero
    Task<EngineInstallStatus> ProbeInstallationAsync(CancellationToken ct);
    Task<EngineConfigSnapshot> ReadNormalizedConfigAsync(CancellationToken ct);
    Task ApplyNormalizedConfigPatchAsync(NormalizedDailyTaskConfig config, CancellationToken ct);
    Task<EngineExecutionHandle> StartDailyTaskAsync(ExecutionContext context, CancellationToken ct);
    Task StopGracefullyAsync(EngineExecutionHandle handle, TimeSpan timeout, CancellationToken ct);
    Task KillForcefullyAsync(EngineExecutionHandle handle);
    Task OpenNativeGuiAsync();
}
```

#### 三大独立适配器设计要点：
1. **`BetterGiAdapter`（原神适配器）**：
   - **单实例预清理**：启动前检查系统中是否存在 `BetterGI.exe` 进程。若存在，先通过 `CloseMainWindow()` 请求退出，等待 3 秒未退出则强制终止，避免 `.UseSingleInstance("BetterGI")` 导致命令行参数失效（见 [App.xaml.cs](https://gitback.org/babalae/better-genshin-impact/src/commit/2a2acb5b0df635a5293507916fa1d1db3cab4aaf/BetterGenshinImpact/App.xaml.cs)）。
   - **配置补丁**：读取 `{Root}\User\OneDragon\<SelectedConfig>.json`，将 `CompletionAction` 设为 `"关闭游戏和软件"`，同步 `TaskEnabledList` 布尔字典。
   - **进程启动**：以 `WorkingDirectory = {Root}` 启动 `BetterGI.exe startOneDragon <SelectedConfig>`。
   - **日志尾随**：使用 `FileSystemWatcher` + 只读共享文件流（`FileShare.ReadWrite | FileShare.Delete`）实时读取 `{Root}\log\better-genshin-impact{yyyyMMdd}.log`。当捕获到 `一条龙和配置组任务结束` 后，给予 15 秒窗口等待进程自行退出；若触发 [#2049](https://github.com/babalae/better-genshin-impact/issues/2049) 未自动退出 Bug，则由适配器主动关闭 `YuanShen.exe` 与 `BetterGI.exe` 并标记任务成功。
2. **`March7thAdapter`（星穹铁道适配器）**：
   - **免责声明状态探测**：优先调用 `m7a.exe --status` 解析返回的 JSON。若 `first_run_completed == false`，在 Vivian UI 上弹出引导提示，一键拉起 `March7th Launcher.exe` 让用户本人点击确认免责声明。
   - **非阻塞化配置补丁**：修改 `{Root}\config.yaml`（使用 `YamlDotNet` 保留注释或 AST 级键值替换），强制写入 `pause_after_success: false`、`exit_after_failure: true`、`check_update: false`、`auto_update: false`（见 [config.example.yaml](https://github.com/moesnow/March7thAssistant/blob/main/assets/config/config.example.yaml)）。
   - **进程启动**：直接调用 `March7th Assistant.exe main`（或 `m7a.exe main --after-finish None`），重定向 `StandardOutput` 与 `StandardError`（强制 `StandardOutputEncoding = Encoding.UTF8`）。
3. **`ZzzOneDragonAdapter`（绝区零适配器）**：
   - **路径合法性硬校验**：在绑定或安装目录时，强制校验路径是否满足正则表达式 `^[a-zA-Z]:\\[a-zA-Z0-9_\-\\]+$`（全英文、无空格、无中文、长度 `< 80` 字符，见 [快速开始文档](https://one-dragon.com/zzz/zh/quickstart.html)）。
   - **锁定集成启动器**：统一调用 `OneDragon-RuntimeLauncher.exe -o -c`（多账号时附加 `-i 1,2`），并在 `config/env.yml` 中关闭启动时自动代码拉取（`auto_update: false`）。
   - **日志尾随**：实时监听 `{Root}\.log\` 下最新生成的 `.log` 文件，解析 `[one_dragon_app.py ...]` 进度事件。

---

### 3.2 任务调度器（`TaskSchedulerEngine`）与严格串行状态机
由于三款游戏及自动化引擎均依赖**前台窗口焦点（或独占虚拟手柄/鼠标点击）**以及高负载的 **DirectML / ONNX 视觉推理**，**严禁多款游戏并行执行**。调度器设计为单通道严格串行有向状态机：

#### 核心状态流转（State Machine）：
`Idle`（空闲） $\rightarrow$ `PreCheck`（前置检查：磁盘/路径/分辨率/冲突进程） $\rightarrow$ `ConfigPatching`（备份并写入目标配置） $\rightarrow$ `StartingEngine`（拉起引擎进程并绑定 JobObject） $\rightarrow$ `Running`（监控心跳、窗口焦点与日志进度） $\rightarrow$ `CooldownCleaning`（回收引擎与游戏进程，冷却 10 秒释放显存） $\rightarrow$ `NextTask / Completed`。

#### 异常处理与防卡死机制：
1. **双层超时控制**：
   - **总时长超时（Hard Timeout）**：每个游戏任务可配置最大允许运行时间（如原神日常 45 分钟、星铁日常 40 分钟、绝区零日常 30 分钟；若启用锄大地可放宽至 180 分钟）。
   - **日志静默心跳超时（Log Stall Watchdog）**：若引擎进程存活，但连续 `8 分钟`（可配置）没有任何新日志行输出，判定为视觉识别死循环或弹窗阻塞。
2. **故障隔离与链式继续（Continue-On-Failure）**：
   - 当某款游戏因超时、游戏需要大版本更新或识别失败报错时：
     1. 自动截取当前桌面全屏快照保存至 `{VivianData}\diagnostics\{RunId}_error.png`（便于用户事后排查是停留在登录框、月卡界面还是报错弹窗）。
     2. 强制清理当前引擎进程树及对应的游戏进程（如 `YuanShen.exe`），防止残留游戏窗口遮挡下一款游戏。
     3. 等待 10 秒冷却期后，**继续执行队列中的下一款游戏任务**，绝不因单个游戏失败导致整晚挂机队列全部作废。

---

### 3.3 游戏窗口与键鼠控制冲突防护（`WindowFocusGuard`）
1. **跨游戏进程互斥清理**：在启动第 $N$ 个游戏引擎前，扫描系统进程列表，若发现前序游戏（`YuanShen.exe` / `GenshinImpact.exe` / `StarRail.exe` / `ZenlessZoneZero.exe`）或前序引擎进程仍在运行，先尝试优雅关闭，5 秒未退则 `Kill()`。
2. **虚拟手柄与后台模式冲突检测**：绝区零一条龙后台模式依赖 `ViGEmBus` 虚拟手柄（见 [游戏设置文档](https://one-dragon.com/zzz/zh/setting/setting_game.html)），若前台同时运行其它支持手柄的游戏会导致输入串台。因此队列执行期间强制串行。
3. **全局紧急制动热键（Panic Hotkey）**：Vivian 注册系统级低级全局热键 `Ctrl + Alt + F12`（或用户自定义）。无论当前鼠标是否被上游脚本高频抢占，按下紧急热键立即挂起并终止当前子引擎进程，立刻归还用户键鼠控制权。
4. **运行前倒计时横幅**：当定时任务触发或队列切换到下一款游戏前，在屏幕顶部弹出 5 秒半透明置顶倒计时提示条（可点击“暂停 5 分钟”或“取消”），防止用户正在打字时突然被抢占鼠标。

---

### 3.4 状态事件总线（Event-Driven Architecture）
基于 `CommunityToolkit.Mvvm.Messaging.IMessenger` 构建强类型事件流：
- `QueueStateChangedEvent(QueueId, OldState, NewState)`
- `EngineTaskProgressEvent(GameType, CurrentSubTaskName, CompletedSteps, TotalSteps, ElapsedTime)`
- `EngineLogReceivedEvent(GameType, Timestamp, LogLevel, RawLine, ParsedCategory)`
- `EngineAnomalyDetectedEvent(GameType, AnomalyType, ScreenshotPath, DiagnosticAdvice)`
- `VersionUpdateAvailableEvent(GameType, CurrentVersion, LatestVersion, CompatibilityStatus)`

---

### 3.5 配置中心与备份恢复机制（`ConfigBackupManager`）
针对上游更新或异常断电偶发导致配置文件被重置或损坏的问题（如 March7thAssistant 曾在 [v2026.10.3](https://github.com/moesnow/March7thAssistant/releases) 修复更新时偶发配置重置问题，BetterGI 曾在 [v0.64.0](https://github.com/babalae/better-genshin-impact/releases) 将配置改为原子写入）：
1. **写前快照（Copy-on-Write Backup）**：Vivian 每次对上游配置文件（`User/OneDragon/*.json`、`config.yaml`、`config/*.yml`）执行修改前，自动计算文件 SHA-256，若发生变化则在 `{VivianData}\backups\{GameType}\{yyyyMMdd_HHmmss}\` 生成带时间戳的压缩快照（最多保留最近 20 份）。
2. **原子写入（Atomic Write）**：所有配置修改先写入同目录 `.tmp` 临时文件并 `Flush(true)` 落盘，校验 JSON/YAML 语法合法后再通过 `File.Move(tmp, target, overwrite: true)` 原子替换。
3. **损坏自愈**：启动前若检测到目标配置文件长度为 0 字节或反序列化失败，自动提示或静默从最近一次有效快照恢复。

---

### 3.6 统一日志系统与 SQLite 数据库设计
- **日志双层存储**：
  - **SQLite 结构化存储（`vivian.db`）**：存储任务运行批次（`TaskBatchRuns`）、单游戏执行记录（`EngineTaskRuns`）、关键异常摘要（`TaskErrors`）与版本变更记录（`EngineVersionHistory`）。
  - **原始日志归档**：每次运行结束后，将该次运行区间内截取的引擎原始日志压缩归档至 `{VivianData}\logs\runs\{RunId}_{GameType}.log`，支持在 UI 中按时间线回放。

```sql
-- 核心表结构示例 (SQLite)
CREATE TABLE TaskBatchRuns (
    BatchId TEXT PRIMARY KEY,
    TriggerType TEXT NOT NULL, -- 'ManualOneKey', 'Scheduled', 'SingleGame'
    StartTime TEXT NOT NULL,
    EndTime TEXT,
    OverallStatus TEXT NOT NULL, -- 'Running', 'Success', 'PartialSuccess', 'Failed', 'Cancelled'
    PostAction TEXT NOT NULL -- 'None', 'CloseGames', 'Sleep', 'Shutdown'
);

CREATE TABLE EngineTaskRuns (
    RunId TEXT PRIMARY KEY,
    BatchId TEXT NOT NULL,
    GameType TEXT NOT NULL, -- 'Genshin', 'StarRail', 'ZenlessZoneZero'
    EngineVersion TEXT NOT NULL,
    CommandLine TEXT NOT NULL,
    StartTime TEXT NOT NULL,
    EndTime TEXT,
    ExitCode INTEGER,
    Status TEXT NOT NULL, -- 'Pending', 'Running', 'Success', 'Failed', 'Timeout', 'Cancelled'
    FailureReason TEXT,
    ErrorScreenshotPath TEXT,
    LogArchivePath TEXT,
    FOREIGN KEY(BatchId) REFERENCES TaskBatchRuns(BatchId)
);
```
