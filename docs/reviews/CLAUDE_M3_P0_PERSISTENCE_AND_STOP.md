# M3 P0 整改：持久化无界与停止信号/启动许可竞态（Claude）

状态：**待 Codex 独立复审**，未获批准。上一轮材料见 `CLAUDE_M3_H30_REMEDIATION.md`（仍有效，本文只补充本轮）。

## 分支与依赖

- PR #33 `fix/m3-run-ownership`：base `fix/m3-safety-barrier`（#30 head `245c606`）。
- 本轮在 #33 中**合并了 PR #20**（合并提交 `843fdeb`，#20 head `cec8e5b`）。原因：第二个 High 只存在于 #20 的 `StopSignal` 与 #33 的 `RunProcessScope` 组合中，修复必须同时触及两者。因此 #33 现在 = #30 + #20 + Claude 整改，是实际可合并的整合单元；冲突已在 #33 中解决一次，按 #30 → #33 合并即可，无需再单独合并 #20。
- 没有合并 main（main 仍为 `5c49461`，与 #30 的 base 相同），没有改写任何历史，没有强推，没有修改 #31、#32。
- `test/m3-run-ownership-with-20` 已被 #33 取代，不再需要（删除需用户批准）。

## High 1：进程启动后的持久化无界

### 复现（修复前）

`tests/VIvian.Tests/Execution/JournalStallTests.cs`，在 `843fdeb` + 新测试上：**9 个中 8 个失败**（日志与 TRX 见 PR 评论）。注入方式：`StallingJournal` 让指定阶段的写入永不完成（可在测试中释放或令其失败）。

### 设计

1. **串行写入器** `JournalWriter`：一个调度器的所有运行状态写入排成一条链，前一个写入结束（成功或失败）后才把下一个交给存储。迟到完成的写入因此**不可能**落在更新的状态之后。存储层（`RunStore.PublishAsync`）本来就拒绝修改已终态的行，作为第二道保护。
2. **有界等待**：每次写入最多等 `JournalTimeout`；存储已停滞时完全不等（新写入排在其后）；等待期间收到紧急停止即结束等待。紧急停止、升级、对账、释放在 SQLite 永不返回时都能到达。
3. **超时写入的所有权**：超时的写入不被丢弃，而是继续排队并被观察（`ObserveOverdueWriteAsync`）。完成前该运行是**持久化隐患**（`SessionHazard.PersistencePending`）：新队列被阻断（`JournalUnavailable`），确认被拒绝（`RunStillActiveException`）。
4. **迟到完成**：成功且为终态 → 记为已持久化；全部完成且清理已确认 → 隐患自动解除。迟到失败 → 需要用户确认。
5. **进程启动后的写入失败**（启动许可已发放之后的任何写入失败，包括即时失败）→ 需要用户确认。若该运行自身已确认清理、终态也已写入，确认只解除会话门禁，**不记录任何清理证据**，也不改写存储的判定。
6. **应用关闭**：首个停滞写入最多等 `JournalTimeout`（默认 10 s），之后不再等待，因此 30 s 的关闭停止窗口内队列可以结束；未写入的状态在下次启动时由对账记为 Blocked/Unconfirmed。
7. **中止路径**不依赖注入时钟（时钟故障时退回真实时间）。

### 验收测试（全部为确定性测试）

| 场景 | 测试 |
| --- | --- |
| Running 写入挂起 + 紧急停止 / 调用方令牌（关闭） | `RunningWriteHangsButEmergencyStopStillTerminatesAndReleasesTheRun(false/true)` |
| Stopping 写入挂起 | `StoppingWriteHangsButEscalationReconciliationAndDisposalAreReached` |
| Reconciling 写入挂起 | `ReconcilingWriteHangsButDisposalIsReachedAndTheNextQueueWaits` |
| 终态写入挂起后迟到完成 | `TerminalWriteHangsThenCompletesLateAndOnlyThenTheNextQueueMayStart` |
| 旧写入在新状态之后完成：顺序单调、同时最多一个写入 | 上述测试中的 `AssertMonotonic` 与 `MaxConcurrent <= 1` |
| 挂起写入迟到失败 | `StalledWriteThatFailsLateKeepsTheQueueBlockedUntilAcknowledged` |
| 进程启动后写入失败 | `WriteFailureAfterTheProcessStartedBlocksTheNextQueueUntilAcknowledged` |
| 状态不确定时尝试启动下一个队列 | 以上各测试均断言被阻断 |

## High 2：停止信号 / 启动许可竞态

- `StopSignal.Trigger()`（#20 `cec8e5b`）现在的顺序：① 设置 `IsRequested` 标志 → ② 发出停止信号 → ③ 同步取消令牌（执行回调）。
- `RunProcessScope` 在每次启动检查与登记复查时**直接读取**该标志，不再依赖任何取消回调。阻塞的适配器回调无法让启动许可继续有效。原有的显式撤销与令牌回调撤销保留，作为多重保护。
- 撤销仍不等待进程创建；没有引入全局锁；#20 的取消与矛盾证据保护未改动。

测试 `StopRevokesLaunchPermissionEvenWhileACancellationCallbackBlocks(passNoToken: true/false)` 的门闩顺序：停止请求开始 → 适配器回调被阻塞 → 并发 `StartAsync` 申请进程 → 断言 `NothingStarted=true`、监管器调用 0 次 → 回调仍阻塞时队列结束 → 重复停止无害。修复前 `CancellationToken.None` 变体失败（进程被创建）；令牌变体因令牌已处于取消状态而本来就通过。

## Medium

- `RunProcessScope.RevokeAsync`：进程组释放（Job、进程、管道句柄）超时或抛异常 → 返回未确认。释放是确认的必要条件，但永远不是充分条件。
- 重启恢复测试的注释已改为明确：只证明门禁协议；模拟进程组不证明任何进程退出；kill-on-close 只覆盖 VIvian 自己的 Job；UAC、启动器、计划任务等外部进程仍为 Pending；确认是用户的陈述，不是清理证据。
- #20 界面：只排入线程池时显示“正在发送紧急停止…”，送达调度器后才显示“紧急停止已送达调度器，正在等待引擎结束并核对清理…”，不会声称已停止。

## 不稳定测试

`H30_LateResultProcessingThatThrowsIsObservedAndKeepsTheHazard` 已重写：不使用 GC，也不使用全局 `UnobservedTaskException`。用门闩控制迟到返回、释放开始、释放失败的顺序；异常是否被观察，以存储的审计记录中包含异常类型来证明；簿记结束通过 `HazardsChanged` 等待；8 个调度器并发运行。变异检查：去掉迟到异常的 `RequireVerifiedRecovery` 后该测试失败。

关于原先 1/8 失败的根因：探针证明 `Task.WaitAsync` 会观察内部任务的异常（200 次未观察计数为 0），所以不是这里漏观察。最可能的原因是全局处理器统计到了同一测试类中其他测试遗留的任务。由于原始日志没有保存，**无法最终确认**。

稳定性：安全相关测试组（AdapterHang、JournalStall、RunProcessScope、SafetyBarrier、事件与停止测试，共 69 个）在 `6fef89c`、`61e69a8`、`4d3111b` 上本机各连续 40 次全部通过，每次都保存了 TRX（Linux，1 个 Windows 专属测试跳过）。

## Windows CI 上暴露的测试稳定性问题（均已修复，产品代码未改）

1. **虚拟时钟跑在真实 I/O 前面**（运行 41889109132，3 个失败）：写入改为在线程池上串行执行后，测试用的虚拟时钟（每 1 秒虚拟时间约 2 毫秒真实时间）会在真实 SQLite 写入完成前就超过 `JournalTimeout`，或者在运行推进之前就用完虚拟时间预算。本地把 SQLite 写入放慢到 80 ms 后复现（7 个失败）。修复：不测试日志超时的测试，把日志超时设为超过其虚拟预算；推进虚拟时钟前，先等没有健康写入在进行（故意挂起的写入除外，由 `VirtualClock.WaitForHealthyWritesAsync` 判断）。
2. **线程池饥饿**（运行 41891143853、41892014390，各 1 个失败）：多个测试类并行，故意阻塞线程池线程；在 2 核运行器上，其他续体要等好几秒才能拿到线程。修复：故意阻塞的测试操作改在专用线程上执行（`DedicatedThread`）；同步就能生效的撤销改为直接调用；测试进程启动时把线程池最小线程数设为 64（`TestAssemblySetup`，只影响测试进程）。修复后全量测试在本机从约 26 s 降到约 17 s，也印证了饥饿确实存在。

最终提交 `4d3111b` 的证据：Windows CI 7 次（push、pull_request 各 1 次，手动触发 5 次）全部 233/233 通过，WPF 冒烟测试通过；本机安全测试组连续 40 次通过，每次都有 TRX。

## 变异检查（本轮）

| 变异 | 结果 |
| --- | --- |
| 不把停止标志传给 `RunProcessScope` | 竞态测试（`CancellationToken.None`）失败 |
| 写入器不串行 | 3 个测试失败（顺序/并发断言） |
| 不登记超时写入隐患 | 4 个测试失败 |
| 进程启动后的即时写入失败不设门禁 | 1 个测试失败 |
| 迟到异常不设核实恢复 | 重写后的测试失败 |

## 剩余风险与 Pending

- 存储层没有每次运行的序列号；顺序由单一进程内的串行写入器加单实例锁保证。若将来出现多个写入方，需要增加条件更新。
- 事件写入（`AppendEventsAsync`）仍沿用原来的有界刷新（30 s），不在本轮范围内。
- 对 `RunAsync` 开始前的停止请求（L-20-1），本轮未修复，仍按文档说明处理。
- 仍为 Pending、需要桌面实机或用户授权：桌面 UAC、提权子进程的归属、真实引擎、真实游戏、`SessionEnding`/系统关机路径。
