# Gemini M3 研究审查

七份报告及最终问答来自 [Gemini 发布原文](https://docs.google.com/document/d/e/2PACX-1vR_eKvVkgqu2T-qsZmf6-wZtG3geiY9e0YaOyCyZRgiNH739d6Jqjc0nKnnvwyWV7T_vDzfM8gP6xct/pub)。导入保留报告正文、表格、源码链接、代码示例和内联图示。原文的证据等级、推荐顺序和版本示例仍是作者结论。本次审查以当前 VIvian 代码和已固定的上游源码为依据，三款引擎的对应发行包与桌面实测仍为 Pending。

| 核查项 | 与当前实现或证据的差异 | 处理方式 |
|---|---|---|
| 预启动清理同名进程 | 最终问答第 5 项建议清理同名进程。进程名称不能证明归属，适配器契约禁止按名称查找或停止进程。 | 识别到可能的外部冲突时阻断并提示用户。停止和清理只通过本次运行的监督器及已确认归属的 Job。 |
| 停止与输入 | 架构 §2.5 的全局 F10、WM_CLOSE、`Process.Kill(entireProcessTree: true)`、全局 SendInput 有作用到其他应用或未受监督进程的风险。固定等待也不能证明清理。 | 正常停止只采用已实测且能绑定本次运行的控制通道。紧急停止立即由监督器终止所属进程。清理无法确认时阻断队列。 |
| 成功判定 | 星铁报告和最终问答把退出码 0 视为成功。日志结束行、固定静默时间、根进程退出与 Job 清空也各自只能证明局部事实。 | 退出码及日志作为原始信号。只有与本次 RunId、目标任务和版本规则匹配的完成证据才能报告成功。无证据或信号冲突时保持 `Indeterminate`，清理状态独立记录。 |
| 版本状态 | 兼容矩阵的 `VerifiedRange`、建议版本与坏版本名单是研究示例。发行包表给出的截断 SHA-256 无法验证下载产物。 | 所列版本全部待实测。逐个记录官方资产、完整哈希、源码提交、系统环境、任务证据和异常场景。验证范围不得自动扩展到相邻版本或分支。 |
| 修改配置 | 报告建议改写 BetterGI 配置、星铁 YAML 和绝区零更新开关。原文同时将星铁 `auto_update` 当作更新键，并把首次使用确认作为运行前置条件。 | 先验证版本对应的字段、默认值、优先级和副作用，优先用已验证的单次参数或隔离副本。原始配置未经授权不得写入。免责声明必须由用户在上游界面本人确认。 |
| 适配接口 | 架构 §1 的 `ValidatePrerequisitesAsync`、`ProcessRunner / IProcessManager` 和 §2.3 的直接 `ProcessStartInfo` 示例与当前实现不一致。 | 对齐 [Contracts.cs](../../src/VIvian.Core/Execution/Contracts.cs) 中的 `PreflightAsync`、`StartAsync`、`IProcessSupervisor` 与 `ProcessLaunchSpec` 参数列表。通过 `IEngineRun` 提交事件、判定、停止和对账。 |
| #11 / #12 | 原文按待开发描述。#11 已限制继承句柄，#12 已提供通用文件跟踪。标准输出来自监督器。 | 以 [证据索引](../evidence/EVIDENCE_INDEX.md) 的合并记录为准。引擎专属解析及双来源关联、去重仍需独立验证。 |
| BetterGI CLI | 报告混合稳定版与重构分支。已固定的 [源码解析器](https://github.com/babalae/better-genshin-impact/blob/9e3a0b008b063e144982818d46f00c00081ffbd2/BetterGenshinImpact/Helpers/CommandLineOptions.cs) 只能证明该快照存在入口。 | 针对对应发行包验证配置名不匹配时的行为、日志标记、自动退出、退出码和提权重启归属。不得把 Pulonia 参数或配置契约移植到未验证的稳定版。 |
| March7thAssistant CLI | [固定源码](https://github.com/moesnow/March7thAssistant/blob/21660281c8e51cf6db69879da8f30a2d1a792bac/utils/cli_entry.py) 区分只读查询和任务提权，并存在 UAC 失败路径。免提权入口不证明执行进程一直处于原 Job。 | 在精确发行包上验证 `--status`、`--list --json`、任务参数、运行 ID、结果结构、退出码和 UAC 拒绝、停止后的清理行为。 |
| OneDragon CLI | [固定源码解析器](https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon/blob/6fd95f11eee82b23bf43d6eb9468043c22e62f8e/src/one_dragon/launcher/exe_launcher.py) 有 `-o`、`-v` 入口。发行包中的 `-c`、`-i`、安装路径限制、更新字段、日志、后端可用性和权限仍需验证。 | 对比发行包实际帮助、包内容和进程树。端口存在、游戏窗口消失及显存变化均不能单独证明任务成功。 |
| 权限及反作弊 | 原文描述自提权、后台控制和全局输入。CI 能验证通用 Job 行为，桌面 UAC 和引擎派生路径需要额外证据。 | 按 [ADR-007](../architecture/ADR-007-M3-PRIVILEGE-BOUNDARY.md) 与 #16 先用无害进程实测。通过正常 UAC 授权。排除反作弊绕过、内存修改和帧率解锁。 |
| UI 与接入顺序 | 全屏截图、自动部署和回滚是设计建议。Gemini 推荐星铁优先，现有索引保留了项目原接入顺序。 | 诊断采集、安装和更新能力需单独审查后启用。保留原文推荐顺序，项目顺序以独立决定及实测门槛为准。 |

当前导入只增加文档及原文图示，并更新索引。实测准入继续采用 [M3 前置清单](../M3_PREREQUISITES.md)。
