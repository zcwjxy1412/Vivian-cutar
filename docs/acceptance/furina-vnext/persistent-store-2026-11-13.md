# 持久计划数据库验证（I）

公共契约 30aa4b5；实现使用 schema 8 的 execution_plans / execution_plan_runs。SQLite immediate 事务串行化接受、启动和终态转换，SHA256 覆盖有序冻结载荷；运行关联先落盘，未知 ID 不回退最近结果。旧 schema 7 的合流档案仅在隔离副本升级，保留 pre-v8.bak，原档案保持 schema 7。含未完父计划的 schema 8 档案不允许复制为第二执行入口。

本地验证：首轮全量 1185/1185；随后明确 immediate 参数、补并发项目启动/活动计划复制拒绝/损坏回执校验，最终相关测试 22/22。完整日志 artifacts/v3-store-tests.log；新测试 Execution/PersistentExecutionPlanTests.cs。正式数据未迁移，没有启动游戏。

DeepSeekHelper 实际 MCP deepseek_review 共两轮（deepseek-v4-pro），证据 artifacts/deepseek/v3-store/round{1,2}-response.json。首审所称 C# 优先级及并发竞态经源码和测试核实不成立：restored=false 原测试已阻断，事务原本为 immediate；现明确括号和 deferred:false 便于审阅。失败任务仍可成功恢复配置，两者不是矛盾状态。其余多数条目是正确行为说明。

复审核实一项健壮性缺口：从数据库反序列化的回执未校验结构。已补摘要、字段、ID、顺序和终态校验以及损坏回执拒绝测试。此修复完成本地验证；遵守两轮上限不再调用第三轮，**本任务不标记最终代码已通过独立复审**。该记录不是宿主接线、协调器或真实游戏验收结论。
