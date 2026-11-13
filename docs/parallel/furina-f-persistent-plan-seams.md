# F 多游持久计划接缝审计（待 I 冻结）

基线 `85977de`，分支 `feat/furina-runtime` 已快进到该提交，原 F 工作树干净。此文件是接口建议，尚未声明或实现新 Core 类型；没有游戏、桌面输入或窗口冒烟。

## 已有入口与缺口

| 游戏 | 现有入口 | 外部子批次 ID | 运行关联最早可写点 |
| --- | --- | --- | --- |
| 星铁 | App/MainViewModel.RunStarRailBatchAsync → MultiAccountOrchestrator.ExecuteAsync → RunAccountTasksAsync → UnifiedTaskControl.ExecuteTaskPlan → QueueScheduler | orchestrator 已接受 actionId，现由 approved?.Id 提供；首页无 approved 时随机 | QueueScheduler 内部生成 RunId，Starting 持久化在 StartAsync 前；需 I 提供可等待的关联接缝，UI Changed 事件不能作持久前置条件 |
| 原神 | BetterGiAccountBatch.ExecuteSavedDailyAsync → ExecuteSavedAsync → BetterGiNativeDaily.RunAsync | 未透传；orchestrator 生成批次，NativeDaily 又生成 RunId，RecordDailyAsync 目前用 RunId 作 QueueId | NativeDaily 生成 RunId 后、写 native recovery marker/MarkDirty/覆盖配置之前，await 关联回调；日志 QueueId 改为传入 ChildBatchId |
| 绝区零 | OneDragonAccountBatch.ExecuteAsync → RunAsync | ExecuteAsync 内 Guid.NewGuid，传入 orchestrator.actionId 和 RunContext.QueueId | RunAsync 创建 context 后、Starting 写入及 adapter.LaunchAsync 之前，await 关联回调 |

三游戏最终均返回 Infrastructure.Accounts.MultiAccountOutcome：BatchId、Completed、Succeeded、Failed、Detail、HostLaunches、HostCleanupConfirmed、GameCleanupConfirmed。它没有明确 Outcome、任务引擎 Cleanup、ConfigurationRestored。BatchTaskResult 有 RunId/Outcome/CleanupConfirmed/ConfigurationRestored，但仅执行结束后 RecordAccountResultAsync 才保存账号与 RunId。不能靠终态字符串猜测恢复状态。

## 提请 I 冻结的最小接缝

1. 保留旧调用兼容：BetterGiAccountBatch.ExecuteSavedDailyAsync 和 OneDragonAccountBatch.ExecuteAsync 尾部增加 `Guid? childBatchId = null, Func<Guid, Guid, CancellationToken, Task>? beforeRun = null`。回调参数固定为 accountId、runId，childBatchId 由闭包绑定。持久计划必须传非空 ID；旧入口 null 保持旧行为。沿内部调用传递，orchestrator 使用 actionId: childBatchId。
2. NativeDaily.RunAsync/RunCoreAsync 接受相同可选 childBatchId 和关联回调（其已有 accountId），回调失败不得启动引擎、写覆盖配置或清除任何旧恢复标记。RecordDailyAsync 新增可选 childBatchId，仅供日志 QueueId 使用。OneDragon.RunAsync 的回调由上层捕获 accountId。
3. 星铁由 I 修改 App/UnifiedTaskControl/Core：把 ChildBatchId 与当前 AccountId 带到执行入口；在已选定 RunId、任何引擎启动之前等待关联持久化成功。建议 QueueScheduler.RunAsync 尾部可选 `Func<RunContext, CancellationToken, Task>? beforeRun = null`；若 I 选择 journal 装饰器，必须保证写入失败阻止启动且不能依靠异步 UI 事件。不同账号各自绑定闭包，QueueId 不必等于 ChildBatchId，映射表显式关联即可。
4. 返回值需补足：建议在现有 MultiAccountOutcome 尾部增加 `RunOutcome? Outcome = null, bool EngineCleanupConfirmed = false, bool ConfigurationRestored = false`（名称与归属由 I 确认）；协调器只放行 Completed && Failed==0 && Outcome==Success && 三类 cleanup 均确认 && ConfigurationRestored。默认 false 防止旧调用冒充已确认。初始化未触碰状态可明确记录 NotRequired，不从异常文本推导。
5. 现有账号编排会继续部分失败（缺登录档案、非 fatal 切换失败、星铁 Failed、绝区零 Indeterminate），且 Completed 可为 true 但 Failed>0。持久计划要求失败即停，建议 orchestrator 尾部增加 `bool stopOnFailure = false`，新持久入口传 true，并在上述分支停止；旧首页行为暂不无意改变。若要求所有入口统一失败即停，请 I 明确扩大范围。

## I 需提供的持久契约语义（类型未自创）

- 一个原子接受操作：PlanId + 规范摘要 + 冻结的有序子批次与账号/配置版本；同 ID 同摘要返回已有回执，同 ID 异摘要冲突。子 ID 在任何执行前保存。不能复用 BeginBatchAsync 的 createOnly 当作接受幂等：它对重复 ID 抛异常，且没有载荷摘要。
- 精确查询、开始子批次、运行关联、子批次终态、计划终态、启动中断对账；各写入要求条件更新或等价事务保证。计划接受不预创建 running 的 batch_execution_records，否则既有 BeginBatchAsync(actionId) 会冲突。
- 进程启动时将此前 Accepted/Running 等未完成计划标 Interrupted（不自动重放）；同 ID 再次提交仍查回原回执，不重新执行。重启时机由 I 宿主启动调用，不在每次提交时对账。
- 接受与调度提交 token 分离：接受前可取消，接受成功后由宿主存活期管理执行任务，AI 面板取消/关闭不得取消它。显式本地停止才取消执行；星铁除 token 外仍调用既有本地 StopAll/调度器停止通道。

## F 协调器实现边界与验证准备

复用 I 提供的持久仓储与宿主提供的三游戏 batch 委托；不自建引擎。单实例协调器串行调度，持久接受先于任何委托。每个现有 batch 自己持有 DesktopOperationLease，协调器不外层嵌套占同一桌面锁；实例/计划互斥不能替代桌面锁。

待冻结后测试：同 ID 并发同摘要一次执行、异摘要零执行；接受落盘失败零启动；关联落盘失败零启动；重启中断无重放；有序运行；失败/取消/清理未确认/恢复未确认停止余下；AI token 取消不停止已接受任务；本地停止生效；异常与持久终态失败不继续后续。全部使用唯一临时目录与模拟 batch，不显示窗口。

暂不改 D/E 已披露的旧诊断问题，避免混淆旧任务审查；本轮只补持久协调必需的 ID、关联与终态接缝。实现后按 AGENTS 单独首审/必要复审最多两轮。
