# 公共契约 v2 — 空闲聊天许可与精确批次查询

本增量不迁移数据库，不开放新的游戏执行能力。v1 其他接口保持兼容。

## 输入许可（I 提供；B/C 消费）

- Core.Execution.IAssistantInputGate.TryAcquire(out IDisposable? permission, out string? reason) -> bool。
- Infrastructure.Processes.AssistantInputGate(string profileDirectory, Func<bool> blocked) 实现：状态阻断先检查，再取得现有 DesktopOperationLease，持锁复查状态；成功返回持有桌面锁的许可。Dispose 释放，不标脏、不清除恢复、不暂停/恢复游戏。
- B 在 MainViewModel 构造中提供 blocked：!CanOperateWorkspace || CanStopAll || Queue.HasUnresolved || _control.HasPendingConfigurationRestore。MainViewModel 属 B，总控允许 B 仅将此已发布构造器接入现有模型，不改公共契约。
- B 打开/激活/聚焦/启用可编辑聊天前必须取得许可；失败时留在原界面给出原因，不先 Show/Activate 再检查。新消息仅更新内容。窗口隐藏/关闭/失活时禁用输入并释放许可；再次显式进入需重新获取。外部强制切入窗口不能跳过许可；此时保持禁用，不自动尝试暂停。
- C 增加 AssistantViewModel.ExclusiveActionStarting 同步事件（Action）。用户确认配置/运行后、取得桌面写入锁/提交运行之前调用；B 的订阅者先禁用全部可编辑输入并释放许可。C 保留原准入、revision、桌面租约检查。无事件订阅不移除这些检查；关闭聊天只取消聊天。
- 首版运行中一律拒绝输入；暂停回执不是已验证安全失焦，后续实机验收后才考虑扩展。

## 精确查回（I 提供；C 消费）

LocalDatabase.LoadBatchAsync(Guid batchId, CancellationToken ct = default) -> Task<AccountBatch?>，直接按主键读取，Guid.Empty 拒绝，未知返回 null。不回退最近记录，不把 null 当作可自动重发。

复用 LoadBatchResultsAsync(batchId) 获取已记录结果及 RunId；运行中尚无结果时保持未知。现有 plan.Id 等于星铁 actionId/batchId。C 可为 get_batch_progress 增加严格 batch_id，或 get_run_status 增加 plan_id；必须明确首版仅查回旧单星铁计划，不伪称父多游计划或完整实时运行关联。

多游计划、同 ID 不同载荷拒绝、接收后自动恢复等仍待后续版本；现有持久主键去重保护保留。本次不把 batch 的单游戏语义改为多游戏，也不增加任意后台执行。

## 验证

新 API 缺失时测试编译失败，加入实现后新功能及账号存储回归 10/10（0 失败/跳过）。覆盖互斥许可释放、忙碌/恢复阻断、取得锁期间状态变化、超出最近20条的精确查询、未知ID不回退、重开数据库与中断对账。完整集成验证及 DeepSeek 审查另记。
