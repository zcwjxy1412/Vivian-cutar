# F 执行内核首批交付

> 最新：已按总控授权进入 v3 多游持久协调器新批次，见文末。以下 v1“产品零修改”仅描述旧 F 证据提交，不代表当前实现。

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

## F v3 持久协调器新批次（2026-11-13）

- 原 F 证据提交之后依次快进 `85977de`、契约 `30aa4b5`、I 存储 `071e29e`。本次实际增量以 `071e29e` 为基线；没有修改 I 所有 Core/数据库/App 文件，也没有修改 C/B 文件。
- 对接说明 `docs/parallel/furina-f-plan-api.md`；前置审计 `furina-f-persistent-plan-seams.md`。采用已冻结 v3 契约，不另造执行器；宿主按 Game 分派既有 batch 委托，协调器自身不嵌套占用桌面锁。
- 实现 `PersistentExecutionPlanCoordinator`：原子接受后才串行执行；Existing/Conflict 不重放；接受 token 不支配执行；本地 StopAll 与宿主 token 停止已接受工作；失败/取消/清理或恢复不确认停止后续。所有持久失败使任务 fault，保留状态供宿主启动中断对账，绝不假写成功或继续。
- D/E 透传 childBatchId、启动前 await RunId 登记、stopOnFailure 与持桌面锁后的 validateConfirmed。原神保留原方法组重载；原神日志 QueueId 使用子批次；绝区零返回值等最终游戏清理后生成。MultiAccountOutcome 增加明确 Outcome/EngineCleanupConfirmed/ConfigurationRestored；新计划每项单账号，旧调用默认保持兼容。
- stopOnFailure 模式覆盖缺登录档案、非 fatal 切换失败、任务失败/未确认等旧的继续分支；普通旧入口默认 false。
- 复审时自查复现并补齐 WhenIdleAsync 等待正在接受的提交：旧行为在持久接受尚未结束时提前返回空闲，失败证据 `idle-red.log`。现在先等待提交串行门，再等待已接受任务。宿主关闭前仍需禁止新提交。

### 验证证据

全部在唯一临时目录和模拟监管组进行，不使用真实桌面共同锁，不启动游戏，不执行显示窗口的 smoke，不迁移正式数据库。

- 编译 RED：缺协调器/Outcome 字段，缺 D/E 接缝参数；分别 `artifacts/tests-f-plan/red.log`、`seams-red.log`。首次绿色前修正 xUnit 分析器规则与旧 Genshin 方法组兼容，非产品 RED。
- 行为 RED：停止与接受交错仍启动三项；最后一项忽略取消把计划记成功（`stop-race-red.log`）；调用者修改返回回执 Items 数组改变后续账号（`receipt-red.log`）；空闲漏等提交（`idle-red.log`）。分别通过停止代次/结果取消检查、执行项快照和等待提交门修复。
- 复审 L1 进一步验证：一个取消回调抛异常会中止 StopAll 枚举，后续已接受计划仍执行；真实临时 SQLite 回归原先实际调用 4 次而非 1 次（`callback-red.log`）。现逐个取消并保证调用本地停止，最后统一抛 AggregateException，不吞诊断。
- 首批针对性 56/56；默认全量曾1194/1194，合入 I store 后1207/1207；最终复审输入之前默认/M5各1208/1208。上述旧计数只证明各阶段代码。
- 最后两项修正后全量默认/M5各 **1209/1209**，0 失败、0 跳过，日志 `post-review-default.log`、`post-review-m5.log`，TRX 同名。
- 真实临时 SQLite 验证：RunId 在委托工作前可被新 LocalDatabase 实例精确读回；顺序子批次完成、重开返回已有回执、未知ID不回退、取消回调异常仍停止另一已接受计划。fake store 测试不冒充数据库事务/幂等实现证据。

### 本次独立审查（两轮上限已用完）

模型 deepseek-v4-pro / thinking disabled。首审 `artifacts/deepseek-f-plan/response-1.json` SHA256 `E11ACC6DD3DDBF35365A33A5538F37150E104FFAA240A1C39C4E69048A75DF2B`；复审 `response-2.json` SHA256 `CF09890A53FA788435623096B06DC58859AD2D08859CF0FBE0D2F21C4140F556`。一次准备复审的 shell 退出且请求/响应均未生成，随后保存请求并实际调用获得上述复审；不将无输出当有效审查。

- 首审 High“outcome 在 finally 未赋值返回”驳回：return 在 finally 之外，异常传播时不会走到 return，真实编译全量通过。首审 High“登记失败必须写终态”与 I 明确 fail-closed 约束冲突，拒绝；持久故障保留 Running、由宿主观察 fault，下次启动标 Interrupted，不重放。
- 首审 Medium 重建协调器会丢失内存阻断：这是宿主集成边界，仍须依赖既有恢复/桌面 dirty lease 与准入检查；新实例不能视为清理证明。I 必须在单实例启动时中断对账，实际 batch 保留原锁；本分支没有也不绕过该锁。
- 复审 H1 确认 return 控制流安全，转而指出 finally.StopGameAsync 可能覆盖旧异常；该行为存在于本次基线，属于已有 E 诊断一致性限制，本轮不扩范围修改，继续披露。
- 复审 M1 返回值仍暴露 store 返回数组：生产 LocalDatabase 回执是独立读取/反序列化对象，修改返回数组不会改 SQLite；执行工作已复制数组并有 RED→GREEN。不能采纳把同一个执行快照直接返回的建议，那会重新暴露执行项。无确认的生产存储别名漏洞。
- 复审 L1 取消回调抛异常：确认其会漏停排队计划，已修复并做真实临时库故障回归；并非“没有注册回调”，宿主 runner 可以注册。
- 其余参数默认、先登记后 Starting、登记早于恢复标记均是既定契约或保守语义，不改旧 D/E 已交付任务，也不重复审 I 存储任务。
- **最终状态：两轮已用完，最终 WhenIdle/StopAll 修正仅本地验证，没有第三轮独立复审；不得标记本批次最终代码“独立审查全部通过”。** 没有将本地测试成功等同实机放行。

### 交接约束

I 可按已落地签名连接；validateConfirmed 的持锁调用位置通过源码/编译核查，未启动真实 D/E 公共入口。星铁 RunId 与配置前置登记由 I 修改旧 scheduler/宿主，不在此分支。OneDragon 资源文件与迁移回退建议详见对接说明，未读取正式账号配置、未更改 SourceProfile.Revision 语义。E 旧启动诊断遮蔽/可能悬挂 journal、D-2 Finished 语义残余仍开放。

