# C 多游持久计划消费规格与落地

## v3 落地记录

I 已冻结 `30aa4b5` 的 `IExecutionPlanService`，C 产品提交 `b9b290a` 消费该接口；下文签名意图为冻结前记录，以 `PersistentExecutionPlan.cs` 和 contracts-v3.md 为准。实际 Prepare 请求仅有序 Game/AccountId，不由模型指定任务/资源版本。ViewModel/Tools 可选注入同一服务，旧路径不变。

可执行用例位于 `tests/VIvian.Tests/Engines/AssistantPersistentPlanTests.cs`，覆盖顺序、边界、非法嵌套字段、准备快照、完整轮次发布、精确 ID 查询/未知/中断、开关、确认前后失效、双击、类型化提交、查回提示和无聊天取消 token。旧路径继续由 AssistantTests/AssistantExclusiveActionTests 覆盖。默认全量1200/1200（数据库实现合入前）。

界限：同 ID 同摘要的实际持久去重/异载荷冲突、重启中断以及失败后不启动下一项由 I/F 负责，C 目前模拟消费这些返回值；此处测试不能代替 I/F 实际集成或游戏验收。C 没有新增第九工具、公共类型、数据库迁移或执行后台任务。

基线 `85977de4b8e0742e232574501dff96abddc5903c`。本文件是合成测试规格和消费需求，不是已实现接口；类型和方法名以 I 发布提交为准。I 拥有 Core/数据库迁移/宿主注册及 MainViewModel.ExecutionPlans.cs，F 拥有持久协调器；C 不另建同义公共类型。

## 最小调用与数据

|用途|最低需求|
|---|---|
|准备|PrepareAsync(有序请求项, ct) 返回宿主核实的待确认计划或类型化拒绝；请求只含目标/任务/资源策略，名称和配置版本由宿主读取，不能信任模型填写|
|计划|PlanId、ExpiresAt、FailurePolicy（首版失败即停）、有序 Items；每项 Ordinal、Game/AccountId、AccountName、TaskId、ConfigurationRevision、TaskPlanRevision、ResourcePolicy、ResourceSummary|
|提交|SubmitConfirmedAsync(冻结计划, ct) 返回 Accepted/Existing/Rejected/Conflict、Receipt（接受/已有时）、ReasonCode；不能仅返回字符串|
|查询|ReadAsync(PlanId, ct) 返回 PlanReceipt?；精确未知为 null，无最近记录兜底|
|回执|PlanId、计划状态、有序项及 ChildBatchId、实际 RunIds/QueueId（可空）、Outcome、Cleanup、ConfigurationRestored、ReasonCode；接受不是执行成功，未启动无 RunId|

同 PlanId 同载荷重确认回同一回执；同 PlanId 改目标/顺序/版本/资源等必须冲突。进程中断不自动恢复执行，查询不触发任何启动。聊天请求取消只影响模型调用/只读刷新，已接受计划归宿主管理，不能把 _request.Token 交给计划执行生命周期。

当前 MainViewModel 的 `_canApply` 仅允许所选单星铁账号，它继续服务旧配置与单星铁入口；新多游计划的完整目标/版本/运行准入由新宿主服务核对，不能对多项机械调用旧回调。C 不复制三游准备或启动器逻辑。

## 工具与卡片兼容

- 仍复用八工具：扩展 propose_run 的有序 items 形态，旧单星铁 game/account_id/revision/task_id/resource_policy 输入保留；两形态混用明确拒绝，不猜优先级。
- 未接入新服务时保留原单星铁行为，多项请求明确不可用。不得因为模型生成三游任务就打开执行开关。
- 保留 AssistantRunPlan、原构造器和委托调用兼容；新服务可选注入。旧 action_id/batch_id 语义与新 PlanId 的映射由 I 冻结，不能擅自令旧查询查到父计划。
- get_batch_progress 增加 plan_id，与 batch_id 互斥；两者均严格 D 格式非空 GUID。无参数仍是最近批次概览，不能把概览当某计划回执。plan_id 未找到时显示未知，既不回退也不重发。
- 多游卡复用 Title/TargetText/Lines/State/ConfirmCommand/CanConfirm 的视图绑定，TargetText 为“有序计划 N 项”，Lines 逐项展示游戏、账号、任务、资源、版本和失败策略。不得把第一账号 Target 当成整个计划；增加专用卡片构造/多目标字段时由 C 所有，保持旧单目标构造不变。
- 用户确认后先检查提案有效期与本地 confirmation revision，触发同步 ExclusiveActionStarting，依宿主类型化结果显示已接受/已存在/拒绝/冲突；不把纯文本当作提交成功。查询失败不把当前回执改为成功，不恢复确认按钮自动重试。
- 第一版精确重查可以通过现有查询工具完成；若新增卡片刷新按钮需先登记 B 绑定，不把刷新挂到确认按钮复用成再次提交。

## 合成消费用例（契约到达后转为可执行测试）

所有账号用固定合成 GUID，数据临时数据库；HTTP 只用脚本模型，宿主准备/提交/查询用冻结接口的测试实现。场景不启动游戏，不使用正式设置。

|ID|输入/操作|必须观察到的结果|
|---|---|---|
|C-P01|旧五字段星铁请求，未注入新服务|保持既有计划/委托路径及资源策略检查；无新增父计划伪回执|
|C-P02|按 zzz→starrail→genshin 提案|宿主按同一顺序准备；卡片与确认正文顺序一致；无排序、合并或自动删项|
|C-P03|空 items、超上限、未知字段、非法 GUID、无白名单任务、旧新形态混用|结构错误在工具边界拒绝，业务准入失败使用宿主原因；不生成可执行卡片|
|C-P04|宿主返回名称/版本与模型描述不同|展示宿主核实值；账号或任务身份不匹配的准备结果拒绝发布|
|C-P05|用户拒绝确认|无 ExclusiveActionStarting、无 Submit 调用，输入许可不释放|
|C-P06|确认弹窗期间失效/过期|拒绝提交，不用新版本替换旧计划后继续；确认 revision 失效不触发释放|
|C-P07|同卡双击、同 PlanId 同载荷重复提交|卡片防双击；宿主 Existing 回同 PlanId/子项，不开始第二份业务|
|C-P08|同 PlanId 目标/顺序/配置版本/资源改变|Conflict；不覆盖已存回执，不自动生成新 PlanId 重试|
|C-P09|提交已接受后关闭聊天/CancelRequest|模型请求结束，宿主执行 token 不取消；只读查询仍可找到接受记录|
|C-P10|接受响应丢失/等待取消|保留原 PlanId，仅可按 ID 重查；不判定未提交，不自动换 ID 提交|
|C-P11|精确 PlanId 查询、已有更近其他计划|只返回匹配计划；未知/null/空/格式错误/与 batch_id 同时给出明确拒绝，无概览兜底|
|C-P12|持久化后程序重开，状态 Interrupted|展示中断及恢复原因，查询零启动；不自动 Resume/Retry|
|C-P13|项一成功、项二失败/取消/清理未知/配置恢复失败|展示各项独立结果，后续未启动不能显示成功；计划总体不报全部完成|
|C-P14|Accepted 尚无 RunId、运行中、终态 Outcome.Success 但 Cleanup.Unconfirmed|分别显示已接受/实际阶段/清理未确认；不用百分比或成功消息替代状态|
|C-P15|模型自称任务完成但只返回提案或宿主拒绝|卡片保持待确认/拒绝，结构化回执不受自然语言覆盖|
|C-P16|事件订阅释放失败|不提交、原命令显示错误；不能重启模型或继续启动补救|
|C-P17|新服务缺失，查询 plan_id|明确不可用；不偷偷使用旧 batch API 把两个 ID 域当同义|

## 错误呈现建议

Malformed/Unsupported → 参数或能力不可用；Stale/Expired → 重新读取并生成提案；Rejected → 提交未接受并显示安全原因；Conflict → ID 已绑定另一份计划，禁止覆盖；NotFound → 尚无法确认该 ID，禁止自动重发；Interrupted → 已中断待核对；Transport/Timeout → 提交状态待查回，禁止推定未接受；Existing → 展示原回执，禁止再次启动。

这组语义不要求 I 原样采用英文枚举名，但应有稳定可区分的结果，不能要求 C 解析中文异常字符串。失败详情必须是脱敏 reason code/安全摘要，不向模型转发原始路径、密钥或账号登录信息。
