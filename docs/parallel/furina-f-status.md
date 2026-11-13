# F 执行内核首批交付

- 日期：2026-11-13（America/New_York）；总任务 #59，F #66。
- 计划：`docs/parallel/furina-vnext/FURINA_UI_AI_BRANCH_PLAN.md` §F；按已批准计划执行，无新设计确认。
- SOURCE_BASE_SHA：`ce3ebeea79c507825e66a9e97097a6f80ede33cc`。
- BASE_SHA / 公共依赖：`8f857cf2cd16515d81f4c65f8e50b693374dccd4`（契约 v1）。
- 分支：`feat/furina-runtime`；工作树：`C:\Users\胡桃\.codex\worktrees\furina-vnext-runtime\Vivian-source`。
- HEAD：以本文件所在提交及后续同分支提交为准，交付精确 SHA 记录在 PR / 本聊天最终回复（避免自引用 SHA）。
- 状态：首批证据补强与公共缺口审计已交付；不宣称 F 全部验收完成。

## 范围与执行记录

1. 已核对分配目录 AGENTS、基线、契约、所有权及原始任务书 F。
2. 交付不更改产品实现：已有能力先用测试证明；跨分支公共能力交 I 决策，未另造执行入口。仅在测试敏感性验证期间暂时放宽锁共享模式，验证失败后按原始字节恢复，最终产品 diff 为零。
3. 新增两项桌面租约测试与一项暂停负向测试；均验证既有行为，不把测试先通过说成修复缺陷的 RED→GREEN。
4. 测试使用唯一临时目录；锁测试通过 `AcquireAt` 使用临时锁，不获取或清除正式共同桌面锁。
5. 只运行模拟器、独立进程探针和离线测试；未启动真实游戏、桌面 UI、正式安装或读写正式账号配置。
6. Superpowers 流程适配：本文件作为 F 的持久执行台账；任务书按字母分支而非编号编码步骤，不使用脚本生成另一份任务计划。既有分配工作树直接复用。

## 已有能力与证据映射

| 能力 / 限定 | 源码依据 | 本轮运行的测试 |
| --- | --- | --- |
| 同一调度器拒绝重入；不同游戏按序执行 | `Core/Execution/QueueScheduler.cs` 的 `_running` 原子门、顺序循环 | `SchedulerTests.SecondQueueCannotRunConcurrently`、`AllGamesWithCompletionEvidenceSucceedInOrder` |
| 清理未确认、日志无法写入、适配器迟到操作都会阻断后续队列 | `QueueScheduler`、`SafetyBarrier`、`RunProcessScope`、`JournalWriter` | `SchedulerTests.UnconfirmedCleanupBlocksTheRestOfTheQueue`、`SafetyBarrierTests`、`AdapterHangTests`、`JournalStallTests`、`RunProcessScopeTests` |
| 桌面锁路径独立于工作树/数据目录；锁持有期包括清理标记被清除后到 Dispose 前 | `Infrastructure/Processes/DesktopOperationLease.cs`；用户目录下共同文件 + `FileShare.None` | `SharedPathUsesTheUserProfileOutsidePackagedAppData`、`DifferentProfilesCannotOverlapAndCleanHandoffReleasesTheDesktop`、新增 `LeaseExcludesASecondProcessUntilConfirmedCleanAndDisposed` |
| 异常释放句柄不清除恢复标记；只有相同档案可恢复 | 同上；`MarkDirty` 持久刷新，`Dispose` 不调用 `ConfirmClean` | `UnconfirmedCleanupBlocksAllNewRunsAndOnlyOwningProfileCanRecover`、新增 `FailedRecoveryDoesNotReleaseTheDirtyMarker` |
| 监管进程身份、子进程和宿主崩溃 | `WindowsProcessSupervisor` 先挂起创建、加入 Job 后继续；记录 PID/创建时间；Job kill-on-close；不按名称结束 | `SupervisorTests.RootIdentityIsRecordedAndOutputIsUtf8`、`SameNamedExternalProcessSurvivesJobTermination`、`OrphanedChildStaysInsideTheJobAndIsTerminated`、`FakeEngineScenarioTests.S10_VivianCrashKillsItsEnginesAndRestartReconcilesHonestly` |
| 星铁 PID 复用不等于旧 worker 存活，不能检查时保持未知 | `March7thWorkerExit`、`March7thUpstreamActivity` | `March7thWorkerExitTests`、`M5StopAndCleanupTests.ActivityFollowsExactPidAndCreationTime`、`ActiveRunIsAdoptedOnlyForThisTaskAndExactProcess` |
| 业务结果与退出/清理分离；其他 RunId / 无证据退出不能算成功 | `ExecutionModel`、`QueueScheduler`、各适配器判据 | `StateModelTests`、`SchedulerTests.EvidenceForAnotherRunDoesNotCountAsSuccess`、`FakeEngineScenarioTests.S02_ExitCodeZeroWithoutEvidenceIsIndeterminate` |
| 星铁 A→B、独立配置、取消、宿主退出和恢复失败阻断 | `MultiAccountOrchestrator`；主页批次持有 DesktopOperationLease | `MultiAccountOrchestratorTests.RunsTwoAccountsAndUsesFirstSnapshotInUnifiedMode`、`IndependentModeKeepsEachAccountsOwnSettings`、`UserCancellationStopsGameAndPersistsStoppedBatch`、`UnconfirmedHostExitIsNeverReportedAsCompleted`、`UnrestoredConfigurationBlocksTheNextAccount` |
| 星铁暂停/继续有控制文件回执；无回执不能标暂停 | `March7thRun.SetPausedAsync`、`IPausableEngineRun`；`QueueViewModel` 等待返回值 | `M5StopAndCleanupTests.PauseAndResumeWaitForWorkerAcknowledgement`、新增 `CancelledPauseWithoutWorkerAcknowledgementNeverReportsPaused` |

表中路径省略 `src/VIvian.` 前缀以便阅读。新增跨进程测试启动隐藏 PowerShell 子进程探测同一临时锁文件，检查占用时拒绝、清理但未释放时仍拒绝、Dispose 后可访问。它不是两个完整 VIvian 应用同时运行的实机测试。恢复异常测试覆盖租约语义，批次恢复失败由既有 orchestrator 测试覆盖。

## 公共缺口与最小方案（交 I，不在 F 改共享文件）

### F-I-1：并发互斥不等于请求幂等

现状：`QueueScheduler.RunAsync` 每次生成新 QueueId / RunId，结束后释放 `_running`；`QueueItem` 没有请求键。`UnifiedTaskControl.ExecuteTaskPlan` 验证授权、安装、空闲和恢复状态，但没有持久的请求回执。不能把重入测试通过报告成模型重试不会重复执行。一个队列还明确拒绝重复 GameId，多账号当前通过外部批次编排完成。

建议最小公共改动：

- `src/VIvian.Core/Execution/ExecutionModel.cs`：新增执行请求身份（`RequestId`、`PlanId`、`ConfirmedRevision`）及关联目标（`Game`、`AccountId`、`ConfigurationRevision`）；不要把密钥/登录凭据带入 DTO。
- `src/VIvian.App/UnifiedTaskControl.cs`：唯一提交入口接收宿主生成的请求身份和不可变计划快照；同 key+同计划返回已有执行回执，同 key+不同内容拒绝。
- I 负责数据库迁移及存储：唯一请求键必须在副作用前原子保留，持久关联 QueueId/RunId；崩溃后保留未决记录而非自动重试。UI 按钮和 AI 确认共用该入口；仅进程内字典不能满足重启场景。

消费用例：C 重放相同确认请求；UI 双击与 AI 同时提交；同请求完成后模型重试；保留请求后、启动前崩溃；已启动但回执未落库时重启。预期均不会产生第二次业务动作，无法判定的请求进入恢复。不同账号/配置版本不能复用同请求键。

### F-I-2：输入前安全失焦是独立门，不能借用按钮状态

现状：仅 `March7thRun` 实现 `IPausableEngineRun`；BetterGI/OneDragon 没有该暂停契约。`AssistantWindow.xaml.cs` 的 Loaded 直接 `InputBox.Focus()`；当前公共接口没有“允许聊天输入”的确认或持有期。`QueueViewModel` 的暂停操作也没有覆盖登录、切号、清理和新一轮启动之间的空窗。

建议最小公共改动：

- I 定义统一 `TryAcquireAssistantInputAsync(ct)` 能力，返回可释放的输入许可（或拒绝原因），在许可持有期阻止新的桌面操作；与任务启动使用同一个原子门。不可先检查空闲再异步启用输入。
- 空闲且无未决恢复可授予；运行中默认拒绝。仅当当前执行阶段及引擎已通过真实释放键鼠验收时，绑定当前 RunId 发起暂停，确认后才授予。超时、取消、运行切换、停止、清理和未知状态均不授予；暂停回执本身不自动等同于已验收安全失焦。
- B 在获取许可后才启用并聚焦输入；关闭窗口释放许可但不取消游戏、不自动继续；C 保持本地停止入口独立于模型在线。
- 具体落点：公共定义由 I 在 Core/Execution 契约中添加，宿主协调接入由 I/B 协作；F 可在契约 v2 后提供运行能力消费。不改 App.xaml/App.xaml.cs 或越权改视图。

消费用例：获取许可期间另一入口提交任务；暂停等待中 RunId 切换；用户取消等待；无暂停能力的引擎；登录/切号尚无 IEngineRun；旧暂停状态残留；聊天收到消息不得夺焦点。此轮没有宣称任一引擎满足实机安全失焦。

### F-I-3：运行事件关联不完整

`RunContext` 只有 RunId/QueueId/Game/CreatedAt；`RunEvent` 只有 RunId/时间/类型/消息；`RunUpdate` 无账号与配置 revision。批次外部有账号记录，但公共事件无法独立绑定确认计划。建议 I 增加上述不可变关联元数据并贯穿 QueueItem→RunContext→RunUpdate/持久运行记录；事件可由 RunId 查询绑定，不强求每条重复存储。C/B 展示必须从已绑定上下文取账号与配置，不能用“当前选中账号”补齐历史事件。

消费用例：A 执行时 UI 改选 B；旧 RunId 事件迟到；同账号确认后配置 revision 改动；重启读取历史回执。元数据不匹配时拒绝执行或明确拒绝归属，不误显示成功。

## 验证记录

SDK 仅当前命令设置 `DOTNET_ROOT=E:\auto agent game\.dotnet` 并加到 PATH；未修改 global.json。结果留在本工作树 `artifacts/tests-f`。

```powershell
dotnet test tests/VIvian.Tests/VIvian.Tests.csproj -c Release --filter 'FullyQualifiedName~Execution|FullyQualifiedName~Processes|FullyQualifiedName~March7th|FullyQualifiedName~M5|FullyQualifiedName~MultiAccountOrchestrator|FullyQualifiedName~OneClickBatchSupport|FullyQualifiedName~DailyBatch' --results-directory artifacts/tests-f --logger 'trx;LogFileName=f-targeted.trx'
dotnet test tests/VIvian.Tests/VIvian.Tests.csproj -c Release --results-directory artifacts/tests-f --logger 'trx;LogFileName=f-default.trx'
dotnet test tests/VIvian.Tests/VIvian.Tests.csproj -c Release -p:M5DailyTestBuild=true --results-directory artifacts/tests-f --logger 'trx;LogFileName=f-m5.trx'
```

- 首次编译：新增同步抛异常测试被 xUnit 重载推断为 Task，编译失败；显式 Action 修正测试，不涉及产品问题。
- 针对性首轮：410/410，0 失败、0 跳过（包含两个新增租约测试；新增暂停负向测试在此轮编译后加入，见后续全量结果）。
- 默认首轮全量：1117/1117，0 失败、0 跳过；含全部三个新增测试，TRX：`artifacts/tests-f/f-default.trx`。
- M5 首轮全量：1117/1117，0 失败、0 跳过；TRX：`artifacts/tests-f/f-m5.trx`。
- 普通证据检查发现探针也用 FileShare.None 会掩盖产品错误放宽共享模式；已改探针为 ReadWrite。通过临时将产品 FileShare.None 改为 ReadWrite，实际观察新测试失败（Expected 23 / Actual 0），随后原字节恢复产品文件。预期失败记录：`artifacts/tests-f/f-lock-mutation.trx`；不是未修复的产品失败。
- 探针修正后默认/M5 全量：各 1117/1117，均 0 失败、0 跳过；最终 TRX 为 `f-default-final.trx` / `f-m5-final.trx`（均约 1 分 7 秒）。复现上述命令时相应替换 LogFileName 即可。
- 最终 `git diff --check` 通过；`git diff --exit-code 8f857cf -- src scripts` 通过，确认产品与脚本未改；`ce3ebee` 仍为 HEAD 祖先。
- `ce3ebee` OneDragon 结果回传修复：产品文件零变更，继承于 BASE；最终全量包含 OneDragon 测试。

## 审查与剩余验收

- DeepSeek：本聊天工具目录无 `deepseek_review`，状态 **blocked，0 轮**；未用其他模型冒充。另做普通只读证据检查，不计入 DeepSeek 轮次，不解除独立审查门槛。
- 普通检查唯一重要发现为探针共享模式，已通过上述错误注入失败及恢复后全量通过验证修正；无剩余已确认测试问题。
- 无产品改动，因此首批不重新部署、发布或启动 WPF 冒烟；总控的 T0 冒烟不是本分支新采集证据。
- 实机待总控串行安排：星铁单账号和 A→B 实际结果、暂停确认后的键鼠释放与恢复、人工切入聊天、真实宿主异常/强停/恢复；随后混合原神→星铁→绝区零队列及中途取消/版本失配/清理失败。
- 接收公共 v2 前保持 F-I-1/2/3 未完成；尤其聊天输入不能因本次离线暂停测试通过就放行。
- E 下一步：总控完成 F 合流安排后，在已分配 `feat/furina-zzz` 核验当前任务结果/skip/RunId 归属、实例配置与清理恢复，保留 ce3ebee 修复；先离线再预约实机。
- D 下一步：E 首批完成并由总控安排后，在 `feat/furina-genshin` 核验接管/归还、原生配置恢复与完整日常证据；不在 F 分支重写 BetterGI/OneDragon。
