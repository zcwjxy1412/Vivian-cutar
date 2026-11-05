# M4 Windows 本机核对（2026-11-05）

- 独立源码目录 `E:\auto agent game\Vivian-source`，分支 `feature/m4-march7th-integration`；未操作现有 `VIvian-win-x64` 或 `%LOCALAPPDATA%\VIvian\vivian.db`。
- 现有阶段 B 本机报告：B1、B2、B3、B4、B4a、B4b、B4c 为 PASS；B5 为清理 Unconfirmed、结果 Indeterminate、队列 Blocked。UAC、官方 CLI、迟到启动与数据库故障注入为 NOT RUN。
- 本机官方 `March7thAssistant_full.zip`：719711875 字节，SHA-256 `e03aaad4ea27c70f30bf5241ab3bfac8894de7675ddd30a9aef7f1e551af9985`；包内版本 `v2026.10.31`，`m7a.exe` SHA-256 `38617000de5497fc1717de37dc771d2a9b57dff6119464bc6fac6c16f64bbda1`。只读核对，未执行该程序。
- 本机 .NET 10.0.403：359 项测试通过；独立 Windows 自包含构建成功；WPF 冒烟通过（渲染、隔离 SQLite 往返、模拟队列及停止）。
- 固定上游 `utils/runs.py` 的 `success` 来自退出码 0，因此目前星铁真实任务完成证据仍不足。
- 用户明确同意后，在已核对官方包的临时隔离副本内运行一次 `m7a.exe --status`：CLI 状态 `Completed`、退出码 `0`、归属进程清理 `Confirmed`、耗时约 `0.8` 秒；探针最终返回阻断（退出码 `1`）。外层非交互捕获发生中文乱码，无法可靠保留具体阻断文案；不推断或填写原因。首次尝试在确认词阶段取消，未执行 CLI。未请求 UAC，也未运行游戏任务。
- 真实任务、UAC 归属与跨权限停止仍未验证，应用真实执行闸门保持关闭。
