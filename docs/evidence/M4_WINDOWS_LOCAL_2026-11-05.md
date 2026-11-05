# M4 Windows 本机核对（2026-11-05）

- 独立源码目录 `E:\auto agent game\Vivian-source`，分支 `feature/m4-march7th-integration`；未操作现有 `VIvian-win-x64` 或 `%LOCALAPPDATA%\VIvian\vivian.db`。
- 现有阶段 B 本机报告：B1、B2、B3、B4、B4a、B4b、B4c 为 PASS；B5 为清理 Unconfirmed、结果 Indeterminate、队列 Blocked。UAC、官方 CLI、迟到启动与数据库故障注入为 NOT RUN。
- 本机官方 `March7thAssistant_full.zip`：719711875 字节，SHA-256 `e03aaad4ea27c70f30bf5241ab3bfac8894de7675ddd30a9aef7f1e551af9985`；包内版本 `v2026.10.31`，`m7a.exe` SHA-256 `38617000de5497fc1717de37dc771d2a9b57dff6119464bc6fac6c16f64bbda1`。只读核对，未执行该程序。
- 本机 .NET 10.0.403：359 项测试通过；独立 Windows 自包含构建成功；WPF 冒烟通过（渲染、隔离 SQLite 往返、模拟队列及停止）。
- 固定上游 `utils/runs.py` 的 `success` 来自退出码 0，因此目前星铁真实任务完成证据仍不足。
- 用户明确同意后，在已核对官方包的临时隔离副本内运行一次 `m7a.exe --status`：CLI 状态 `Completed`、退出码 `0`、归属进程清理 `Confirmed`、耗时约 `0.8` 秒；探针最终返回阻断（退出码 `1`）。外层非交互捕获发生中文乱码，无法可靠保留具体阻断文案；不推断或填写原因。首次尝试在确认词阶段取消，未执行 CLI。未请求 UAC，也未运行游戏任务。
- 修复 UTF-8 捕获和脱敏结构化报告后，重新在官方隔离副本执行状态查询：`cliState=Completed`、`ExitCode=0`、`cleanup=Confirmed`、`OutputTruncated=false`、`fileSnapshotIncomplete=false`、`block=FirstRunNotConfirmed`。明确阻断字段为上游首次免责声明未由本人确认；用户需自行在上游界面完成，不由 VIvian 代为确认。报告位于本机独立状态探针目录的 `状态结果.json`，不含原始路径或账号内容。
- 真实任务、UAC 归属与跨权限停止仍未验证，应用真实执行闸门保持关闭。
- 用户本人在上游界面完成首次免责声明并核对游戏路径后，重新运行同一官方发行包的隔离 `--status`：`cliState=Completed`、`ExitCode=0`、`cleanup=Confirmed`、`OutputTruncated=false`、`fileSnapshotIncomplete=false`、`readyForUacValidation=true`、`block=None`。仅证明基本前置条件通过；未执行游戏任务或请求 UAC。脱敏结果位于本机状态探针目录的 `状态结果.json`。
- M4-A2：按官方 v2026.10.31 `utils/runs.py` 核对，最终记录的 `pid` / `pid_create_time` 属于实际任务进程；`utils/cli_entry.py` 的停止回执 `acknowledged` 只表示指令已收到。代码增加精确 RunId 回执、PID 与创建时间观察，仍要求本次终态记录及自身 Job 清理；无法确认则返回 Unconfirmed。365 项本机测试通过，独立 WPF 冒烟通过。真实执行闸门未打开。
- 无 UAC 的无害边界工具本机试跑：普通权限工作进程及其子进程的身份匹配，停止标记送达后两者退出。提权路径尚未执行；独立方案见 `M4A2_UAC_TEST_PLAN_2026-11-05.md`。
- 用户单独批准后，执行一次无游戏 UAC 边界工具：系统 UAC 结果为 accepted，启动句柄 PID 与本次工作进程记录一致；工作进程与其子进程均为提权状态、父子 PID 匹配，停止前均为 Running，停止标记送达后均为 Exited，工具结果 `cleanupConfirmed=true`；按报告中的两个 PID 复查均已不存在。报告位于 `E:\auto agent game\M4A2-UAC-probe-result\结果.json`。这仅验证无害类比进程，真实 March7thAssistant Worker 与游戏行为仍未实测。
- 核对固定上游 `screen_test`：任务会打开/连接星铁并逐项切换可达界面；代码只在本次运行全部切换记录和计数一致、上游终态匹配时认定完成，退出码 0 单独不构成证据。该证据规则通过合成测试，真实任务未运行；首次游戏测试副作用与停止方案见 `M4A2_FIRST_GAME_TEST_PLAN_2026-11-05.md`。
