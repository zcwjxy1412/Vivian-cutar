# C / AI 助手首批交付

## 第二批：消费公共 v2（2026-11-13）

- 已按总控指令 fast-forward 合入 `d8fd96b1a3a6b230c14dcc3ad53fdb18eca5b94b`（包含公共 `f49d9e3`）；本批 diff 以 d8fd96b 为基线。下文首批记录为历史，首批 #69 已由总控合流；总控报告其 DeepSeek 首审无 blocker，本聊天不重复审查首批。
- 新增 `AssistantViewModel.ExclusiveActionStarting` 同步 `Action` 事件。配置/运行均在用户确认后、卡片禁用后、获取写锁或提交前触发；取消确认或确认期间 revision 失效不触发。事件异常向原命令错误处理传播并阻断后续动作。无订阅时仍保留原准入、revision、桌面锁和去重路径。B 是预期唯一订阅者，必须同步禁用全部可编辑输入并 Dispose 输入许可，不使用 async void 或延后释放；订阅/解绑归 B。事件触发后若操作失败，仍保持输入关闭，按 v2 重新显式申请许可，不能自动恢复输入。
- 为无窗口验证，公共构造器维持原签名和 MessageBox 默认 No 行为，增加仅程序集可见的确认函数构造器重载；两条确认方法改为 internal 供真实 ViewModel 路径测试。未绕过生产人工确认，未新增公开确认开关。
- `get_batch_progress` 新增可选 `batch_id`：严格非空 D 格式 GUID，用 v2 `LoadBatchAsync` 精确查询；未知明确报错且提示不可据此自动重发。省略仍返回最近五批次；结果仍为数组，精确查询最多一项，运行中尚无结果保持空列表。继续复用既有批次结果与 RunId，非多游父计划，不扩展执行能力。
- 补充显式 JSON null / stop 结束原因的命名兼容用例，没有修改首批客户端产品代码。
- 测试：精确查询在实现前 RED；事件初次构建因尚未调用而报 CS0067，随后另做运行顺序负向回归（移除运行事件调用时 Assert.False 失败），恢复调用后通过。助手针对性初轮 49/49；新增两个兼容用例后，默认全量 **1149/1149，0 失败/跳过，退出码 0**。记录位于 `artifacts/tests-c-v2/{red-batch,red-event-order,green,full}.trx`。数据独立临时目录，未启动窗口或游戏。
- 验证命令：当前进程设置已有 DOTNET_ROOT/PATH 后，`dotnet test tests/VIvian.Tests/VIvian.Tests.csproj -c Release --results-directory artifacts/tests-c-v2 --logger 'trx;LogFileName=full.trx'`；`git diff --check` 通过。
- 第二批 DeepSeek：通过总控提供的本机 launcher 调用真实 `deepseek_review`（deepseek-v4-pro，thinking disabled），仅发送本批 diff、新测试和必要上下文、v2 契约及脱敏验证情况。**首审 + 修复后复审共 2 轮完成，复审无剩余 blocker、无 critical/high**；请求/结果为 `artifacts/tests-c-v2/review-{request,response}{,-2}.json`，不含真实凭据，本聊天未读取 DPAPI 密钥。
- 首审核实：意见 2（确认期间失效却先释放输入）成立；配置/运行两条 RED 用例复现后增加事件前 revision 检查。意见 1（事件异常无人处理）不成立，现有 `Mvvm.AsyncCommand` catch 调用 `ProposalCard` 错误回调并写 State，新增两条真实命令测试验证；意见 3（batch_id:null 回退）不成立，现有 schema MatchesType 在进入查询前拒绝 null/数字，新增断言验证。意见 4 为单订阅者契约注意项：任何异常都阻断后续动作，不为了继续调用其他处理器而忽略释放失败。
- 修复后最终默认全量 **1153/1153，0 失败/跳过，退出码 0**，`artifacts/tests-c-v2/full-reviewed.trx`；确认失效 RED 记录为 `red-review.trx`。复审已发送完整本批最新 diff 和上述代码证据、测试结果。没有重复首批产品审查。
- 复审保留低优先级建议：配置路径在事件之后、申请桌面锁之前额外检查一次 revision。当前 `ApplyConfirmedAsync` 的 stillAllowed 回调已经核对捕获 revision，且事件订阅契约仅禁用输入/释放许可；无已确认的越权写入缺陷，本批不增加重复检查。代价是异常订阅者若使提案失效，配置路径仍可能先获取桌面锁再由写入准入拒绝；不会因此允许写入。B/I 实际接线仍需集成验证，复审不证明实机焦点行为。
- 未验证：B 窗口实际订阅后的焦点/许可释放实机、真人确认窗口、真实模型端点联调、游戏执行及 M5 本批全量。原正式安装/账号/上游配置未操作。下一步由 B/I 集成订阅后串行验收；C 不扩展公共层或多游父表。

- 日期：2026-11-13；任务 #63，总任务 #59。
- 工作树：`C:\Users\胡桃\.codex\worktrees\furina-vnext-ai\Vivian-source`；分支 `feat/furina-ai-agent`。
- SOURCE_BASE_SHA：`ce3ebeea79c507825e66a9e97097a6f80ede33cc`；依赖公共 T0 / 分支 BASE：`8f857cf2cd16515d81f4c65f8e50b693374dccd4`。
- 产品 HEAD：`70661de3d10d7437a595b4a9d37e0a881c6ce6c2`；本状态文件为其后的文档提交，无后续产品变更。
- 独立审查：C 本聊天 **blocked / 0 轮**。总控随后通知正在接通真实 DeepSeek 审查，由总控独占处理；C 不重复发起，不接收凭据，最终审查结果以总控证据为准。

## 审计事实与裁定

1. 已有八个工具（七个默认 + 一个受开关限制）：`list_games`、`list_tasks`、`read_configuration`、`propose_changes`、`get_run_status`、`list_accounts`、`get_batch_progress`、`propose_run`。工具参数检查对象、字段、枚举、类型、大小和必填项；没有模型可直接调用的任意写入、启动、停止或 shell 工具。
2. `ChatCompletionsClient` 已使用所填端点/模型/密钥执行真实聊天测试，并另做带随机验证码的工具往返测试；HTTP 状态错误脱敏，测试有 20 秒时限。生产 HttpClient 禁止重定向，限制缓冲大小。凭据仍仅沿用 DPAPI `AssistantSettingsStore`。
3. `AssistantViewModel` 已区分未配置、未测试、测试中、测试成功/失败；鉴权、超时等详细原因在 SettingsStatus；参数修改递增 revision 并取消旧请求，旧测试结果不能覆盖新参数。成功文案是测试成功，不宣称持续在线。未重做这些功能，也未新增 B 的绑定要求。
4. `AssistantSession` 已限制轮次/调用数/参数量、拒绝重复调用 ID、关联 tool_call_id、有工具和整轮时限；只有完整成功轮次才提交历史与提案。取消聊天不调用游戏停止。
5. 配置已按 game/account_id/revision 定位；`ApplyConfirmedAsync` 单次消费提案，宿主确认与桌面租约后调用提供者，星铁提供者核对 revision、运行准入、SQLite 审计和事务内读回。原测试已经验证账号 B 不变、上游配置不变、重复确认被拒绝、版本冲突和提交条件变化回滚。裁定：不再增加一套写入/回读实现；新增真实工具回读断言验证模型读到的目标、revision 和新值。
6. `propose_run` 仅为 M5 开关下的单星铁账号 routine，已有 ActionId 和数据库去重，但提交委托返回最终文案，不能据此宣称多游计划或结构化回执已完成。

## 本次产品增量

- 根因：兼容客户端取 message 后忽略显式 finish_reason，导致 length/content_filter 等不完整回复仍可通过测试，甚至完成提案轮次。
- 修复：显式结束原因仅接受 stop/tool_calls；声明 tool_calls 必须带非空调用数组；未提供工具定义时拒绝额外工具调用。错误为固定脱敏文案。
- 兼容裁定：保留既有端点省略或返回 null finish_reason 的兼容性。代价：这些端点仍只能依靠正文/调用结构判断完整性；显式未知结束原因拒绝使用。
- 未改任何公共 DTO、项目文件、App.xaml、App.xaml.cs、数据库、其他分支界面或正式安装。

## 本地证据

所有命令只在本工作树运行，使用已有 SDK；数据为测试自行创建的独立临时目录。无正式账号库、配置或凭据读取，无真实游戏和前台窗口操作。

```powershell
$env:DOTNET_ROOT='E:\auto agent game\.dotnet'
$env:PATH="$env:DOTNET_ROOT;$env:PATH"
dotnet test tests/VIvian.Tests/VIvian.Tests.csproj -c Release --filter FullyQualifiedName~Assistant --logger 'trx;LogFileName=c-green.trx' --results-directory artifacts/tests-c
dotnet test tests/VIvian.Tests/VIvian.Tests.csproj -c Release --logger 'trx;LogFileName=c-final-full.trx' --results-directory artifacts/tests-c
```

- RED：四个连接测试均以“应抛异常但没有异常”失败，`artifacts/tests-c/c-red.trx`。
- 初次 GREEN：助手针对性 36/36。增加 Session 截断调用测试后，首轮默认全量 1120/1120。
- 补充代码审查（非 DeepSeek）发现显式 tool_calls 却没有调用内容的缺口；新增测试复现 1 失败 / 3 通过，`c-review-red.trx`，随后修复。正常工具往返测试使用显式 tool_calls/stop；原用例仍覆盖缺省结束原因。
- 一次过早启动补测与本工作树全量测试发生产物占用，构建 MSB3027 失败；等全量退出后串行重跑，成功复现上述 RED。未终止其他聊天进程。
- 最终默认全量：**1121/1121，通过；失败 0，跳过 0，退出码 0**，`artifacts/tests-c/c-final-full.trx`。本分支没有重跑 M5 全量；不引用总控测试作为本次证据。`git diff --check` 通过。

## 交总控的最小公共需求（仅建议，不是已发布契约）

1. **多游计划与幂等提交**：建议在 `Core/Execution` 由 I 冻结 `ConfirmedPlan(PlanId, ConfirmationRevision, Items, FailurePolicy)`，每项含现有 `AccountConfigurationTarget`、TaskId、ConfigurationRevision、TaskPlanRevision、ResourcePolicy；`SubmitConfirmedPlanAsync(plan, ct)` 返回 `PlanSubmission(PlanId, QueueId, Accepted, ReasonCode)`。沿用现有 ActionId 作为 PlanId，不再另造随机重试键；同 ID 同载荷返回同一回执，同 ID 不同载荷拒绝。C 将替换当前 `Func<AssistantRunPlan, Task<string>>`，B 仍消费现有卡片命令。失败用例：重复确认、请求取消但已接收、重启后重复提交、配置版本变化、混合游戏中间失败/清理未确认。
2. **回执关联**：在现有 `RunContext`/`RunUpdate`/`RunRecord` 与事件持久化路径补充可空 PlanId、AccountId、ConfigurationRevision（旧数据保留 null），事件投影带 RunId 与序号。建议增加按 PlanId/RunId 读取的 `ReadPlanRunsAsync`，C 的 get_run_status 只返回真实关联，不把“最近一次”猜成当前请求。调用用例：提交后立即取得 QueueId，轮询/订阅该计划实际运行；未知/未启动保持未知。失败用例：同账号旧成功日志、其他账号结果、终态前清理失败、事件乱序、旧库缺关联。
3. **安全输入**：B 当前 `AssistantWindow` Loaded 会聚焦 InputBox，C 当前没有安全失焦的准入接口。建议 F/I 提供 `AcquireAssistantInputAsync(ct)` 返回可释放的输入租约（只在空闲或本次引擎已确认安全失焦/暂停时授予），持有期间阻止新任务抢占；B 先获准再启用/聚焦，释放租约后按明确的宿主策略继续。暂不把“已发送暂停请求”当许可。失败用例：暂停超时/不支持、授予前切换运行、窗口关闭、取消请求、另一宿主占用桌面。该能力未发布前，运行中输入需要由 B/F 总控统一封闭；C 不越权修改窗口或调度器。

## 未验证及下一步

- 未提供用于本分支的有效模型配置；真实端点聊天、鉴权、工具往返和真人确认 UI 未联调，模拟 HTTP 不能替代此验收。
- 未运行 WPF 冒烟/焦点/DPI 实测、未启动游戏、未部署。总控需串行安排：空闲时真实连接测试与取消/改字段；隔离账号提案确认后通过工具回读；游戏占用桌面时打开聊天必须先通过安全输入契约；真实 PlanId→RunId→终态/清理回执。
- 本首批为协议可靠性增量与现有配置闭环审计，不标记整个 AI 工作项或三游闭环完成。等待 I 发布公共契约 v2 后消费，DeepSeek 工具恢复后用实际脱敏 diff 首审。

## 追加只读审计：ActionId、确认调用链与 v2 最小接入

核对版本为产品 `70661de` / 状态提交 `0bf73e6`；本次仅更新本文档，不修改待审产品代码，也不假定集成分支已有任何新接口。草稿 PR：#69。

### 可直接复用的链路

- `AssistantCore.ProposeRunAsync` 创建 `AssistantRunPlan.Id`，工具输出 `action_id`；同一卡片确认使用同一个对象和 ID。模型重新生成计划会产生新 ID，因此当前保证不覆盖“不同计划 ID 的语义重复请求”。
- `AssistantViewModel.ConfirmRunAsync` 检查过期、账号准入、人工确认与 confirmation revision，禁用卡片，再调用 `_submitPlan(plan)`；构造器仍是 `Func<AssistantRunPlan, Task<string>>`。`CancelRequest()` 仅取消聊天请求，与该委托无关。保留这条隔离关系。
- `MainViewModel` 构造 Assistant 时传入 `SubmitAssistantPlanAsync`；该方法通过 `RunAsync` 等待 `RunAccountBatchAsync(GameId.StarRail, plan)`，最后返回 `BatchStatus` 文案，**不是立即接受回执，也不返回 QueueId**。
- `RunStarRailBatchAsync` 在桌面租约内再次读取配置和账号任务版本，核对开关、选中账号、有效期、安装路径和分解策略，冻结账号设置，然后把 `approved?.Id` 传给 `MultiAccountOrchestrator.ExecuteAsync(... actionId)`。这些准入和设置快照应保留，不能用 C 的卡片检查替代。
- `MultiAccountOrchestrator.ExecuteAsync` 先校验同游戏、非空且不重复账号，再 `BeginBatchAsync`，之后才 `_host.BeginBatch()` 和桌面动作。`LocalDatabase.Accounts.cs` 的 `BeginBatchAsync` 令 `batch.Id = actionId ?? Guid.NewGuid()`；有 actionId 时 `INSERT ... ON CONFLICT(id) DO NOTHING`，受主键保护，插入数不是 1 就抛异常。它是持久化“最多创建一次”，不是“重复提交返回同一结果”。
- 无单独 action_id 列或载荷摘要；普通 `SaveBatchAsync` 是更新入口，不可当作提交去重入口。现有 `AssistantTests.StarRailRunPlansAreOptInReadOnlyAndHaveASingleDurableActionId` 验证串行重复 ID 被拒绝；没有证明跨进程并发、同 ID 异载荷或接收后断连的回执恢复。
- `RecordAccountResultAsync` 保存 batch_id/account_id/run_id；但调用发生在 runner 返回后。`RunStore.ReadBoundRunsAsync` 依赖这条历史关联筛选账号，不能用于尚未结束的实时账号关联。`batch_execution_records.game` 是单游戏；不能直接用同一个父 PlanId 为三个游戏各建批次。

### 分两步落地，缩小公共变更

**第一步：单星铁结构化查回，无数据库迁移也能取得增量。** I 增加按精确 BatchId 读取批次的方法与按 BatchId 读取结果的类型化投影，C 用已有 plan.Id 精确查回。明确区分“已记录批次/已中断/尚未找到”，不能以查不到等于未提交，也不能查询最近 20 条后猜测。现有批次结果可提供结束后的 RunId。这个步骤不宣称支持同 ID 异载荷验证、立即接受、运行中 RunId 或多游计划；最小情况下确认委托继续等到结束，只把返回 string 换为类型化结果即可，不需要新建后台任务。

**第二步：多游与可重放接受回执，需要最小新增持久化。** 建议 I 在现有 v7 之后分配迁移编号（C 不预占 v8），增加父计划表与有序计划项表，不改造旧 batch 的单游戏含义：

- 父表 `execution_plans`：PlanId 主键、规范化载荷摘要、版本化的已确认计划 JSON、确认版本、接受时间、状态。保存用于核验和展示的内部 ID/策略，不保存凭据或登录资料；规范化摘要须覆盖项目顺序、游戏/账号/任务、两类 revision、资源策略、失败策略和有效期，不能散列原始 JSON 文本。
- 子表 `execution_plan_items`：父 PlanId + ordinal 联合主键、预先分配并持久化的唯一 ChildBatchId。目标和冻结配置来自父表不可变计划 JSON，避免两处重复存同一计划。首版可一项一个原有单游戏批次，保持原顺序，不为合并相同游戏而重排。
- **启动前运行关联**：若一项可能产生多个 RunId，增加 `execution_plan_runs(PlanId, ordinal, RunId UNIQUE)`；F/I 必须在分配 RunId 后、调用适配器/输入前写入关联。若 I 选择在 runs 上加可空 PlanId/ordinal，则这张关联表可省，二者不要同时建。账号与 revision 可由计划快照投影给 C，首批不必给所有 RunEvent 重复加字段。已有 run_events.id 可作为持久化游标投影，避免另建一套事件表。
- 原 `batch_execution_records`、`account_execution_history` 和 `runs` 继续承载执行与清理事实；旧记录不回填猜测关联，旧 schema 数据必须保持可读。父计划只是编排/接受状态，不复制或覆盖引擎成功结论。

**提交事务与恢复顺序：** 宿主验证确认与冻结载荷 → 同一事务插入父计划和 ChildBatchId → 返回接受回执 → 宿主持有独立于聊天的执行生命周期顺序启动子批次。相同 PlanId/相同摘要返回已存回执；不同摘要返回冲突；插入竞争后读取胜者并比较摘要。不要仅捕获 BeginBatchAsync 的任意 InvalidOperationException 当作重复接受。原 BeginBatchAsync 的 create-only 保护仍是每个子批次开始前的第二道保护。

提交取消/崩溃后，C 仅用同 PlanId 查询，不自动换 ID 重试。已经存在的子批次不能因“无账号结果”而再次启动；结合既有 `ReconcileInterruptedBatchesAsync` 和运行恢复，把状态保守显示为中断/待核对。父计划已接受但子批次未创建时，是否允许继续由宿主重新检查确认、有效期和准入；不得因数据库有记录就跳过授权检查。失败或清理/配置恢复未确认时停止后续项；安全清理门槛优先于任何继续策略。

### 建议 v2 的最小调用面与 C 下一批

以下为待 I 冻结的签名意图，不是本分支已定义的 DTO：

1. `SubmitConfirmedPlanAsync(ConfirmedPlan, CancellationToken) -> PlanSubmission`：至少 PlanId、接受状态、原因码及子项 BatchId。父计划先于各账号队列存在，不强求一个全局 QueueId；每个实际 RunId 的 QueueId 直接读现有 runs。
2. `ReadPlanAsync(PlanId, CancellationToken) -> PlanReceipt?`：返回不可变目标、子项/批次、真实 RunId/QueueId、阶段、Outcome、Cleanup、恢复状态；未启动 RunId 为 null，旧库无关联不推断。C 不解析 BatchStatus 字符串判断成功。
3. 确认/提交后的只读订阅或轮询必须属于宿主生命周期；关聊天只停止聊天/界面刷新，不能取消已接受计划。若采用立即接受，I/F 必须先提供受管理任务和异常处理，C 不自行 `Task.Run` 启动游戏。

待 v2 发布后，C 可独立实现的顺序：

- C1：用现有 ProposalCard 的 Id/Target/State/ConfirmCommand 消费类型化单账号回执；保留现有 B 绑定，提交成功仅显示“已接受”，RunOutcome/清理来自精确查询。新增模拟宿主测试：重复点击、接收后断连、未知计划、关闭聊天不中止执行、旧成功不得冒充本次。
- C2：在 `get_run_status` 增加严格验证的可选 plan_id/run_id，未知 ID 返回明确错误，不退回最近记录。只从 v2 投影读取账号/revision，模拟验证错账号、缺关联、运行中、终态但清理未确认。
- C3：公共有序计划 DTO 和准入提供者就绪后再扩展 `propose_run` 的参数和 LastRunPlans；模型仍只能生成待确认计划。先保持现有可执行任务白名单，缺能力的游戏返回不可用。多项卡片展示新增绑定先登记给 B，不假装现有单目标 TargetText 可完整表达混合计划。

仅文档追加的验证：源码调用链/SQL/现有测试逐项只读核对；不运行数据库迁移、不读取正式库、不重复运行上批 1121 项测试。不声称上述拟议接口已实现或已经过并发恢复测试。
