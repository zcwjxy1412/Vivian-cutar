# VIvian M2 外部评审材料（供 DeepSeek 与 Gemini 使用）

> 用法：把这份文件整份发给评审方。仓库地址：https://github.com/zcwjxy1412/Vivian-cutar （目前为公开仓库）。如果评审方无法访问 GitHub，请把 §3 中列出的文件内容一并粘贴给它。
> 评审基线：`main` @ `06d2cda`。

## 1. 背景

VIvian 是一个 Windows 桌面程序，用来统一调度三款开源游戏自动化引擎（BetterGI、March7thAssistant、ZenlessZoneZero-OneDragon）的日常任务。它**不**重写这些引擎，而是通过独立的适配器调用它们。

M2 只实现“执行内核”，并用模拟引擎验证，**没有接入任何真实引擎**。M2 必须满足的规则：

- 执行阶段、任务结果、清理结果三层状态相互独立
- 退出码 0、日志里没有报错、游戏已关闭，都不能作为成功的依据；只有本次 RunId 的明确完成证据才算成功
- 只终止属于本次运行的进程（Windows Job Object），**绝不按进程名结束进程**
- 清理无法确认时，阻断后续所有任务，跨重启也一样，直到用户确认
- VIvian 崩溃后诚实对账，绝不把中断的运行标为成功
- 紧急停止立即生效，没有倒计时
- 不确定的任务不自动重跑

## 2. 评审分工与具体问题

### DeepSeek：代码质量、安全、并发、可靠性

1. `QueueScheduler` 中有没有可能让下一款游戏在上一款清理未确认时就启动的路径？
2. 紧急停止有没有可能丢失或被推迟？请特别关注 `EmergencyStop()`、调用方令牌、`RequestStopSafeAsync`，以及发出停止与写记录的先后顺序。
3. 有没有可能把没有证据的运行判为 Success？请关注 `Judge`、`VerdictPolicy`、`FakeEngineProtocol.Parse`。
4. `WindowsProcessSupervisor`：挂起创建 → 放入 Job → 恢复这个顺序中有没有漏洞？句柄泄漏、`bInheritHandles=true` 带来的风险、管道读取线程、退出回调与释放之间的竞态。
5. `RunStore`：崩溃对账在什么情况下会给出错误结论？记录不可覆盖的约束是否足够？迁移失败时能否完整回滚？
6. `SingleInstanceGuard`：遗弃锁、拒绝访问、跨线程释放的处理是否正确？
7. 测试本身：有没有测试实际上什么也没验证？有没有依赖机器快慢、可能偶发失败的测试？

### Gemini：架构与 UI/UX

1. `IGameAdapter` / `IEngineRun` 契约是否足以承载三款真实引擎？（三款引擎的事实需要单独核实，见 `docs/M3_PREREQUISITES.md` §B）有没有应该在 M3 之前补上的抽象？
2. 三层状态模型以及原因码，对用户来说是否容易理解？`RunPresentation` 中的中文措辞是否合适？
3. “模拟队列”界面：能否一眼看出这是模拟、不是真实结果？紧急停止是否足够醒目？“清理未确认”的提示与确认流程是否清楚？
4. 有没有界面上的误导风险，比如让用户以为游戏日常已经完成？

## 3. 关键文件

| 文件 | 内容 |
|---|---|
| `docs/M2_PLAN.md` | 已批准的 M2 计划 |
| `docs/M2_ACCEPTANCE.md` | 验收结果、已修复的缺陷、剩余风险 |
| `src/VIvian.Core/Execution/Contracts.cs` | 适配器与进程监管契约 |
| `src/VIvian.Core/Execution/ExecutionModel.cs` | 三层状态、状态机、证据判定 |
| `src/VIvian.Core/Execution/QueueScheduler.cs` | 串行调度器（最关键） |
| `src/VIvian.Core/Execution/RunPresentation.cs` | 面向用户的中文措辞 |
| `src/VIvian.Infrastructure/Processes/WindowsProcessSupervisor.cs` | Job Object 进程监管 |
| `src/VIvian.Infrastructure/RunStore.cs` | 运行记录与崩溃对账 |
| `src/VIvian.Infrastructure/SingleInstanceGuard.cs` | 单实例锁 |
| `src/VIvian.Infrastructure/Simulation/FakeEngineAdapter.cs` | 模拟引擎适配器 |
| `src/VIvian.App/QueueViewModel.cs`、`MainWindow.xaml` | 模拟队列界面 |
| `tests/VIvian.Tests/Processes/FakeEngineScenarioTests.cs` | 11 个故障场景 |

## 4. 请按以下格式回复

每条发现：

- **严重度**：Critical / High / Medium / Low
- **位置**：文件和行号（或函数名）
- **问题**：一句话说明
- **触发条件**：具体的输入或状态 → 错误结果
- **建议**：怎么修

**请只报告你实际看过代码后确认的问题。** 推测性的问题请标明“推测”，不要和已确认的问题混在一起。
