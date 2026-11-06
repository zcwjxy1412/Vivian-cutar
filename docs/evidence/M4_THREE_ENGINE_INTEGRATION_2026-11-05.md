# M4 三引擎集成本机记录（2026-11-05）

从 `origin/main` 创建独立 `integration/m4-three-engines-codex` 工作区，依次组合 PR #43 (`5c65ddc`)、PR #44 (`1621d49`)、PR #45 (`06e68d2`)。三者组合没有 Git 冲突；未合并 PR #42，也未修改 Claude 的适配器源码。BetterGI 和 OneDragon 的专用只读安装检查已接入 WPF 引擎管理。三引擎预览构建显式关闭 `M4ScreenTestBuild`，另用独立数据目录，真实队列仍关闭。

本机组合 .NET 10 测试 514/514 通过；WPF 自包含预览冒烟通过渲染、SQLite、单实例、三游戏模拟串行队列、紧急停止、持久化和诊断导出。无真实游戏任务由集成预览包运行。BetterGI 与 OneDragon 上游本机安装身份及真实执行尚待验证。

星铁第二次独立包的单次 `screen_test` 经用户单独批准后启动。官方 v2026.10.31 文件身份再次核对一致。VIvian RunId `bcf81f20-5546-40c9-b1bb-d4f39386c9b7`，上游 RunId `20261105-215213-be09`；入口打印需要 UAC 的提示。约 92 秒后上游报告用户取消管理员授权、任务未运行，退出码 5（WinError 1223）。VIvian 独立数据库终态 `Blocked / Indeterminate / Unconfirmed / no_completion_evidence`；这是缺少 Worker 终态证明时的保守阻断。事后本机进程查询未发现 `m7a.exe`。未观察到游戏步骤、Worker PID 或完成证据；不把本次视为真实任务闭环通过。一次性标记仍在，第二包不能重试。
