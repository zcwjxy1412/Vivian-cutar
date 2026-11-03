# M3 接管证据索引（2026-11-03）

本索引记录**源码可观察事实**与仍需 Windows 发行包/实机验证的事项。源码出现参数或方法不代表发行包可用，也不代表一次游戏日常已完成。生产适配器只依据完成实测的版本规则实现。

## VIvian 基线

- main @ `2841b286ffe999a1f2074e21810abb14ea7090a8`：M2 模拟内核、#12 通用日志跟踪器、#11 子进程句柄白名单。
- [#13 Windows validation](https://github.com/zcwjxy1412/Vivian-cutar/actions/runs/41689138828)：165 通过，0 失败，0 跳过；合并为 `3e3138e`。
- [#14 Windows validation](https://github.com/zcwjxy1412/Vivian-cutar/actions/runs/41689958754)：166 通过，0 失败，0 跳过；合并为 `2841b28`。
- 以上是模拟程序和工程测试，**不是**三款游戏的运行证明。

## 上游源码快照

| 引擎 | 固定源码 HEAD | 已核实的源码入口 | 尚不能据此宣称 |
|---|---|---|---|
| BetterGI | [`9e3a0b008b063e144982818d46f00c00081ffbd2`](https://github.com/babalae/better-genshin-impact/tree/9e3a0b008b063e144982818d46f00c00081ffbd2) | [CommandLineOptions.cs](https://github.com/babalae/better-genshin-impact/blob/9e3a0b008b063e144982818d46f00c00081ffbd2/BetterGenshinImpact/Helpers/CommandLineOptions.cs) 解析 `startOneDragon`、`--startGroups` 和 `--pulonia-run`；[ApplicationHostService.cs](https://github.com/babalae/better-genshin-impact/blob/9e3a0b008b063e144982818d46f00c00081ffbd2/BetterGenshinImpact/Service/ApplicationHostService.cs) 处理命令入口 | 发行版参数、完成证据、退出码与 Job 内进程树均待验证 |
| March7thAssistant | [`21660281c8e51cf6db69879da8f30a2d1a792bac`](https://github.com/moesnow/March7thAssistant/tree/21660281c8e51cf6db69879da8f30a2d1a792bac) | [main.py](https://github.com/moesnow/March7thAssistant/blob/21660281c8e51cf6db69879da8f30a2d1a792bac/main.py) 定义 `--status`、`--list --json`、`--follow`、`--control`；普通权限运行任务时调用 `run_task_elevated` | `m7a.exe` 在对应发行包的实际行为、提权进程身份和清理、运行 JSON 与结果判定均待验证 |
| ZenlessZoneZero-OneDragon | [`6fd95f11eee82b23bf43d6eb9468043c22e62f8e`](https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon/tree/6fd95f11eee82b23bf43d6eb9468043c22e62f8e) | [launcher_base.py](https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon/blob/6fd95f11eee82b23bf43d6eb9468043c22e62f8e/src/one_dragon/launcher/launcher_base.py) 定义通用参数；[win_exe/launcher.py](https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon/blob/6fd95f11eee82b23bf43d6eb9468043c22e62f8e/src/zzz_od/win_exe/launcher.py) 转交给 Python 启动器 | 发行包中入口和后端可用性、进程树、完成/失败信号及权限均待验证 |

这三个 HEAD 是本轮读取远程仓库时的快照；与用户电脑上的安装版本**尚未对应**。需要发行包名称、官方 URL、SHA-256 及源码对应关系，才能建立兼容性规则。

## 阻断项与执行顺序

1. `C1` 权限与进程身份：单独 `spike/m3-privilege-boundary` 分支，用**无害测试进程**验证普通权限、UAC 拒绝、执行器退出、UI 崩溃和 Job 清理；先提交设计决策，不能让 UI 默认提权。
2. `C4` 外部进程识别：在固定版本和路径后设计，只阻断冲突，不按名称结束其他程序。
3. `B1–B12`：每款引擎单独记录版本、发行包哈希、原始命令与输出、前后摘要、进程树、权限、完成/失败/取消证据和限制。顺序：BetterGI → March7thAssistant → OneDragon；若接口不可验证，显示“不支持统一控制”。
4. `D1–D3`：DeepSeek 和 Gemini 的独立评审及 Critical/High 闭环仍待记录。
5. 逐款 `feature/m3-bettergi-adapter`、`feature/m3-m7a-adapter`、`feature/m3-onedragon-adapter`，各自 PR 和 Windows CI；三款均经实机验收后再验证串行队列。

参见 [M3_PREREQUISITES.md](../M3_PREREQUISITES.md) 的 A1–A5 与 B1–B12。当前没有测试电脑授权或发行包实测证据；**生产适配器、真实游戏测试和“已完成日常”宣称均未开始**。