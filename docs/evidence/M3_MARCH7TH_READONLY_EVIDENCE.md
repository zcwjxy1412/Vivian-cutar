# March7thAssistant 只读发现与发行包门禁

状态：**Local inspector implemented / release CLI behavior Pending**。对应 [#22](https://github.com/zcwjxy1412/Vivian-cutar/issues/22)。Gemini 原始研究见 [报告](../research/M3_MARCH7TH_ANALYSIS.md)，风险核对见 [审查](../reviews/M3_GEMINI_RESEARCH_VALIDATION.md)。

## 已有证据

| 项 | 依据 | 可支持的结论 |
|---|---|---|
| 上游发布记录 | [v2026.10.31 官方发布页](https://github.com/moesnow/March7thAssistant/releases/tag/v2026.10.31)，标注提交 `1e77d59` | 发布存在；**不等于**已取得资产或可运行 |
| 源码入口 | [固定源码 `21660281c8e51cf6db69879da8f30a2d1a792bac/main.py`](https://github.com/moesnow/March7thAssistant/blob/21660281c8e51cf6db69879da8f30a2d1a792bac/main.py) 与 [CLI 入口](https://github.com/moesnow/March7thAssistant/blob/21660281c8e51cf6db69879da8f30a2d1a792bac/utils/cli_entry.py) | 源码含只读查询入口；与 v2026.10.31 发行包的精确对应关系待核验 |
| VIvian 本地发现 | `March7thReadOnlyInspector` 和测试 | 用户指定绝对路径中是否存在 `m7a.exe`、文件版本元数据、完整 SHA-256；不会运行该文件 |

当前没有拿到官方完整包原始文件、官方完整 SHA-256、包内文件清单、解压后的 `m7a.exe` 与源提交对应关系。Gemini 报告中的截断哈希不能代替完整校验。CI 也未验证 `--status` 或 `--list --json` 的实际输出。因此兼容性恒为 `Unverified`，UI 明确显示“暂不支持查询”，不会启动 CLI、游戏或写上游配置。安装路径不存在、CLI 缺失、符号链接和读取期间变动也按保守状态提示。

## 下次验证精确发行包

1. 获取官方 v2026.10.31 **完整包**，记录资产 URL、文件名、字节数、官方发布 SHA-256 与本机独立计算 SHA-256；与打包提交完整 SHA 对应。下载与安装不得使用用户账号或私有配置。
2. 在隔离、无游戏的 Windows 10/11 x64 测试目录中记录 `m7a.exe` 的 SHA-256、文件版本、签名/清单；先读静态内容。逐项运行发行包的 `--status` 与 `--list --json`，保存原始 stdout/stderr、退出码、启动前后配置摘要、权限和新进程清单；若其中任何调用会提权、启动游戏或写配置，立即停止并记为不支持。
3. 给实际状态 JSON 和任务列表 JSON 制定针对该哈希的只读解析与错误输入测试。只在精确版本门禁通过后开放查询能力；版本变化一律退回 `Unverified`。
4. 任务执行、提权和完成证据属于后续阶段，受 #16、ADR-007 与用户真实游戏授权限制。

本文件没有声明发行版 CLI 已验证，也没有声称游戏任务可执行。
