# VIvian M3 三引擎进程拓扑、Job Object 逃逸与权限风险分析 (M3_ENGINE_PROCESS_AND_PRIVILEGE_RISKS.md)

**文档路径**：`docs/research/M3_ENGINE_PROCESS_AND_PRIVILEGE_RISKS.md`  
**核查日期**：2026-11-04  
**分析重点**：深入剖析三款上游引擎在启动、提权（UAC `ShellExecute`）、唤起第三方工具或游戏启动器时的**子进程派生树**，识别会导致子进程脱离 `WindowsProcessSupervisor`（Windows Job Object）监管的逃逸路径，并给出不规避任何安全机制的工程解决方案。

---

## 1. 为什么自动化引擎子进程会脱离 Windows Job Object？（三大逃逸机制）

在 Windows 10/11 系统中，即使 `WindowsProcessSupervisor` 创建了设置了 `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` 的 Job Object 并将直接拉起的引擎进程（`Process A`）加入其中，以下三种场景仍会导致后续进程逃逸监管：

### 逃逸路径 1：`ShellExecuteEx("runas")` 经由 AppInfo 服务提权启动（必然逃逸）[级别 B]
- **触发机制**：当一个非管理员进程（Medium Integrity Level）调用 `ShellExecuteEx` 且 `lpVerb = "runas"` 启动需要管理员权限（High Integrity Level）的目标程序时，实际创建新进程的是系统服务 `svchost.exe (Application Information / AppInfo)`，而非当前父进程！
- **上游实证**：
  1. **BetterGI**：[`RuntimeHelper.cs` L81-102](https://gitback.org/babalae/better-genshin-impact/src/commit/4bf302b01325400342e09fd679532e78e2609be1/BetterGenshinImpact/Helpers/RuntimeHelper.cs) 中，若检测到当前非管理员，会以 `UseShellExecute = true, Verb = "runas"` 重新启动 `BetterGI.exe`，原进程立即以退出码 `559` 退出。新拉起的管理员 `BetterGI.exe` 父进程为 `svchost.exe`，**完全不在 VIvian 的 Job Object 内**。
  2. **March7thAssistant**：[`March7th Assistant.spec` L81-83](https://github.com/moesnow/March7thAssistant/blob/main/March7th%20Assistant.spec) 中，`m7a.exe`（`uac_admin=False`）在未提权状态下执行日常任务时，会通过 UAC 提权启动 `March7th Assistant.exe`（`uac_admin=True`）。若调用方未提权，被拉起的 `March7th Assistant.exe` 同样由 `AppInfo` 服务创建，可能脱离常规父子进程句柄树。

### 逃逸路径 2：启动器壳程序（Launcher）拉起工作进程后自身立即退出 [级别 B/C]
- **触发机制**：若 VIvian 仅以 `Process.WaitForExitAsync()` 等待启动器进程（Process A）结束就判定“任务已完成”，而 Process A 只是一个负责环境检查并拉起真正工作进程（Process B）的 Launcher，就会发生 **“假完成（False Completion）”** 与 **“工作进程失管”**。
- **上游实证**：
  - **ZenlessZoneZero-OneDragon 原始启动器 (`OneDragon-Launcher.exe`)**：根据官方 [千机链文档 3.1 & 3.2 节](https://one-dragon.com/tools/zh/script_chainer.html) 明确记载，`OneDragon-Launcher.exe` 启动后会拉起虚拟环境中的 `python.exe` 执行一条龙逻辑。如果外部监管器只盯着 `OneDragon-Launcher.exe`，就会误以为任务结束，而 `python.exe` 仍在后台控制游戏。
  - **集成启动器 (`OneDragon-RuntimeLauncher.exe`)**：虽然官方千机链文档标注其监听目标进程名即为 `OneDragon-RuntimeLauncher.exe`，但因其由 PyInstaller 打包（见 [`build-release.yml` L117](https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon/blob/main/.github/workflows/build-release.yml)），在 Windows 下 PyInstaller bootloader 是否存在双进程结构（父 bootloader 解压/设置环境后派生子进程），**必须在 Windows 实机验证 [级别 D]**。

### 逃逸路径 3：通过 WMI / COM 任务计划程序 / 独立守护进程跨会话启动 [级别 B]
- **触发机制**：
  - **BetterGI 桌面分身 (`ChildSession`)**：通过任务计划程序 COM 接口（`ITaskService`）注册一次性临时任务，调用 `RunEx` 带 `TASK_RUN_USE_SESSION_ID` 在 RDP 子会话中拉起第二个 `BetterGI.exe`（见 [Issue #1180 源码剖析](https://github.com/moesnow/March7thAssistant/issues/1180)）。该子会话进程由 `svchost.exe (Schedule)` 创建，必然脱离主会话的 Job Object。
  - **游戏启动器中转 (`HYP.exe` / `launcher.exe`)**：当引擎通过米哈游启动器（`launcher.exe` / `HoYoPlay`）间接启动游戏或更新游戏时（如 March7th 配置 `update_via_launcher: true`），游戏进程的父进程是启动器而非引擎。

---

## 2. 三款引擎完整进程派生树与权限矩阵

| 审计维度 | 原神：BetterGI (`v0.66.0`) | 崩坏：星穹铁道：March7thAssistant (`v2026.10.31`) | 绝区零：ZenlessZoneZero-OneDragon (`v2.6.0`) |
| :--- | :--- | :--- | :--- |
| **入口进程及权限** | `BetterGI.exe`（强制管理员）**[B]** | `March7th Assistant.exe`（强制管理员）<br>`m7a.exe`（`asInvoker`，执行任务时请求管理员）**[B]** | `OneDragon-RuntimeLauncher.exe`（请求管理员）**[C]** |
| **派生的辅助子进程** | - 内置更新时拉起 `BetterGI.update.exe` **[B]** | - 执行锄大地时拉起 `.\3rdparty\Fhoe-Rail\*.exe`（或 `python.exe`）**[B]**<br>- 执行旧版模拟宇宙时拉起 `.\3rdparty\Auto_Simulated_Universe\*.exe` **[B]**<br>- 云游戏模式拉起 `chrome.exe` / `msedge.exe` + `chromedriver.exe` **[B]**<br>- 更新时拉起 `March7th Updater.exe` 与 `assets\binary\7za.exe` **[B]** | - 原始启动器派生 `.venv\Scripts\python.exe` **[C]**<br>- 集成启动器内部执行 `src/zzz_od/gui/app.py`（是否派生子进程 **待实机验证 [D]**）<br>- 若开启 MCP 后端则存在 `:23001` 服务进程 **[B]** |
| **直接或间接启动的游戏进程** | 国服：`YuanShen.exe`<br>国际服：`GenshinImpact.exe` **[B]** | `StarRail.exe`（或启动器 `HYP.exe` / `launcher.exe`）**[B]** | `ZenlessZoneZero.exe` **[C]** |
| **进程残留风险等级** | **高风险**：<br>1. 异常弹窗（`MessageBox.Show`）阻塞导致 `BetterGI.exe` 永不退出 **[B]**；<br>2. `--startOneDragon` 偶发完成后不触发退出（#2049）**[B]**；<br>3. 强杀 `BetterGI.exe` 不会自动关闭 `YuanShen.exe`。 | **中风险**：<br>1. 若未设 `pause_after_success: false`，`March7th Assistant.exe` 结束后永久停留在控制台等待输入 **[B]**；<br>2. 锄大地子进程 `Fhoe-Rail` 或浏览器驱动可能残留。 | **中高风险**：<br>1. 若误用 `OneDragon-Launcher.exe`，启动器退出后 `python.exe` 仍在后台运行 **[C]**；<br>2. 游戏进程 `ZenlessZoneZero.exe` 需显式传 `-c` 才会随任务关闭 **[C]**。 |

---

## 3. 工程应对方案：确保 100% 进程受控与零残留（不绕过任何安全机制）

为彻底消除上述逃逸与残留风险，同时严格遵守“不绕过 UAC 或安全机制”的要求，VIvian 与各引擎适配器应采取以下 **4 层防护策略**：

### 策略 1：VIvian 宿主与引擎同级管理员运行（消除 `runas` 跨权限逃逸）
- **实现方式**：VIvian 桌面应用在自身的 `app.manifest` 中正式声明 `<requestedExecutionLevel level="requireAdministrator" uiAccess="false" />`，由用户在启动 VIvian 时通过系统标准 UAC 确认框正规授权。
- **技术收益**：
  1. 当已具备管理员令牌的 VIvian 调用 `BetterGI.exe`、`March7th Assistant.exe` 或 `OneDragon-RuntimeLauncher.exe` 时，可直接设置 `UseShellExecute = false`，子进程直接继承管理员令牌，**绝对不会触发 `RuntimeHelper.RestartAsElevated`（退出码 `559`）或 `AppInfo` 服务中转**，从而 100% 留在 VIvian 的 Windows Job Object 内部。
  2. 对于 March7thAssistant，在执行实际自动化任务时（如 `main`、`daily`、`power`），**适配器应直接调用 `{Root}\March7th Assistant.exe <task>`（而仅在免提权或查询状态时调用 `m7a.exe --status`）**，减少一层不必要的中转进程派生。

### 策略 2：锁定 ZZZ-OneDragon 集成启动器 + 双目标生命周期监控（防“假完成”）
- **强制选型**：VIvian 仅支持绑定 `OneDragon-RuntimeLauncher.exe`（若检测到用户目录只有 `OneDragon-Launcher.exe`，提示下载 `RuntimeLauncher.zip` 或 `WithRuntime-Full.zip`）。
- **复合存活监控（借鉴官方千机链 2.4 节逻辑）**：
  - 不仅监控启动器进程句柄，同时以 2 秒间隔轮询系统进程表中的目标游戏进程（`YuanShen.exe` / `GenshinImpact.exe`、`StarRail.exe`、`ZenlessZoneZero.exe`）以及引擎工作目录下路径匹配的辅助进程（通过 `Process.MainModule.FileName.StartsWith(EngineRootDir)` 精确过滤，绝不误杀系统其它 Python 进程）。
  - 只有当 **（引擎主进程已退出 + 该引擎目录下的所有衍生子进程已退出 + 关联游戏进程已关闭或收到明确完成日志）** 时，才判定当前游戏运行周期彻底结束。

### 策略 3：三级递进式停止与清理协议（Graceful $\rightarrow$ JobKill $\rightarrow$ Path & Game Sweep）
当用户点击「停止」或触发看门狗超时时，适配器按顺序执行：
1. **Level 1（优雅请求，等待 5 秒）**：发送窗口关闭消息（`CloseMainWindow`）或停止信号。
2. **Level 2（Job Object 树终结）**：由 `WindowsProcessSupervisor` 终止关联的 Job Object，清理所有受管子孙进程。
3. **Level 3（按安装路径与游戏进程名兜底清扫）**：
   - 扫描系统进程，凡是 `ExecutablePath` 位于当前 `{EngineRoot}` 目录下的残留进程（如逃逸的 `Fhoe-Rail`、`python.exe`、`chromedriver.exe`），以及对应的目标游戏进程（如 `YuanShen.exe`、`StarRail.exe`、`ZenlessZoneZero.exe`），执行精确清理。
   - **键鼠状态复位**：调用 Win32 `SendInput` 释放修饰键（`Alt`, `Ctrl`, `Shift`）与常用移动键（`W`, `A`, `S`, `D`），防止强杀瞬间某键处于 `KeyDown` 导致用户桌面输入异常（对应 BetterGI [`TaskRunner.cs`](https://gitback.org/babalae/better-genshin-impact/blame/commit/5f02b26cd295fd97bc1ee3968cc322055eee7a69/BetterGenshinImpact/GameTask/TaskRunner.cs) 中的 `Simulation.ReleaseAllKey()` 机制）。
