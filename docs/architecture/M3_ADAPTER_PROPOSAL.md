# VIvian M3 真实引擎适配层架构设计方案 (M3_ADAPTER_PROPOSAL.md)

> Gemini 原始研究。文中的版本状态、示例和建议保留原貌，实测状态与冲突见[审查记录](../reviews/M3_GEMINI_RESEARCH_VALIDATION.md)。

文档路径：docs/architecture/M3_ADAPTER_PROPOSAL.md

对接基准：VIvian M2 统一执行内核（IGameAdapter, IEngineRun, ProcessRunner, JobObject）

设计目标：在完全不破坏 M2 核心执行契约与 158 项既有单元测试的前提下，平滑接入 BetterGI、March7thAssistant、ZenlessZoneZero-OneDragon 三款真实外部自动化引擎。

## 1. 现有 M2 接口承载能力评估

在 VIvian M2 阶段，系统抽象了通用的执行管道与进程管理接口：

1.  IGameAdapter：定义了适配器的初始化、环境前置校验（ValidatePrerequisitesAsync）、执行规划与运行上下文构建。
2.  IEngineRun：代表一个具体的引擎运行实例，管理其底层进程句柄、流重定向、取消令牌（CancellationToken）与状态流。
3.  ProcessRunner / IProcessManager：基于 Win32 Job Object 实现进程树的生命周期绑定与资源隔离。

### 承载能力诊断分析：

- 高度契合点：M2 的异步执行管道（ExecuteAsync）、取消逻辑（CancelAsync）与基于 Windows Job Object 的进程树收容设计，完美契合三款引擎作为独立进程启动并防止残留孤儿进程的需求。
- M2 到 M3 的核心差异与必要扩展：

<!-- -->

- M2 测试阶段多采用 Mock 进程或标准返回退出码的测试桩；但上游三款真实引擎存在“退出码 0 不代表子任务成功”（BetterGI 异常被吞、或未设自动退出直接挂起）与“部分引擎通过独立文件而非标准输出记录核心事件”（BetterGI 写 Serilog 文件、ZZZ-OneDragon 写 .log/）的客观现实。
- 结论：严禁修改 M2 的核心 IGameAdapter 抽象基类契约！所有针对特定上游引擎的“双轨日志解析、前置配置补丁、版本兼容门禁、证据链比对”全部封装在各自的派生适配器类（BetterGiAdapter、March7thAdapter、ZzzOneDragonAdapter）及其内部实现细节中。

## 2. 真实引擎适配器架构实现细则

                   [ VIvian M2 Execution Engine ]
                                 |
                       +---------+---------+
                       |   IGameAdapter    |  (M2 Stable Core Contract)
                       +---------+---------+
                                 |
             +-------------------+-------------------+
             |                   |                   |
    +-----------------+ +-----------------+ +-----------------+
    | BetterGiAdapter | | March7thAdapter | | ZzzOneDragonAd. |  (M3 Concrete Adapters)
    | - Serilog Tailer| | - Stdout Parser | | - .log/ Tailer  |
    | - JSON Patch    | | - YAML Patch    | | - Path Guard    |
    | - Mutex Cleaner | | - UAC Probe     | | - Launcher Bind |
    +-----------------+ +-----------------+ +-----------------+
             |                   |                   |
             +-------------------+-------------------+
                                 |
                       [ BaseProcessRunner ]
                 (Win32 Job Object Process Guard)

### 2.1 引擎能力声明（Capability Declaration）

每个适配器暴露强类型能力元数据，供 VIvian 上层调度器与 UI 页面做动态渲染：

    public record EngineCapabilities(
        bool SupportsSubTaskCommandLine,      // 是否支持在 CLI 中直接指定子任务（仅 March7th 为 true）
        bool SupportsPureHeadless,             // 是否支持完全控制台静默（March7th 为 true，其余为 false）
        bool SupportsStructuredStatusQuery,    // 是否支持命令行 JSON 状态查询（March7th 为 true）
        bool RequiresStrictAsciiPath,          // 路径是否严禁中文/空格（ZZZ-OneDragon 为 true）
        bool RequiresPreRunProcessCleanup,     // 启动前是否必须清理残留同名进程（BetterGI 为 true）
        TimeSpan DefaultExecutionTimeout       // 默认超时阈值（原神 45m / 星铁 40m / 绝区零 30m）
    );

### 2.2 配置隔离与补丁机制（Config Patching）

适配器不全量反序列化上游复杂的配队/战斗策略内部模型，采用 非侵入式 AST 键值修补（Non-Intrusive Patching）：

1.  BetterGiAdapter：

- 检查 {Root}\User\OneDragon\VivianDefault.json。
- 动态更新字典 TaskEnabledList\[key\] = value。
- 强制将 CompletionAction 设为 "关闭游戏和软件"。

2.  March7thAdapter：

- 使用 YamlDotNet 读取 {Root}\config.yaml。
- 强制覆盖系统键：

  ```yaml
  pause_after_success: false
  exit_after_failure: true
  check_update: false
  auto_update: false
  after_finish: "None"
  ```

- 写入日常开关：daily_enable, power_enable 等。

3.  ZzzOneDragonAdapter：

- 修改 {Root}\config\env.yml，强制 auto_update: false。
- 修改目标实例目录（如 config\01\\下的日常任务开关。

### 2.3 启动参数构建器（Argument Builder）

    public class EngineArgumentBuilder
    {
        public static ProcessStartInfo BuildBetterGi(string rootDir, string profileName) => new()
        {
            FileName = Path.Combine(rootDir, "BetterGI.exe"),
            Arguments = $"startOneDragon {profileName}",
            WorkingDirectory = rootDir,
            UseShellExecute = false,
            CreateNoWindow = false
        };

        public static ProcessStartInfo BuildMarch7th(string rootDir, string taskName) => new()
        {
            FileName = Path.Combine(rootDir, "m7a.exe"),
            Arguments = $"{taskName} --after-finish None",
            WorkingDirectory = rootDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };

        public static ProcessStartInfo BuildZzzOneDragon(string rootDir, string instances = "1") => new()
        {
            FileName = Path.Combine(rootDir, "OneDragon-RuntimeLauncher.exe"),
            Arguments = string.IsNullOrEmpty(instances) ? "-o -c" : $"-o -c -i {instances}",
            WorkingDirectory = rootDir,
            UseShellExecute = false,
            CreateNoWindow = false
        };
    }

### 2.4 事件转换与三维成功证据确认（Success Evidence Engine）

适配器将外部进程的非标准原始事件转换为 VIvian 核心统一的 TaskProgressEvent：

    [原始信号来源]                                              [VIvian 统一领域事件]
    BetterGI: "一条龙任务执行: 2/6"               ------------->  TaskProgressEvent(Genshin, 2, 6, "自动秘境")
    March7th: "开始执行：每日实训"                ------------->  TaskProgressEvent(StarRail, 1, 3, "每日实训")
    ZZZ: "[one_dragon_app.py] 下一个应用..."      ------------->  TaskProgressEvent(ZZZ, 3, 8, "咖啡店")

#### 严格证据链判定三要素：

1.  要素一：状态机闭环。必须按顺序观察到 Started ![原文内联图示](../research/assets/gemini-m3/15a864e59603.png) ProgressUpdates ![原文内联图示](../research/assets/gemini-m3/15a864e59603.png) CompletedMarker。
2.  要素二：致命异常排查。在执行区间内的日志中，不得出现未经捕获的 \[FATAL\], UnHandle Exception, Traceback, PaddleOcr预热异常。
3.  要素三：进程正常归宿。进程退出且没有触发看门狗硬超时；若有残存游戏窗口，在冷却期内被安全关闭。

### 2.5 停止与多进程树安全清理（Process Tree Teardown）

1.  阶段 1：请求优雅终止：

- BetterGI：发送 Win32 WM_CLOSE 给主窗口句柄；
- March7th：发送全局停止热键 F10；
- 等待 5 秒 观察进程是否响应退出。

2.  阶段 2：强制进程树清理：

- 若 5 秒未退出，调用 Windows Job Object 的 TerminateJobObject，或通过 P/Invoke GenerateConsoleCtrlEvent / Process.Kill(entireProcessTree: true) 彻底清理引擎及其可能派生的辅助子进程（如 7za.exe, python.exe）。
- 随之调用 Win32 keybd_event / SendInput 释放 W, A, S, D, F, Alt, Ctrl 等所有可能卡住的物理按键状态。

## 3. 对 Codex 现有 Issue \#11 / \#12 的解耦建议

- Issue \#11（多进程执行管理与状态同步）：

<!-- -->

- Codex 正在完善通用的进程生命周期抽象。
- 建议：保持底层通用进程管道纯粹，仅暴露 IProcessHandle、IOutputLogStream 与 JobObjectPool。三款游戏的具体日志行正则解析与特征判定全量收敛在 src/VIvian.Infrastructure/Adapters/{Game}/ 目录下，互不污染。

<!-- -->

- Issue \#12（日志流追踪器与故障检测）：

<!-- -->

- Codex 正在开发日志跟踪组件。
- 建议：日志跟踪组件应支持双源驱动：既能挂载 Process.StandardOutput 管道（面向 March7th），又能基于 FileSystemWatcher + 增量流挂载独立物理日志文件（面向 BetterGI 与 ZZZ-OneDragon）。
