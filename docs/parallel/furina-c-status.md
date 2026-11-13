# C / AI 助手首批交付

- 日期：2026-11-13；任务 #63，总任务 #59。
- 工作树：`C:\Users\胡桃\.codex\worktrees\furina-vnext-ai\Vivian-source`；分支 `feat/furina-ai-agent`。
- SOURCE_BASE_SHA：`ce3ebeea79c507825e66a9e97097a6f80ede33cc`；依赖公共 T0 / 分支 BASE：`8f857cf2cd16515d81f4c65f8e50b693374dccd4`。
- 产品 HEAD：`70661de3d10d7437a595b4a9d37e0a881c6ce6c2`；本状态文件为其后的文档提交，无后续产品变更。
- 独立审查：**blocked / 0 轮**。本聊天工具目录没有 `deepseek_review`，没有调用替代模型冒充 DeepSeek。

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
