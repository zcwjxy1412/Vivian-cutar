# M3 安全整改：H-30-1 / H-30-2 主开发者补强（Claude）

状态：**待 Codex 独立复审**。本文不是批准结论；CI 通过不等于权限、清理或发行包验收。

- 分支：`fix/m3-run-ownership`，**依赖 PR #30**（基于其 head `245c606`，未改写 #30 的任何历史）。
- PR #20 跟进提交直接追加在 `fix/m3-scheduler-hardening`（`a1decdd`、`06e7716`，未强推）。
- 组合验证分支：`test/m3-run-ownership-with-20`（本分支 + PR #20 的合并，只用于 CI 证据，不应合并）。
- 范围：只用无害的假进程组与 FakeEngine；未运行真实游戏、真实引擎、UAC 提权或任何上游 CLI。

## 1. 对 Codex 在 PR #30 上已有修复的审查结论

Codex 的 `RunProcessScope`（按 RunId 的一次性启动许可）方向正确，但有以下缺口，本分支逐项补上：

| 编号 | 缺口 | 后果 | 处理 |
| --- | --- | --- | --- |
| G1 | 调用真实监管器 `StartAsync` 时一直持有内部锁 | `RevokeLaunches()` 在紧急停止回调（PR #20 同步 Cancel）或超时放弃路径中被慢启动阻塞，停止本身被拖住 | 锁只用于检查/登记，监管器调用在锁外；增加“启动进行中”标志 |
| G2 | 撤销后才完成的启动仍交给适配器 | 适配器拿到一个本应被撤销的进程组 | 登记时复查撤销；已撤销则不交给适配器，立即隔离终止，并告知 `NothingStarted=false` |
| G3 | 监管器抛出非“确认未启动”的异常时未锁存为不安全 | 例如 Job 分配失败、无法确认挂起进程已退出时，可能被当作“未启动” | 除 `EngineStartException{NothingStarted:true}` 外的任何失败（包括 faulted task）都锁存 `HasUnsafeLaunch` |
| G4 | `RevokeAsync` 使用 `TimeProvider.System`，不释放进程组 | 测试无法确定性驱动；Job 句柄要等别处释放 | 使用注入的时钟；终止后释放进程组（kill-on-close 兜底），但释放**从不**算作清理证明 |
| G5 | `RevokeAsync` 期间启动仍在进行 | 可能在启动完成前就报告“已清理” | 有界等待启动结束；超时或仍在进行一律返回未确认 |
| G6 | 超时放弃的任务后来出错无人观察 | `UnobservedTaskException` | 新增 `TaskObservation.ObserveLater`，用于迟到释放、审计写入、终止、隔离任务 |
| G7 | 事件泵晚于 `watchCts.Cancel()` 启动时丢失已缓冲事件 | 运行记录不完整（基线上 `EventsFromOtherRunsAreIgnored` 偶发失败的根因） | 取消前先有界（2 s）等待事件流自然结束 |

## 2. H-30-1：超时后的迟到进程创建

保证链（按发生顺序）：

1. 适配器只拿到 `RunProcessScope`，不拿真实监管器；RunId 不符、已用过、已撤销、令牌已取消都在调用监管器**之前**拒绝（`NothingStarted=true`）。
2. 撤销（停止令牌回调、调用超时、放弃、结束）只设置标志，**从不**等待启动。
3. 生产监管器同步完成“挂起创建 → 放入 Job → 恢复”；未同步完成的启动被视为所有权未知：锁存不安全并在完成后隔离终止。
4. 启动完成时若已撤销：进程组登记以供终止，并立即隔离（终止 + 释放），适配器收到 `NothingStarted=false`。
5. 调度器在放弃/失败/结束路径调用 `RevokeAsync`；返回 false 时设置 `RequiresVerifiedRecovery`，该运行在本会话中不能被普通确认解除。

不在保证范围内（已文档化，真实适配器仍由 `RealAdapterUnverified` 门禁全部阻断）：适配器绕过监管器自行创建进程；经 UAC/AppInfo、任务计划程序或启动器脱离 Job 的进程。对这些情况 Job 计数为零**不**代表已清理。

## 3. H-30-2：迟到清理无界

- 每个迟到等待都有界：迟到结果释放（`DisposeTimeout`）、审计写入（`JournalTimeout`）、进程组终止与释放、隔离任务（`DisposeTimeout`）。
- 簿记与安全保证分离：`PendingOperations` 回到 0 只代表等待结束；任何超时、异常或未确认的终止都会设置 `RequiresVerifiedRecovery`，屏障继续关闭，`AcknowledgeAsync` 抛出 `RunStillActiveException`。
- 清理超时**从不**变成 `CleanupState.Confirmed`。
- 后台清理任务数量有界：每个运行最多一个启动与少数被放弃的调用；屏障关闭期间不能启动新队列，因此不会无限增长。
- 受控恢复路径：VIvian 退出时关闭其持有的全部 Job 句柄（kill-on-close），Windows 终止其中所有进程；重启时 `ReconcileInterruptedRunsAsync` 把未结束的行记为 Blocked/Unconfirmed（从不记为成功），用户再按正常流程确认。存储的判定不会被改写。

## 4. 其他评审项

| 编号 | 处理 |
| --- | --- |
| M-30-1 | 上一会话遗留的未结束行被计为“未解决”却无法确认。现在：该行不在本会话屏障中且没有队列运行时，确认前先在占用运行标志的情况下调用 `ReconcileInterruptedRunsAsync`（从不记为成功），再重试一次。 |
| M-30-2 / M-30-3 / M-30-4 | Codex 已在 #30 处理（`Task.Run`/CTS 初始化异常安全、初始化移入 try、`CountUnresolvedAsync` 有界且超时失败即关闭）。本分支未改动，保留其测试。 |
| L-30-1 | 结束为未确认时，先关闭屏障再发布 Blocked 状态，用户不可能抢在屏障之前确认已显示的行。 |
| PR #20 | `Trigger()` 先释放停止信号再同步取消令牌：阻塞的适配器取消回调不会再拖住紧急停止路径；关联令牌仍同步可见。窗口关闭时先紧急停止，再有界等待（30 s）队列写入最终状态；超时仍退出，依赖 kill-on-close 与启动对账。WPF 冒烟测试新增关闭停止检查。 |
| PR #23 | 核对结果：#23 的 `OwnsProcess` 与现有 `ActiveProcessCount` 在 Job 查询失败时抛出异常，而不是返回“不属于”或“为零”；本分支的 `RevokeAsync` 把终止过程中的任何异常一律视为未确认。#23 本轮未改动，桌面 UAC 验证保持 Pending。 |
| PR #24 / PR #26 | 未在本轮处理（优先级低于 H-30）；建议列入下一轮：根/中间目录重解析点、文件身份与版本/哈希 TOCTOU、慢文件系统查询取消；固定官方 SHA-256 回归测试，自算哈希不能作为独立验证。 |

Gemini 五份文档（PR #32，未修改）已阅读。其中“VIvian 与引擎同级管理员运行”与“按安装路径/进程名兜底清扫”与本项目安全约束冲突，勘误文档也已否定；本实现只按 VIvian 持有的 Job/句柄与 RunId 终止，不扫描路径或进程名。

## 5. 新增测试（均为确定性测试，虚拟时钟或显式门闩驱动）

`RunProcessScopeTests`：
- `RevokeLaunchesDoesNotWaitForALaunchInProgress`：创建期间撤销不阻塞；迟到进程组立即终止。
- `RevokeConcurrentWithLaunchTerminatesRegisteredGroupAndRejectsLateRetry`（改为确定性；断言适配器收到 `NothingStarted=false`）。
- `RevokeAsyncDuringAStuckLaunchIsUnconfirmedAndBounded`：终止与登记竞争，有界并返回未确认。
- `SupervisorFailureThatMayHaveStartedSomethingLatchesUnsafe`：Job 分配失败。
- `UnexpectedSupervisorErrorsAreNeverNothingStarted`（同步抛出 / faulted task）。
- `ProvenNothingStartedFromAFaultedTaskStaysSafe`。
- `ForeignRunIdGroupIsQuarantinedAndUnsafe`。
- `AsynchronousLaunchIsRejectedAndTheLateGroupIsTerminated`。
- `UnprovenTerminationIsNeverConfirmed`（挂起 / Unconfirmed / 抛异常）。
- `RevocationAppliesEvenWhenTheAdapterPassesNoToken`（`CancellationToken.None`）。

`AdapterHangTests`（调度器层）：
- `H30_EmergencyStopDuringLaunchNeverWaitsAndTheLaunchedGroupIsTerminated`：创建阻塞时多次并发紧急停止。
- `H30_RepeatedEmergencyStopsEndTheQueueOnceAndKeepTheBarrierClosed`。
- `H30_ConcurrentCleanupTimeoutsAreAllBoundedAndNeverConfirmed`：终止、迟到释放、审计写入同时超时。
- `H30_LateResultProcessingThatThrowsIsObservedAndKeepsTheHazard`。
- `H30_RestartIsTheControlledRecoveryPathForAnUnresolvedCleanup`。
- `M30_AcknowledgingARowLeftOpenByAnEarlierSessionClosesItFirst`。
- `L30_BarrierClosesBeforeTheBlockedStateIsPublished`。

`SchedulerTests`：`EventsAreNotLostWhenTheEventPumpStartsLate`；PR #20 上 `EmergencyStopIsDeliveredWhileAnAdapterCancellationCallbackBlocks`（组合分支中改为在 StartAsync 令牌上注册，因为 #30 后适配器令牌按调用划分）。

已有的 H-30 测试（`DisposeAsync` 永不返回、审计写入永不完成、迟到操作返回、会话内确认被拒绝）全部保留。

### 变异检查（证明测试能发现问题）

| 变异 | 结果 |
| --- | --- |
| 去掉通用异常的不安全锁存 | 2 个测试失败 |
| `RevokeAsync` 忽略进行中的启动 | 1 个测试失败 |
| 撤销后的启动仍交给适配器 | 3 个测试失败 |
| `RevokeLaunches` 等待进行中的启动 | 调度器紧急停止测试挂起（被检测到） |
| 删除 L-30-1 的预先关闭屏障 | `L30_…` 失败 |
| 删除 M-30-1 的对账 | `M30_…` 失败 |
| 删除事件排空等待 | `EventsAreNotLost…` 3/3 失败 |
| PR #20：取消后才释放信号 | #20 单独与组合分支上均失败 |

本地（Linux，Windows 专属测试跳过）：本分支 190 通过 / 20 跳过，连续 5 次；组合分支 197 通过 / 20 跳过，连续 5 次。Windows CI 结果见 PR 评论。

## 6. 已知限制与风险

- 生产监管器必须同步完成启动；异步监管器一律被视为所有权未知（保守但会阻断）。
- `PublishAsync` 仍未设时限（有界后可能出现迟到写入覆盖更新状态的顺序问题，需要单独设计）；SQLite 自身的忙等待超时是目前唯一的上界。
- 受控恢复依赖“重启 VIvian”；会话内没有自动解除 `RequiresVerifiedRecovery` 的途径（这是有意的）。
- M-30-1 的对账会把本会话中其他“终态未写入”的行也记为 Blocked/Unconfirmed（只会更保守）。
- 一次未能复现的偶发失败：`H30_LateResultProcessingThatThrowsIsObservedAndKeepsTheHazard` 在 8 次分组运行中失败 1 次，其后 30+ 次及 10 次全量均未复现，失败信息未保留。请 Codex 重点复查其等待条件。
- 窗口关闭协调只有 WPF 冒烟测试覆盖视图模型部分，`Closing` 事件处理需人工审查。

## 7. 请 Codex 独立核实的问题

1. `RunProcessScope` 中“锁外调用监管器 + 登记时复查撤销”是否还存在任何交错使进程组既未登记也未隔离？
2. `RevokeAsync` 对 faulted/超时的释放任务只做观察、不改变结果，是否足够？
3. M-30-1 在 `_running` 上的 CompareExchange 与 `RunAsync` 的互斥是否完整？
4. PR #20 的信号先于取消，是否会让任何代码在令牌尚未取消时误判“已停止”？
5. 上述偶发失败的根因。
