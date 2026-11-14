# 已接受计划的启动前拒绝分类

基线 `6129b8e1e47a983662cf361a7078a88ca74e127b`，包含先前时间戳修复。分支 `fix/plan-preflight-classification`。这是独立任务，未修改 MainViewModel.cs、窗口、正式数据、历史回执或恢复标记，未启动游戏或部署。

## 契约

- Core 增加 `ExecutionPlanPreflightException : InvalidOperationException`，仅表达已知的条件／确认版本拒绝，不自带“清理已确认”的含义。
- `PersistentExecutionPlanCoordinator` 构造函数末尾增加可选 `Func<ExecutionPlanItemReceipt, CancellationToken, Task>? validateBeforeExecution`。它只能只读检查，不得启动适配器、操作桌面或写配置。协调器在调用 executeBatch 之前单独执行它；不向它提供 beforeRun 回调。
- 仅在该只读回调中捕获到上述专用异常时，记录 `Failed / NotRequired / ConfigurationRestored=true / preflight_error`。当前项目为 Failed，计划 Stopped，后续项目 Skipped。不会设置协调器 `_blocked`，新确认的新计划可以执行；旧 PlanId 仍返回 Existing，绝不重放。
- 回调里的未知异常仍为 `adapter_error / Unconfirmed / ConfigurationRestored=false`。进入 executeBatch 后，无论是否已调用 beforeRun、是否有 RunId，即使抛同一个专用异常，也保持未知清理并阻断后续计划。
- 存储故障、运行登记故障的原有抛错／启动中断机制未改；不会在存储已失败时补写虚假的安全终态。

## App 接线

MainViewModel.ExecutionPlans.cs 将 RunPersistentItemAsync 开头的首轮 ValidatePersistentItemAsync 提取为上述协调器只读回调，保持在适配器前执行。已知运行条件不满足或读取结果与确认项目不相等，抛专用异常；读取过程其他异常不一概转为安全拒绝。适配器持有桌面锁后的后续 Validate 调用仍存在，属于执行阶段，不能判定无需清理。

首页与 AI 接受计划后的回执展示共用 PersistentPlanCompletionText。只有明确 preflight_error、失败项没有 RunId 且 NotRequired/restored=true，并且没有其他清理／恢复未知项时，提示：

> 条件或配置已变化，受影响项目尚未启动。请检查账号与任务配置，重新确认后再开始。

真正未知清理仍调用既有 BlockPersistentPlans。此补丁不清除任何已存在的会话故障、恢复记录或历史失败，不改变接受之前 SubmitConfirmedAsync 的既有校验／异常返回机制。

## 验证

- 修复前（新契约签名但尚未接入回调）：行为回归 2 失败 / 2 通过，artifacts/tests-preflight/behavior-red-final.trx。先前编译／fixture 失败日志单独保留，不算行为证据。
- 针对性 32/32；默认全量 1284/1284；M5 全量 1284/1284（default.trx / m5.trx）。
- 真实临时 SQLite 测试验证：第一项成功后第二项明确拒绝，不调用第二项适配器、无运行登记、不制造待清理 runs、后项跳过；重复提交旧计划不重放，新确认计划可执行；未知预检异常、适配器登记前／后专用异常都保持 Unconfirmed，并阻断新计划。
- 回执展示测试验证明确预检失败提示“尚未启动／重新确认”且不要求清理，适配器异常不展示安全未启动文案。未执行真实 WPF 点击或游戏验收。

## 独立审查

DeepSeekHelper / deepseek-v4-pro 共两轮。首轮提出的存储失败写终态建议经核对不采纳：存储失败要保留不完整状态给启动中断处理，且 WhenIdleAsync 会抛错，不能假定正常返回；已有 StorageFailureNeverStartsLaterWork(register) 覆盖登记写失败。接受前校验抛异常是原行为，专用类型继承 InvalidOperationException，不扩大本任务修改范围。第二轮附源码事实复核，结论 No confirmed new blocking defects / approve。两轮之间及复审后无实现改动。

原始审查位于 ignored artifacts/deepseek-preflight：

- response-1.json SHA256 `9CEE4F994ADA6BDA5ABD6EA2B22EEBA0A2BAE30E16FB8CC27A3B03F4D874AC07`
- response-2.json SHA256 `4EF9046BF10606DAE5A440EA57CE0B268938FEBB52DF9959BCCFCDDC8FD6486E`

边界：只读预检是可信宿主契约，并非沙箱。当前 App 回调仅检查现有状态及读取配置；未来接入不得在该回调内引入桌面／进程／配置写入副作用。
