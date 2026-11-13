# 公共契约 v3：多游持久计划

基线 85977de。公共类型位于 Core/Execution/PersistentExecutionPlan.cs，数据库由 I 实现 IExecutionPlanStore。所有入口复用现有批次/适配器，不新建游戏执行体系。

- C 使用可选 IExecutionPlanService；PrepareAsync 只接收按序 Game/AccountId，宿主读取真实名称、任务、配置版本和资源摘要；不能信任模型提供的版本或摘要。仅支持 routine / saved_account_settings。旧单星铁工具与构造保持兼容；旧单星铁路径仍使用原 actionId，不能将旧批次 ID 静默解释成新父计划 ID。
- 新计划每项一个账号、一个预先生成并持久化的 ChildBatchId。有序 Items 最大 32 项，同一游戏账号不得重复。所有新计划固定失败即停。PlanId 与完整冻结载荷摘要幂等：相同回原回执，不同 Conflict；过期且未接受的计划 Rejected。已经接受的过期计划仍查回原回执。
- IExecutionPlanStore 接受先落盘，TryStart 原子独占；StartItem 只能启动下一项且此前所有项成功、清理确认、配置恢复。RegisterRun 必须在副作用前 await；RunId 精确且不可跨项复用。CompleteItem 记录 Outcome/Cleanup/ConfigurationRestored，CompleteAsync 仅 Succeeded 或 Stopped，停止时跳过剩余项。启动对账把未完计划改 Interrupted，绝不自动重放。
- F 实现协调器，接收 IExecutionPlanStore 与三游批次委托；接受 token 与宿主执行 token 分离，聊天关闭/取消不能取消已接受计划。协调器不得嵌套占用已有桌面锁；本地 StopAll 继续使用现有停止通道。任何持久化失败禁止启动后续项。
- F 可向 D/E 现有批次入口尾部追加可选 childBatchId、beforeRun(accountId,runId,ct)、stopOnFailure；旧调用默认保持兼容。NativeDaily 在恢复标记/临时覆盖前 await beforeRun，日志 QueueId 使用 ChildBatchId；ZZZ 在 Starting/Launch 前 await。MultiAccountOutcome 可追加 Outcome、EngineCleanupConfirmed、ConfigurationRestored，默认未知/false，不凭文本推断。I 负责星铁宿主/调度器同等接缝。
- 正式数据不迁移、不替换安装；I 在隔离副本验证 schema 7→8、备份和既有档案兼容。UI 主机公共连接由 I 的 MainViewModel.ExecutionPlans.cs 完成，B 不改提交/停止主机逻辑。

验证：并发同 ID 一次接受、异载荷冲突；顺序与配置版本；写失败零启动；精确未知查询；崩溃中断无重试；取消、清理/恢复未确认阻断；旧单星铁路径兼容。当前只冻结契约，不代表实现或实机验收完成。
