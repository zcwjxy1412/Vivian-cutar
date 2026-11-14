# E 日志写入边界与 D-2 终态修复

基线：`a1e05ed`；分支：`fix/engine-journal-boundaries`。仅调整 Infrastructure 引擎实现和隔离测试；没有改 Core 接口、数据库结构、App、正式账号或引擎配置，没有启动真实游戏。

## 行为

- OneDragon Starting、Running、事件追加及终态写入均有默认 8 秒等待上限。同步阻塞的调用也在后台执行；用户停止能够中断 Running 写入的等待，随后进入本次拥有的引擎清理。
- 同一运行的写入串行化，超时后仍持有写入顺序，直至原写入真正退出。排队写入超时后不会在解锁时重新执行；晚到失败均被观察，不覆盖首个异常。数据库现有终态保护保持不变。
- 事件写入失败可提前触发停止；停止、核对清理后只调用一次释放。首个运行异常不会被释放或补写诊断的异常替换。
- 启动异常后的 Blocked 诊断失败或挂起时，保留原始 `EngineStartException` 对象。
- 持久计划遇到日志挂起停止，第二个子批次跳过。BetterGI 清理／恢复未确认时记录 Blocked，确认后才记录 Finished。

## 验证

- 修复前行为回归：8 失败、7 通过，见 `artifacts/tests-journal/behavior-red.log`。
- 最终针对性测试：34/34；默认全量：1266/1266。
- M5 首次全量：1265/1266；唯一失败为既有 `M5StopAndCleanupTests.PauseAndResumeWaitForWorkerAcknowledgement` 第 173 行暂停确认。原始 `m5.log` / `m5.trx` 保留，未修改该测试或 March7th 实现。
- M5 停止／暂停测试组单独复测：10/10；相同代码全量复测：1266/1266（`m5-recheck.log` / `m5-recheck.trx`）。初次暂停确认失败未再次复现，原因尚未确定，不据此宣称已修复。
- 新测试包含同步／异步挂起、用户停止先到达清理、晚到写入顺序、过期排队调用不执行、真实临时数据库终态保护、持久计划阻断，以及 BetterGI 释放失败后的数据库阶段。

## DeepSeek 独立审查

本任务共两轮，均使用 DeepSeekHelper MCP / deepseek-v4-pro，提交实际改动（含新增文件）及必要上下文。

首轮回复达到输出上限，部分推测随后自行排除。逐项核对未发现确认的实现问题：停止测试在等待批次前已显式解除模拟写入阻塞；私有 CTS 仅在写入和取消回调结束后释放；串行锁在成功取得后才释放。无关协调器和无限游戏运行时长不扩展到本任务。第二轮附这些事实要求给出完整结论，结果为 `No confirmed implementation defects` / `Overall: approve`。两轮之间及第二轮之后未修改实现。

复审文字中对晚到写入的残余描述有先后矛盾；本地源码与测试的准确结论是：同一运行的旧写入始终先释放串行锁，后续终态才可能写入。不能保证任意忽略取消的 I/O 被物理终止，也不能保证终态一定落盘；永久挂起时记录可能停在 Starting/Running，但调用方有界退出，计划不会继续，恢复标记不因后台写入而清除。

本地原始证据（ignored artifacts）：

- `artifacts/deepseek-journal/response-1.json` SHA256 `CBAA5C777D5CE9D6C722DE4F2C753E5E58CDB6B736B3DFB81F4BF836D24B4D39`
- `artifacts/deepseek-journal/response-2.json` SHA256 `E4135D6ED933764DA1168A99C117F491DA52BBF33C45C563ED247B6BAE5AFE77`
- `artifacts/deepseek-journal/reviewed.diff` SHA256 `0FD1E8F5F93575E51461DC23A364D9C6055729B27580DC1A678CC76B2841B864`

## 边界

8 秒是每次日志等待的上限，不是完整停止的总时限。清理、输出收尾和补写可增加总耗时；底层永久挂起的写入可能继续占用后台任务／线程和串行锁，不强杀线程。保留不确定状态而非宣告清理成功。真实三游戏验收和正式部署由总控与用户后续执行。
