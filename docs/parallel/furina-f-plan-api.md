# F v3 协调器接口

基线 30aa4b5；实现进行中。I 宿主可依照以下签名连接，不依赖数据库落地。

命名空间 `VIvian.Infrastructure.Accounts`：

```csharp
public delegate Task<MultiAccountOutcome> ExecutionPlanBatchRunner(
    ExecutionPlanItemReceipt item,
    Func<Guid, CancellationToken, Task> beforeRun,
    CancellationToken cancellationToken);

public sealed class PersistentExecutionPlanCoordinator
{
    public PersistentExecutionPlanCoordinator(IExecutionPlanStore store,
        ExecutionPlanBatchRunner executeBatch, Action? stopActive = null,
        CancellationToken hostStopping = default);
    public Task<ExecutionPlanSubmission> SubmitAsync(PreparedExecutionPlan plan, CancellationToken ct = default);
    public void StopAll();
    public Task WhenIdleAsync(CancellationToken ct = default);
}
```

宿主 runner 根据 item.Item.Game 调现有入口，使用 item.ChildBatchId 与单账号；D/E 的 beforeRun(accountId,runId,ct) 闭包核对 accountId 后调用协调器回调。I 星铁桥接同样必须在副作用前 await。stopActive 接既有本地停止方法，不得递归回调协调器 StopAll。关闭 AI 仅取消 SubmitAsync 的接受 token；hostStopping 只绑定程序宿主结束。

WhenIdleAsync 等待当前已接受工作；持久化故障会抛出，且协调器阻断新执行。启动对账由宿主单实例锁下先调用 store.InterruptUnfinishedAsync，再构造协调器。构造不会扫描或重放持久记录。普通失败停止当前计划，清理/恢复不确认则阻断此协调器后续工作。

实现步骤：先模拟仓储验证并发幂等与 token 分离/持久失败；再 D/E 关联与批次结果字段；最后两构建离线回归、实际 diff DeepSeek 首审及必要复审。没有真实游戏或显示窗口验证。

## 已实现 D/E 接缝

- BetterGiAccountBatch.ExecuteSavedDailyAsync 保留原 8 参数重载（兼容 App 注册方法组），新增重载在原 ct 之后接 `Guid? childBatchId, Func<Guid, Guid, CancellationToken, Task>? beforeRun = null, bool stopOnFailure = false`。
- OneDragonAccountBatch.ExecuteAsync 原 ct 之后追加 `Guid? childBatchId = null, Func<Guid, Guid, CancellationToken, Task>? beforeRun = null, bool stopOnFailure = false`。
- 上述 beforeRun 的两个 Guid 顺序固定 accountId、runId；新计划必须传 childBatchId / beforeRun / stopOnFailure:true。
- 按 I 最新要求，两入口尾部再追加 `Func<CancellationToken, Task>? validateConfirmed = null`；取得现有桌面锁后立即 await，先于批次配置冻结及任何恢复写入/桌面副作用。宿主传入重读当前配置/任务版本并比较 frozen item 的闭包。验证异常直接阻断，未开始游戏。
- MultiAccountOutcome 尾字段：`RunOutcome? Outcome = null, bool EngineCleanupConfirmed = false, bool ConfigurationRestored = false`。orchestrator 从 BatchTaskResult 获取，不能以 Completed 代替 Outcome 或清理证据。OneDragon 额外等待 finally 的游戏停止与桌面 ConfirmClean 后才返回最终结果。
- 首批接缝及协调器离线回归 56/56；最终默认/M5 全量各 1209/1209。DeepSeek 两轮已用完，最后 WhenIdle/StopAll 修正仅本地验证，最终代码未获独立复审通过；不能作为发布或实机验收。

## I 资源摘要问题核对

只读核对本机 `E:\VIvianEngines\OneDragon-v2.6.0\src` 源码，未读取正式账号配置：ApplicationConfig 构造器的路径是 config/NN/group_id/app_id.yml，默认组常量为 one_dragon。CoffeeConfig 的 app_id=coffee，ChargePlanConfig 的 APP_ID=charge_plan；故当前路径为 `NN/one_dragon/coffee.yml` 与 `NN/one_dragon/charge_plan.yml`（相对 config）。ApplicationConfig 在组路径缺失时可能从 `NN/coffee.yml`、`NN/charge_plan.yml` 复制迁移。

咖啡 `run_charge_plan_afterwards` 默认 false，可触发后续体力计划；体力 `restore_charge` 有不使用/储蓄电量/以太电池/两者四档，另有 plan_list（plan_times/run_times/card_num 等）、double_reward、combat_simulation_double_reward_config、skip_plan。不能仅哈希 _group.yml 宣称已冻结资源策略。

最小建议：I 将两份当前文件的路径、存在标志与全文摘要纳入确认版本；缺失时同时记录旧路径是否存在及其全文摘要，模拟上游只读选择语义但不主动迁移。源代码版本身份、game_account/group/relevant binding 等仍独立进入总摘要。全部缺失时明确表示上游默认，不能把缺失等同于显式禁用。摘要仅检测变更，未实现字段解释/资源预算核验；确认卡应写“资源参数由 OneDragon 上游设置控制，未解析消耗上限，请实机前核对”。未更改 OneDragonSourceProfile.Revision 兼容语义。

