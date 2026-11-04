# March7thAssistant 官方发行包审计与验证门槛

日期：2026-11-04。负责人：Codex。对应 [Issue #25](https://github.com/zcwjxy1412/Vivian-cutar/issues/25)。本文件独立于 [PR #24](https://github.com/zcwjxy1412/Vivian-cutar/pull/24)，不更改其代码或验收标准。证据等级：**官方发布元数据 / 固定 tag 源码 / 实际资产待下载**。本轮没有执行上游程序、游戏或账号操作。

## 固定版本与官方资产

[GitHub Releases `latest` API](https://api.github.com/repos/moesnow/March7thAssistant/releases/latest) 在本次读取时返回稳定版 [`v2026.10.31`](https://github.com/moesnow/March7thAssistant/releases/tag/v2026.10.31)，`prerelease: false`，发布于 2026-10-31 11:47:54 UTC；[tag ref](https://api.github.com/repos/moesnow/March7thAssistant/git/ref/tags/v2026.10.31) 指向完整提交 `1e77d594ff4f887ea5b62e2c191c2b983cea1903`。发布页显示该提交的 GitHub verified 签名；这**不是**对下载 EXE 的 Authenticode 签名验收。

| 正式发行资产 | 字节数（发布 API） | GitHub 资产 `digest`（完整 SHA-256） | 用途 / 证据 |
|---|---:|---|---|
| [`March7thAssistant_full.7z`](https://github.com/moesnow/March7thAssistant/releases/download/v2026.10.31/March7thAssistant_full.7z) | 474333248 | `1fcaa1934e3d30d598ce7863756c49b9e9fb4f4fbb759066a62f52541a39dee0` | 完整包元数据，未读取字节 |
| [`March7thAssistant_full.zip`](https://github.com/moesnow/March7thAssistant/releases/download/v2026.10.31/March7thAssistant_full.zip) | 719711875 | `e03aaad4ea27c70f30bf5241ab3bfac8894de7675ddd30a9aef7f1e551af9985` | 完整包元数据，未读取字节 |
| [`update.7z`](https://github.com/moesnow/March7thAssistant/releases/download/v2026.10.31/update.7z) | 163523697 | `4f9ba139ee485aac770e7498551885aa1ae821c3db019962c0c3e4dc84e097c6` | 更新包，不用于新安装识别 |

GitHub 连接器能读取资产元数据，但不能下载二进制；本环境访问下载 URL 返回 404。因此没有对实际归档独立计算哈希、列出目录或检查包内签名。**资产实际内容及 `m7a.exe` 是否随这个精确归档发布仍为 Unknown。** 官方网站 [m7a.top](https://m7a.top/) 标明 Windows 10/11 64 位与免费 GitHub 完整包；这不是对某台机器的可运行性证明。

## 源码与构建清单能证明什么

| 调查项 | 官方来源 | 本轮结论 / 剩余未知 |
|---|---|---|
| 预期包结构 | [固定 tag 构建工作流](https://github.com/moesnow/March7thAssistant/blob/v2026.10.31/.github/workflows/1.build_release.yml) 将 PyInstaller 生成的 Assistant、Launcher、Updater、`assets/`、README、AGENTS.md 和第三方资源并入 `March7thAssistant_full/` 后打包；[spec](https://github.com/moesnow/March7thAssistant/blob/v2026.10.31/March7th%20Assistant.spec) 同一 `COLLECT` 中列出 `March7th Assistant` 与 `m7a` | **预期结构**，未对真实归档核查，运行时依赖与附带资源的实际列表 Unknown |
| CLI 是否存在 | [固定 tag spec](https://github.com/moesnow/March7thAssistant/blob/v2026.10.31/March7th%20Assistant.spec) 定义 `m7a`（`uac_admin=False`）与任务 EXE（`uac_admin=True`）；[随包说明源码](https://github.com/moesnow/March7thAssistant/blob/v2026.10.31/assets/agent/skills/march7th-assistant/SKILL.md) 指向 `m7a.exe` | 源码与官方说明支持该入口；真实包内文件、签名和运行行为 Unknown |
| CLI 参数 | [固定 tag `main.py`](https://github.com/moesnow/March7thAssistant/blob/v2026.10.31/main.py) 定义 `--help`、`--version`、`--status`、`--list --json`、`--follow`、`--control` 等；[官方随包说明](https://github.com/moesnow/March7thAssistant/blob/v2026.10.31/assets/agent/skills/march7th-assistant/SKILL.md) 给出查询调用 | 有官方来源，不代表发行包在本机上已经通过参数、超时、输出和副作用验收 |
| 版本查询 | `main.py` 的 `--version` 调用 [固定 tag `utils/cli_entry.py`](https://github.com/moesnow/March7thAssistant/blob/v2026.10.31/utils/cli_entry.py) `read_version()`，读取 [`assets/config/version.txt`](https://github.com/moesnow/March7thAssistant/blob/v2026.10.31/assets/config/version.txt)（tag 中为 `v2026.10.31`） | 源码级版本接口明确；实际资产中的文件值及 stdout/exit code Unknown |
| 只读状态 / 任务列表 | `--status` 源码读取配置和最近运行记录；`--list --json` 输出任务 `id` / `name` 列表 | 查询不运行任务，但 `collect_status()` 的 `_dir_writable()` **会在安装目录创建并删除临时文件**；严格无写入的“只读”契约未成立。只可在隔离副本中测量实际副作用，不应对用户原安装直接运行 |
| 结果与进程 | [固定 tag `utils/runs.py`](https://github.com/moesnow/March7thAssistant/blob/v2026.10.31/utils/runs.py) 将退出码 0 映射为 `status: success`，保存运行编号、任务、PID/创建时间和日志；未提权入口通过 UAC 新建管理员进程后跟随日志 | 上游记录可用于关联，但 0 / `success` 不能单独证明 VIvian 的目标任务完成。UAC 分叉可能脱离 VIvian Job；归属、日志写入失败、崩溃、停止和清理均待实测 |
| 校验与签名 | 发布 API 提供三份完整 SHA-256；tag 提交已签名 | 下载资产独立哈希、EXE Authenticode、发布物与源码构建对应性 Unknown |

特别注意：源码的状态函数写临时文件，上游 `RunSession._save()` 出错时会吞掉异常，`_status_for_exit_code(0)` 会标记 `success`。设计适配器时，应同时核对**本次运行编号、目标任务、终止记录、进程归属和清理证据**；记录缺失、输出冲突或进程仍运行时保持未知并阻断，不能以退出码 0 代替完成证据。

## 可重复验证方案与测试分层

本分支的 `March7thPackageEvidenceTests` 对**合成 ZIP**验证完整 SHA-256、预期入口、版本文件和路径穿越拒绝；不运行 EXE。Windows CI 通过仅证明这组离线门禁测试本身，不能替代官方归档实测。

| 阶段 | 测试输入与步骤 | 通过门槛 | 当前状态 |
|---|---|---|---|
| 发行包识别、版本解析 | 从上述官方 URL 取得完整 ZIP，记录原始文件大小；独立 SHA-256 与发布 API 比对；只读枚举 ZIP 目录、检查异常路径、`m7a.exe`、主 EXE、Launcher、`assets/config/version.txt` 和版本值 | 哈希、路径、文件和版本全部一致；保留原始清单 | **Pending：未取得二进制** |
| CLI 参数与错误输入 | 只在哈希匹配的**隔离测试副本**执行 `--help`、`--version`、`--list --json`、`--status` 和一个无效参数，分别记录参数、退出码、stdout/stderr 与超时；配置目录前后哈希、文件列表、临时文件与进程数 | 只读查询不启动游戏/提权；JSON 可解析；已知写入副作用可限定在副本且在原安装禁用 | **Pending** |
| stdout/stderr 与版本关联 | 用同一完整哈希启动每条查询，逐项保存原始 UTF-8 字节、解码结果和启动前后版本文件；测试空输出、坏 JSON、乱码、超时 | 不能因“退出码 0”接受空/冲突结果 | **Pending** |
| 生命周期与异常退出 | FakeEngine 验证进程树、根进程退出、强制结束、日志中断和隔离；真实 `m7a.exe` 查询仅在无游戏隔离环境核对是否额外启动进程 | PID/创建时间/Job 成员可确认；超时后只终止本次拥有的 Job | FakeEngine 已覆盖部分；**真实发行包 Pending** |
| 任务结果与清理确认 | 将上游本次运行编号映射 VIvian RunId，核对目标任务、终止 JSON、日志证据、错误与取消；等待所有相关进程清理确认；故障注入缺少 JSON、退出码 0、冲突状态、锁不可用、UAC 拒绝 | 未确认一律 `Indeterminate` / `CleanupState.Unconfirmed` 并阻断下一任务 | **Pending：运行任务及桌面 UAC 需另行授权** |

后续使用真实发行包验证时，应将下载 UTC 时间、资产 ID、URL、独立哈希、机器 OS 版本、命令、原始输出、文件前后摘要、PID/创建时间和测试限制补录在本文件。不得提交账号、截图、私人配置或原始完整日志中的个人数据。
