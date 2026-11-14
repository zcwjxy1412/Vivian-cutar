# 一键日常第二项停止：运行时间戳版本修复

基线 `01635904d0a6b812469f828d30275713c01b9dcc`，分支 `fix/latest-daily-receipt`。仅修改 Infrastructure 与隔离测试；未修改 App/MainVM/窗口、正式档案或上游配置，未启动游戏、清除恢复记录或代为确认清理。品牌迁移暂后置。

## 现场只读结论

2026-11-14 09:29–09:31（America/New_York）计划 `d9a1f71f-261d-4f31-907d-242838162d02` 在 09:31:56 为 Stopped：

- 第一项星铁：运行 `7be24f4e-42ea-41e5-9ba4-19ac8b95343d` 为 Success / Confirmed；子批次 `fe2cf1d0-fbcc-42d0-aa01-641f1cd00434` 为 finished。
- 第二项星铁：Indeterminate / Unconfirmed / ConfigurationRestored=false / adapter_error；没有 RunId，且没有相应 batch_execution_records 行，异常发生在创建批次之前。
- 后续两项原神、两项绝区零全部 Skipped。
- 正式库未解决运行数 0、运行中批次数 0；档案级 temporary-config-recovery.json 和 protocol-recovery.json 均不存在；共同桌面标记为 0 字节。这些是当次检查时的状态，不是未来清理授权。
- Queue Completed 只对应第一项。QueueViewModel.RefreshAsync 捕获原神隔离历史 InvalidDataException 后继续刷新，因此该警告不是这次中断的直接原因。

## 根因链路与证据边界

SavedExecutionPlanReader 原先冻结 StarRailAccountConfigurationProvider 的版本；该版本包含上游 config.yaml 整文件 SHA256。固定版的 assets/config/config.example.yaml 第 191 行声明 last_run_timestamp 为上次日常运行时间戳。现场该值为 1794666702.4456193，对应 09:31:42；文件修改时间同为 09:31:42，在第一项运行期间。

引擎正常完成后更新时间戳，使第二项的确认版本变化；MainViewModel.ExecutionPlans.cs 的 ValidatePersistentItemAsync 会拒绝。协调器吞掉具体异常后统一给 adapter_error / Unconfirmed，随后 MainViewModel.ExecutionPlans.cs:149 调用 BlockPersistentPlans；下一次点击在第 127 行提示先完成清理。用户因而看到清理提示，但没有对应待清理运行。

原异常消息／堆栈未持久记录，不能声称已从现场堆栈证明唯一原因。隔离测试用真实 LocalDatabase、SavedExecutionPlanReader 和 PersistentExecutionPlanCoordinator 重现了同一链路：只更新时间戳，第一项成功后第二项未执行，计划停止；修复前该用例失败（期望 2 次，实际 1 次），资源变更阻断用例通过。

## 修复与保留的门禁

执行计划确认专用读取只规范化唯一、可解析、数字形式的 last_run_timestamp 标量值；其余字节全部纳入版本，包括未知资源配置、注释、引号和格式。重新读取原始文件时先核对其摘要等于已读取快照，防止将两次不同读取拼成一个版本。

普通配置读取、提案和写入继续使用严格整文件版本。带引号的数字时间戳可规范化；缺失、重复或异常字段不放宽。其他上游运行字段仍严格检查，若后续出现另一种运行字段变化，需要独立证据后再扩展。

总控需处理的 App 事项：将启动前条件变化与真实待恢复清理区分，并保留安全的异常分类／诊断；不能因为当前桌面标记为空而一概确认任意 adapter_error 的清理。此补丁不修改 App，也不重写历史失败回执或自动重跑旧计划。

## 验证与独立审查

- 修复前：行为回归 1 失败 / 1 通过，artifacts/tests-daily/behavior-red.trx。
- 初始针对性测试 26/26、初始全量 1276/1276；最终追加带引号／缺失字段测试及可选读取锁一致性后，默认全量 1278/1278、M5 全量 1278/1278 均通过（default-final.trx / m5-final.trx）。
- 本任务 DeepSeekHelper 两轮（deepseek-v4-pro）：首轮建议补可选读取锁及边界测试，已落实；其关于重复键返回第一项、引号数字不支持和同一 byte[] 解码竞态的推测经源码核对不成立。保留缺失／非法字段严格校验。第二轮结论为 No blocking bugs found / diff acceptable。复审后没有实现变更。
- 原始审查位于 ignored artifacts/deepseek-daily：response-1.json SHA256 `459E6DCBDE855304531463606221C5C431274C7B0F776ABCBD14C7F43C68CA4F`；response-2.json SHA256 `BAF3EE0280DB322CB02BFAC26905D5DDCC60F3CAF6AA065339CC02723019A477`。

不代表真实三游戏整批验收通过；部署、界面修复和用户实测由总控继续。
