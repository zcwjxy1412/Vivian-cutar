# 原神登录失败后的清理结果传递

基线 `2ed964c`，分支 `fix/genshin-login-cleanup-outcome`。只修改 MultiAccountOrchestrator、BetterGiAccountBatch 与对应测试；没有修改 Core、App、登录／焦点驱动及其测试。

## 问题与证据

2026-11-15 00:30:55 与 00:40:14 EST 的两次正式原神批次均记录 login_screen_failed，原因是前台窗口或客户端身份变化；没有进入任务 runner，没有 RunId。此失败发生在 PrepareAsync 返回之后，不能称为启动等待超时。

MultiAccountOrchestrator 的初始登录失败分支提前调用 FinishAsync，却遗漏任务结果、引擎清理、配置恢复参数，得到 null / false / false。BetterGiAccountBatch 随后执行客户端清理，只更新 GameCleanupConfirmed。因此即便客户端清理成功，计划也被判为 Indeterminate / cleanup_unconfirmed。

旧隔离首跑历史库 schema 7 与当前 schema 8 的严格读取不兼容，是另一个历史展示问题；与此次登录失败没有直接因果。本补丁不迁移该库。

## 行为边界

- 初始登录明确失败时传递 RunOutcome.Failed，以及此时仍为 true 的 engineClean、configurationRestored。这两个变量仅在实际调用任务 runner 前才改为 false；不从标记文件、没有 RunId 或进程快照推断清理。
- 游戏清理仍为 false，必须等待 BetterGi 外层对本轮客户端实际调用 StopGameAsync。成功才设 true，失败或抛错保持 false。
- 提取 CompleteFailedCleanupAsync 供隔离测试，并保留外层最新清理检查覆盖旧结果的语义。不能用历史 true 与本次 false 做 OR。
- 客户端清理不会改变引擎、配置、宿主状态、任务 Outcome、完成计数；引擎已启动或配置待恢复不能借此变成已确认。
- 原始登录失败保存在批次和账号执行历史；最终清理详情附加在返回的 MultiAccountOutcome.Detail 中。清理抛错时保留原始失败原因并追加异常详情。最终清理详情未回写原批次行，展示层若需要它应使用返回值。
- 编排／持久化抛错的既有外层 catch 仍清理并抛出，不伪造有效结果；协调器继续将未知适配器异常视为未确认。

## 验证

- 红：初始登录失败回归测试 1 失败，Expected Failed / Actual null。
- 绿：针对性 51/51，包含编排器、BetterGI 批次、持久计划协调器既有回归。
- 新增 6 项：登录失败＋客户端清理成功／失败／抛错；引擎清理未确认、配置未恢复或两者同时存在时，客户端清理成功仍原样保留其他状态。真实临时 SQLite 验证历史结果及无 RunId，全部客户端动作使用假驱动。
- 默认全量、M5 全量：各 1304/1304 通过。
- 本地证据位于 ignored `artifacts/tests-login-outcome`。

## 独立审查

DeepSeekHelper / deepseek-v4-pro / thinking=disabled 共两轮。首轮两项意见均不采纳：异常后的编排结果不存在，存储失败路径必须抛出；覆盖 GameCleanupConfirmed 是原有代码，保留旧 true 会掩盖更新的清理失败。第二轮提交相同实际 diff 与事实复核，结论 no confirmed new defects / no actionable fixes。没有根据审查修改实现。

原始响应在 ignored `artifacts/deepseek-login-outcome`：

- review-1.json SHA256 `6AB9E6247993CC62EC44E0F1E52BB88A14A799E15B1E6F5BD478D4E58E3FA187`
- review-2.json SHA256 `6C107814DDFA6E876F91DF3011437C7D4BEB3A998700C3FC5783D1C29FCE49EC`

审查文本中的历史源码推测不代替本地验证。未启动／停止真实游戏、未部署、未修改正式账号数据或恢复标记、未自动确认旧记录；实际任务栏和登录焦点验收由独立修复负责。
