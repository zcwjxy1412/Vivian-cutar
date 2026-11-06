# M4-C2：ZenlessZoneZero-OneDragon 适配器（绝区零）

状态：**已实现生命周期代码，真实执行保持关闭**。基线 `main@aa0db83`，与 BetterGI 分支互不依赖。上游固定版本：`v2.6.0`（提交 `5f2e473bbc5b100ae2ea482dd25be0277bbca87c`）。

## 1. 实现内容

| 文件 | 作用 |
|---|---|
| `src/VIvian.Infrastructure/Engines/OneDragon/OneDragonContract.cs` | 固定版本事实与来源；`LaunchContractVerified`、`CompletionSignalsVerified` 均为 `false` |
| `OneDragonTaskRequest.cs` | 白名单参数：`-o`（必有）、`-c`（可选）、`-i n[,n…]`（0–99，最多 8 个，不重复） |
| `OneDragonInstallationInspector.cs` | 只读安装检查：只接受集成启动器；文档路径规则改为**拒绝**；要求 `src`、`.runtime` |
| `OneDragonOutput.cs` | 启动器输出的标记分类与本次运行证据 |
| `OneDragonRun.cs` | `IEngineRun`：区分启动器寿命与工作寿命、停止、对账 |
| `OneDragonAdapter.cs` | `IGameAdapter`、单安装运行槽、外来进程检测（只检测，不操作） |

没有修改共享接口、调度器、执行策略、March7thAssistant 或 WPF 文件。

## 2. 固定源码核对结果（证据分类）

| 事实 | 分类 | 依据 |
|---|---|---|
| 参数 `-o/--onedragon`、`-c/--close-game`、`-i/--instance <字符串>`、`-s [秒]`、`--hibernate`、`--sleep`、`-v`、`-h` | 固定源码确认（B） | `launcher_base.py`、`exe_launcher.py` |
| `-c/-s/-i` 等不带 `-o` 时打印错误并退出 1 | B | `exe_launcher.py` |
| **非管理员时**：`pyuac.runAsAdmin(sys.argv, wait=False)` 后立即 `sys.exit(0)`——启动器以 0 退出，工作在作业外的提权副本中进行 | B | `exe_launcher.py` |
| **管理员时**：一条龙代码在启动器进程**内**运行（集成启动器无独立工作进程） | B | `runtime_launcher.py`、`zzz_od/win_exe/runtime_launcher.py` |
| 每次运行先执行代码同步；`auto_update_code` 默认 **True**；首次运行（本地分支不存在）无条件联网同步 | B | `runtime_launcher.py._sync_code`、`env_config.py` |
| 致命异常时弹出模态错误框后退出 1（会一直等待用户点击） | B | `runtime_launcher.py._show_fatal_error` |
| 控制台格式 `[时:分:秒.毫秒] [文件 行] [级别]: 消息`；文件日志 `.log\log.txt` **没有 PID** | B | `log_utils.py` |
| 启动器为控制台程序（`console=True`），标准流可被 VIvian 读取 | B | `deploy/OneDragon-RuntimeLauncher.spec` |
| 一条龙结束行 `指令[ 一条龙 ] 执行成功 …` / `执行失败 …` | B（通用 Operation 日志，未针对 CLI 路径实测） | `operation.py`、`application_const.py` |
| 进程退出码与一条龙结果无关（正常返回即 0） | B | `application_launcher.run_application` |
| 安装路径全英文、无空格 | **仅文档（C）**；固定源码中未发现检查 | one-dragon.com 快速开始 |
| `-i` 编号从 0 还是 1 开始 | **未验证** | — |
| 发行包与程序文件身份 | **未验证** | 摘要只来自研究报告 |

## 3. 判定与清理规则

- 证据**只取自 VIvian 启动的启动器自身的标准输出**。文件日志无 PID，无法归属到本次运行，因此不作为证据，也不读取。
- 成功需同时满足：出现 `OneDragon 集成启动器 v2.6.0`（证明工作在本进程内且版本一致）；出现 `未开启代码自动更新，跳过`；没有 `正在检查代码更新...`/`首次运行，正在同步代码仓库...`；指定实例时回显 `指定运行实例: [1, 2]` 完全一致；恰好一条 INFO 级 `指令[ 一条龙 ] 执行成功`；无 ERROR/CRITICAL、Traceback、参数错误、`未找到有效的实例`；启动器自行以 0 退出；完成契约已验证。当前契约未验证，**所有结果至多为 Indeterminate**。
- **启动器先于工作者退出**：没有看到本进程内的开始行就结束（典型为非管理员时交给提权副本）→ 判定 Indeterminate，清理 **Unconfirmed**，即使作业已空；运行槽在本次会话内保持占用。
- VIvian 在看到开始行之前终止启动器，也按 Unconfirmed 处理（无法排除它已交出工作）。
- 启动器退出后作业内仍有进程（例如由它启动的游戏）：对账时终止**本次作业内**的进程，这是现有 `ReconcileAsync` 语义；建议真实运行使用 `-c`。
- 正常停止：命令行模式没有可绑定到本次运行的停止通道，不发送任何热键或消息，返回 `graceful_unavailable`；紧急停止只终止本次作业内的进程。
- 为让 UTF-8 读取器正确解码，启动时设置 `PYTHONIOENCODING=utf-8`、`PYTHONUNBUFFERED=1`（冻结运行时是否遵守未验证；若不遵守，标记无法匹配，结果保持 Indeterminate）。刻意不设置 `PYTHONUTF8`，以免改变 OneDragon 读写自身文件的方式。

## 4. 执行门

与 BetterGI 相同的三层：调度器预检前阻断非模拟适配器；`PreflightAsync` 返回 `real_adapter_unverified`；`StartAsync` 先检查门并抛出 `EngineStartException(nothingStarted: true)`。生命周期只能经内部 `LaunchAsync` 进入（仅测试使用）。

## 5. 仍需 Windows 桌面验证（Codex）

1. 官方 `WithRuntime-Full.zip` 实包摘要与目录结构（`OneDragon-RuntimeLauncher.exe`、`.runtime`、`src`、`.git` 是否随包提供）。
2. 管理员权限 VIvian 下 `-o` 是否确实在启动器进程内运行、没有子进程离开作业；普通权限下是否以 0 退出并出现 UAC（预期：VIvian 记录 Unconfirmed 并阻断）。
3. 冻结运行时是否遵守 `PYTHONIOENCODING`；控制台输出的实际格式与中文是否正确解码。
4. 代码自动更新：默认安装会联网同步代码。**用户必须先在 OneDragon 界面中关闭“代码自动更新”**；首次运行前本地仓库是否已存在。VIvian 不修改 `config\env.yml`；当前仅通过输出中的“跳过”行确认。
5. 一条龙正常结束、子任务失败、实例无效、致命异常（模态弹窗）时的实际输出与退出码。
6. `-i` 编号基数，以及 `-c` 关闭游戏后游戏进程是否在作业内。

## 6. 需要 Codex 决定的事项

- 是否允许在预检中**只读**检查 `config\env.yml` 的 `auto_update_code` 一个键（现有策略是不读取配置内容）。不允许则保持现状：以运行输出为准，未确认跳过更新即不判成功。
- 两个适配器各自复用 `FakeEngineAdapter.ForeignProcessIds` 做只检测的进程查找，以免放宽 `ProcessSafetyGuardTests`。若希望拆出独立探测器，需要同时调整该测试的允许清单。
