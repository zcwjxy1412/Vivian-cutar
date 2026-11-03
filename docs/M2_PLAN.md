# VIvian M2 计划：执行内核（不接入真实引擎）

状态：**草案，待用户确认**　日期：2026-11-03　基线：`main` @ `40a599a`（M1，Windows CI 已验证）

## 1. 目标与边界

M2 交付一个**可信的执行内核**：能按队列串行运行任务、实时报告状态、只管理自己启动的进程、在崩溃后诚实对账。全部用模拟引擎（FakeEngine）验证。

依据 `INTEGRATED_REVIEW.md` §4–§5：三个上游引擎的 CLI、配置键、日志格式、结束信号仍是"待核实"（C-1～C-5 未关闭），所以 M2 **不写任何真实适配器**，避免把猜测写进生产代码。

| 纳入 M2 | 不纳入（后续里程碑） |
|---|---|
| `IGameAdapter` 契约与三层状态模型 | BetterGI / M7A / ZZZ 真实适配器（M3，需先完成实机验证） |
| 串行调度器状态机、紧急停止 | 提权执行器（需先做 Spike，M3 前决定） |
| 进程归属管理（Windows Job Object） | 修改上游配置文件（M3） |
| 运行记录持久化与启动时对账 | 上游版本检测、更新与回滚（M4） |
| 单实例锁 | 托盘、主题、WPF-UI 评估（M5） |
| FakeEngine 与故障场景测试 | 安装器、签名（M6） |
| 队列与运行监控界面（仅演示模式） | 定时自动启动（推迟） |

## 2. 核心设计

### 2.1 三层状态，互不替代（对应 D01/D02）

| 层 | 取值 |
|---|---|
| 执行阶段 `RunPhase` | Pending → Preflight → Starting → Running → Stopping → Reconciling → Finished；任意阶段可进入 Blocked |
| 任务结果 `RunOutcome` | Success / Failed / Cancelled / Indeterminate；运行中为空 |
| 清理结果 `CleanupState` | NotRequired / Pending / Confirmed / Unconfirmed |

规则：
- 进程已结束但证据不足，结果判为 **Indeterminate**，不判失败，也不判成功。
- "没有报错""退出码为 0""游戏已关闭"都**不能**作为成功的依据。
- 只要清理结果不是 Confirmed，**下一任务一律不启动**，整个队列进入 Blocked，等待用户处理。

### 2.2 适配器契约（初稿，M2 中冻结）

```csharp
public interface IGameAdapter
{
    GameId Game { get; }
    string RuleVersion { get; }                          // 适配规则版本，写入每条运行记录
    Task<PreflightResult> PreflightAsync(RunContext ctx, CancellationToken ct);
    Task<IEngineRun> StartAsync(RunContext ctx, IProcessSupervisor supervisor, CancellationToken ct);
}

public interface IEngineRun : IAsyncDisposable
{
    Guid RunId { get; }
    IAsyncEnumerable<RunEvent> Events { get; }           // 只包含属于本次运行的事件
    Task<RunVerdict> WaitForVerdictAsync(CancellationToken ct);  // 结果 + 原因码 + 证据引用
    Task<StopResult> RequestStopAsync(StopMode mode);    // Graceful / Emergency（立即，无倒计时，D08）
}
```

和原方案相比的修改：取消 `KillForcefullyAsync` 按进程名结束进程的做法（D03）；配置读写不放进 M2 的契约，等 M3 实机验证后再加（D07）。

### 2.3 进程归属（对应 D03/D05）

- `ProcessSupervisor` 用 Windows Job Object 启动并包住引擎进程。只有在这个 Job 里的进程才算"本次运行"的进程，才可以停止。
- **绝不按进程名查找或结束进程**。如果发现同名但不属于本次运行的进程，只阻断队列并提示用户，不接管、不强杀。
- 记录 PID + 进程创建时间 + RunId，防止 PID 被系统复用后误判。

### 2.4 运行记录与崩溃对账

- SQLite 从模式 v1 升级到 v2，新增 `runs`（运行记录）和 `run_events`（运行事件）两张表，并提供迁移测试。
- VIvian 启动时如果发现上次有停在 Running/Stopping 的记录，转入 Reconciling：结果标为 Indeterminate，清理标为 Unconfirmed，不自动续跑。

### 2.5 单实例锁

使用命名互斥体。遇到遗弃（abandoned）或拒绝访问时进入对账提示，不当作"没人在运行"（D05）。这个锁只协调 VIvian 自己，不承诺能阻止用户手动打开上游引擎。

## 3. 工作拆分（每项一个 PR，每个 PR 都必须通过 Windows CI）

| # | 内容 | 主要测试 |
|---|---|---|
| M2.1 | 状态模型、契约、调度器状态机（纯逻辑） | 状态转换表全覆盖；清理未确认则阻断队列；紧急停止立即生效 |
| M2.2 | 模式 v2 迁移、运行记录、启动对账 | v1→v2 迁移保留设置；崩溃后记录正确对账；模式版本过新时拒绝启动 |
| M2.3 | FakeEngine（独立测试程序）+ ProcessSupervisor | 见 §4 故障场景；只在 Windows CI 上跑 |
| M2.4 | 单实例锁 | 双开、遗弃互斥体、拒绝访问 |
| M2.5 | 队列与运行监控界面（明确标注"演示模式"） | 冒烟测试扩展为跑完一次 FakeEngine 队列 |
| M2.6 | `M2_ACCEPTANCE.md`、发布 `v0.2.0-m2` 预发布版 | 汇总证据 |

## 4. FakeEngine 故障场景（验收必须全部通过）

1. 正常成功并给出完成证据 → Success
2. 退出码 0 但没有完成证据 → Indeterminate
3. 明确报失败 → Failed
4. 日志里同时出现成功和失败字样 → 按本次运行的终态证据判定，不按关键词一票否决（D02）
5. 读到上一次运行残留的旧日志 → 忽略，不影响本次结果
6. 父进程退出、子进程仍在运行 → Job 清理子进程后 Confirmed；清不掉则 Unconfirmed，队列阻断
7. 长时间无输出（卡死） → 超时后停止，结果 Indeterminate 或 Failed（取决于是否有失败证据）
8. 停止请求被忽略 → 升级为终止 Job；仍失败则 Unconfirmed，队列阻断
9. 用户紧急停止 → 立即发出停止，结果 Cancelled
10. VIvian 自身在运行中被杀 → 重启后对账为 Indeterminate + Unconfirmed
11. 系统中已存在一个不属于本次运行的同名进程 → 预检 Blocked，不碰该进程

## 5. M2 验收标准

- Windows CI：构建 0 warning / 0 error，全部测试通过（M1 的 14 项加上 M2 新增项），WPF 冒烟测试跑完一次演示队列。
- 第 4 节 11 个场景全部有对应的自动化测试，并且在 Windows 上实际运行通过。
- 代码中不存在按进程名结束进程的路径（通过代码检查确认）。
- 界面上任何地方都不会把演示模式的结果显示为真实游戏的结果。
- 人工验收清单：沿用 M1 的视觉/DPI 检查，另加"演示队列运行与紧急停止"。

## 6. 与 M3 的衔接（实机验证）

真实适配器必须先完成 `INTEGRATED_REVIEW.md` §5 中 S0–S3 的验证。这些验证**需要一台装好三个引擎和游戏的 Windows 电脑**，云端环境做不了。M2 开发期间可以先准备好验证步骤和记录模板，M2 结束时再决定由谁、在哪台机器上执行。

## 7. 开发方式说明

- 当前云端环境无法安装 .NET SDK（下载被网络拦截），所以每次编译和测试都通过推送到 GitHub 由 Windows CI 完成，每轮约 2 分钟。
- 每个子项单独开分支、单独开 PR，CI 全部通过后合并到 `main`。
- Codex 恢复后可以按本计划认领子项，共用同一套 CI 门禁。

## 8. 待用户确认

1. M2 范围是否认可：只做执行内核 + FakeEngine，不接真实引擎？
2. 单个游戏失败后是否默认继续下一个游戏？建议：**仅在清理已确认时**才继续，否则停止整个队列。
3. 您是否有一台可用于 M3 实机验证的 Windows 电脑（已安装或愿意安装三个引擎）？
