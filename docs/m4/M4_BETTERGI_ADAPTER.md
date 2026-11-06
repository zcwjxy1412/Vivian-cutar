# M4-C1：BetterGI 适配器（原神）

状态：**已实现生命周期代码，真实执行保持关闭**。基线 `main@aa0db83`。上游固定版本：标签 `0.66.0`（提交 `0af68c28f6b0e714d5304625ce644736d508022e`，即 VIvian 记录的 `0af68c2`；上游标签名没有 `v` 前缀）。

## 1. 实现内容

| 文件 | 作用 |
|---|---|
| `src/VIvian.Infrastructure/Engines/BetterGi/BetterGiContract.cs` | 固定版本事实与证据级别；`LaunchContractVerified`、`CompletionSignalsVerified` 均为 `false` |
| `BetterGiTaskRequest.cs` | 白名单参数构造：`startOneDragon`、`startOneDragon <配置名>`、`--startGroups <组…>` |
| `BetterGiInstallationInspector.cs` | 只读安装检查：复用 `EngineReadinessInspector`，另查 `Assets`/`GameTask` 文件夹与程序文件中的构建提交号 |
| `BetterGiLogSource.cs` | 基于现有 `LogFileTailer` 的增量日志读取；启动前建立基线，同时跟踪当天与次日的日志文件 |
| `BetterGiLog.cs` | Serilog 多行记录解析、标记分类、按 PID + 实例启动时间的归属判断 |
| `BetterGiRunEvidence.cs` | 本次运行的证据汇总，最终交给共享的 `CompletionEvidenceEvaluator` |
| `BetterGiRun.cs` | `IEngineRun`：事件、判定、停止、对账 |
| `BetterGiAdapter.cs` | `IGameAdapter`、单安装运行槽（防重复运行）、外来进程检测（只检测，不操作） |

没有修改任何共享接口、调度器、执行策略、March7thAssistant 或 WPF 文件。

## 2. 证据分类

| 能力 | 分类 | 依据 |
|---|---|---|
| `startOneDragon` / `startOneDragon <名>` / `--startGroups` 的解析 | 固定源码确认（B） | `Helpers/CommandLineOptions.cs`：参数会被 `Trim()`；`--instance`、`--restart-from-pid` 及其值在任意位置被剔除；`arg1.Contains("startOneDragon")` |
| 指定配置名不存在时仍运行当前选中配置 | 固定源码确认（B） | `OneDragonFlowViewModel.OnLoaded`：只记 `未找到，请检查。` 然后执行 |
| 完成行 `一条龙和配置组任务结束` 仅在正常结束路径写入日志 | 固定源码确认（B） | 取消路径只发通知、写 `任务被取消，退出执行` |
| 子任务异常被吞掉后仍可能写完成行、以 0 退出 | 固定源码确认（B） | `TaskRunner.RunThreadAsync` 以 ERR 记录异常消息后继续 |
| 日志头含 `Primary:S{会话}:P{PID}:T{实例启动毫秒}` | 固定源码确认（B） | `App.xaml.cs` 文件模板 + `InstanceContext.cs` |
| 提权自重启退出码 **553**（研究报告写的是 559） | 固定源码确认（B） | `RuntimeHelper.RestartAsElevated`：`'r'+'u'+'n'+'a'+'s'` |
| 单实例冲突或缺少 `Assets`/`GameTask` 时退出码 0xFFFF | 固定源码确认（B） | `RuntimeHelper.CheckSingleInstance` / `CheckIntegration` |
| 日志文件名 `better-genshin-impact{yyyyMMdd}.log` | 源码确认基础名与按日滚动（B）；展开后的文件名是 Serilog 行为，**未实测** | `App.xaml.cs` |
| 发行包与程序文件身份 | **未验证** | 没有官方程序文件摘要；7z 摘要只来自研究报告 |
| 启动、进程归属、停止、完成证据 | **未验证** | 需要 Windows 桌面实测 |

没有任何能力被标记为 Verified。

## 3. 判定与清理规则

- 成功必须同时满足：日志记录属于本次运行的根进程（`Primary`、PID 相同、实例启动时间不早于根进程创建时间减 2 秒）；先出现恰好一次 `启用一条龙配置：…`（指定配置名时必须一致），之后恰好一次 `一条龙和配置组任务结束`；没有 `未找到，请检查。`、取消、无任务、ERR/FTL、`执行配置组任务时失败` 等记录；根进程自行以 0 退出；并且固定版本的完成契约已验证。当前契约未验证，所以**所有结果至多为 Indeterminate**。
- 只看消息首行并精确匹配，异常堆栈中的文字不会被当作完成。
- 历史日志：启动前记录文件长度作为基线；即使基线失效（如文件被替换），PID + 实例启动时间也会排除旧记录。
- `--startGroups` 没有已知完成记录，永远不会被判为成功。
- VIvian 终止进程后的退出码不作为引擎证据（不会被误判为 Failed）。
- 退出码 553，或运行期间有本次运行之外的 BetterGI 进程写日志：判定 Indeterminate，清理 **Unconfirmed**，即使作业已空；该安装的运行槽在本次会话内保持占用。
- 正常停止：BetterGI 没有可绑定到本次运行的外部停止通道，不发送热键或窗口消息，返回 `graceful_unavailable`，由调度器升级为紧急停止。紧急停止只终止本次运行作业内的进程。

## 4. 执行门

1. 调度器在预检前阻断所有非模拟适配器（未改动）。
2. `PreflightAsync` 在 `RealExecutionPolicy.BuildPermitsRealExecution && BetterGiContract.LaunchContractVerified` 为假时返回 `real_adapter_unverified`。
3. `StartAsync` 先检查同一门，失败即抛出 `EngineStartException(nothingStarted: true)`，不创建任何进程、不打开日志。
4. 生命周期代码只能经内部方法 `LaunchAsync` 进入，仅测试使用。

## 5. 仍需 Windows 桌面验证（Codex）

1. 官方 `BetterGI_v0.66.0.7z` 实包 SHA-256 与目录结构（`BetterGI.exe`、`Assets`、`GameTask`、`log`），以及 `BetterGI.exe` 的 ProductVersion 是否含 `+0af68c2…`。
2. 普通权限 VIvian 启动 BetterGI 时是否以 553 退出并脱离作业（预期如此：此时清理必须为 Unconfirmed）；管理员权限 VIvian 下是否不再自重启、所有进程留在作业内。
3. 实际日志文件名、编码、记录格式，`BgiInstance` 中的 PID 是否等于作业内根进程 PID。
4. 一条龙在 `CompletionAction = 关闭游戏和软件` 下：完成行之后进程是否自行退出、退出码是否为 0。其他完成动作下 BetterGI 不会退出，运行只能以超时结束（Indeterminate）。
5. 指定配置名不存在、配置组任务失败、子任务异常、手动取消时的实际日志。
6. 紧急停止（作业终止）后游戏进程是否也在作业内，按键是否残留。
7. 每次启动都会改写 `HKCR\bettergi` 协议指向（级别 B）：只能在用户正式安装目录上实测，且需用户明确授权。

以上全部通过并有证据文件后，才能把 `BetterGiContract` 中对应标志改为 `true`；`RealExecutionPolicy` 的开放由 Codex 统一决定。
