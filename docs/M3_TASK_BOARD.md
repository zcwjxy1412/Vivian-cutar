# M3 任务与依赖（2026-11-04 更新）

main：`7a73ebe`（0.3.0-dev）。Windows CI：[41921391010](https://github.com/zcwjxy1412/Vivian-cutar/actions/runs/41921391010)，267/267 通过，WPF 冒烟测试通过（模拟引擎与工程测试，不是真实游戏的证明）。

分工：Claude 负责实现；Codex 负责每个实现 PR 的一次聚焦独立审查和整合验收；Gemini 只在具体的上游技术问题上做研究；DeepSeek 不再参与常规审查。

| 任务 | 状态 | 下一步 / 阻断 |
|---|---|---|
| 调度器安全门禁（#19、#28、#29、H-30、P0） | 已完成：PR #20、#30、#33 已合并，Codex 验收通过 | — |
| [#16](https://github.com/zcwjxy1412/Vivian-cutar/issues/16) 权限与 Job 边界 | 无害探针与 `OwnsProcess` 已合并（PR #23） | 桌面 UAC 同意/拒绝、提权子进程归属、执行器被杀：需要用户提供桌面并授权 |
| [#22](https://github.com/zcwjxy1412/Vivian-cutar/issues/22) 星铁只读发现 | 只读发现、版本识别、链接与 TOCTOU 防护、有界查询、界面状态已合并（PR #24、#35） | 在隔离副本中实测 `--version`、`--list --json`、`--status` 的输出与副作用，之后才开放受哈希门禁保护的查询 |
| [#25](https://github.com/zcwjxy1412/Vivian-cutar/issues/25) 星铁官方发行包证据 | 官方摘要已固定；核对工具已合并（PR #26、#35） | 用真实下载的官方 zip 实测核对，并记录独立计算的哈希 |
| BetterGI 适配器 | 只读发现程序文件，状态“不支持” | 独立的版本与生命周期证据、实机验收；排在星铁之后 |
| OneDragon 适配器 | 只读发现程序文件，状态“不支持” | 排在 BetterGI 之后；不能照搬其他引擎的权限与进程假设 |

发布之前关闭全部 Critical / High 缺陷；没有完成确认的进程清理时，不进入下一任务。
