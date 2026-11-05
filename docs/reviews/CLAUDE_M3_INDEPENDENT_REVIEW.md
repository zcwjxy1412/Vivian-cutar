# VIvian M3 独立代码审查报告（Claude）

审查日期：2026-11-04　审查人：Claude（独立审查，未修改 Codex 的实现）

> 证据分三类，本报告严格区分，不互相替代：
> **[CI]** GitHub Windows CI 的结果（同权限的无害进程）；**[本地]** 审查人在 Linux 上的构建、测试与复现（Windows 专用测试会显示为跳过）；**[阅读]** 只基于源码阅读得出的判断。
> 本次没有进行任何桌面 Windows、UAC 或真实引擎/游戏的验证。

## 1. 仓库与提交基线

| 项目 | 值 |
|---|---|
| 仓库 | https://github.com/zcwjxy1412/Vivian-cutar （公开） |
| `main` | `5c49461`（与授权文件一致；审查期间未变化） |
| 三个 PR 的合并基点 | 均为 `5c49461` |
| 本地工具链 | .NET SDK 10.0.403（与 `global.json` 一致） |

## 2. 被审查的 PR

| PR | 分支 | Head | 目标 | 可合并 | CI（PR 触发） |
|---|---|---|---|---|---|
| [#20](https://github.com/zcwjxy1412/Vivian-cutar/pull/20) | `fix/m3-scheduler-hardening` | `32ff2d4` | main | 是 | [41784504262](https://github.com/zcwjxy1412/Vivian-cutar/actions/runs/41784504262)：170/170 通过 |
| [#23](https://github.com/zcwjxy1412/Vivian-cutar/pull/23) | `spike/m3-privilege-validation` | `748c088` | main | 是 | [41785624903](https://github.com/zcwjxy1412/Vivian-cutar/actions/runs/41785624903)：168/168 通过 |
| [#24](https://github.com/zcwjxy1412/Vivian-cutar/pull/24) | `feature/m3-m7a-readonly` | `3b0455a` | main | 是 | [41785165684](https://github.com/zcwjxy1412/Vivian-cutar/actions/runs/41785165684)：169/169 通过 |

CI 全部为绿色，但这只说明“在 CI 上那一次运行通过”。下文 M1 显示，PR #20 里有一项测试会偶发失败。

## 3. PR #20：调度器可靠性

### 3.1 改动内容 [阅读]

1. `QueueScheduler.RunAsync`：把创建停止信号、注册令牌、创建 `stopToken` 的代码移入 `try`，保证 `_running` 在 `finally` 中一定复位；`Exchange` 改为 `CompareExchange`。
2. `Judge`：适配器声称 Success 时，本次运行的证据只要有两种以上（例如“完成”和“失败”同时出现），就判为 Indeterminate（`conflicting_evidence`）。
3. `RunPresentation.Summary`：Blocked 且清理未确认时，摘要显示“已阻断”，不再显示“成功”。

### 3.2 新测试能否真正发现问题 [本地]

做法：把 PR #20 的 4 项新测试放到 `main` 修复前的 `QueueScheduler.cs` 和 `RunPresentation.cs` 上运行。

| 测试 | 修复前 | 修复后 | 结论 |
|---|---|---|---|
| `AdapterClaimingSuccessWithConflictingEvidenceIsIndeterminate` | **失败** | 通过 | 有效：证实 `main` 上确实存在“矛盾证据被判为成功”的缺陷，PR 修复正确 |
| `UnconfirmedCleanupSummaryLeadsWithBlockedEvenIfTaskReportedCompletion` | **失败** | 通过 | 有效 |
| `DisposedCallerTokenDoesNotLeaveSchedulerRunning` | 通过 | 通过 | 不能证明修复，见 L1 |
| `EmergencyStopDuringStartDoesNotLoseRequestOrStartNextGame` | 通过 | 通过（偶发失败） | 用于防回归，但存在偶发失败，见 M1 |

### 3.3 关于“已释放令牌会抛异常”的更正

本地 .NET 10.0.403 的实际行为与 Codex 的结论一致：在已经 `Dispose` 的 `CancellationTokenSource` 的令牌上调用 `Register` **不会抛异常**，第一个队列正常完成。因此，原先报告的 `ObjectDisposedException` **不作为已确认缺陷**。把初始化代码移进 `try` 属于正确的防御性加固，但在当前运行时上找不到会触发它的路径。

### 3.4 必答问题

| # | 问题 | 结论 | 证据 |
|---|---|---|---|
| 1 | 调度器状态会不会被永久锁住？ | **会**：适配器的某个调用永远不返回、又忽略取消时 | H1，复现 R1 [本地] |
| 2 | 取消请求会不会丢失？ | 通过令牌取消**不会丢失**。`EmergencyStop()` 在队列开始前的极短窗口里可能丢失，这一点 M2 已有文档说明，界面使用的是令牌方式。但取消传到适配器**有延迟**（M1） | M1；R3 通过 [本地] |
| 3 | 清理未确认时，下一任务会不会启动？ | 在同一个队列内**不会**。但在同一会话中，如果写入“清理未确认”时数据库失败，**下一个队列会启动** | H2，复现 R2 [本地] |
| 4 | 矛盾证据会不会被判为成功？ | 修复后**不会** | 3.2 表 [本地] |
| 5 | 意外异常会不会绕过清理逻辑？ | **不会**：兜底处理会关闭为 Blocked + Unconfirmed，并释放运行资源。适配器返回空证据列表时也是保守处理 | R4 通过 [本地] |
| 6 | 现有测试是否准确反映生产行为？ | **部分不能**：有 2 项新测试在修复前也通过（L1）；1 项偶发失败（M1）；没有覆盖“适配器不配合”和“日志写入失败” | 3.2、R1、R2 |

## 4. PR #23：Windows 权限与 Job Object

### 4.1 改动内容 [阅读]

只改测试和文档，**没有改产品代码**：FakeEngine 新增 `probe-identity` 场景，`SupervisorTests` 新增 2 项测试，并新增权限验证记录和 M3 任务看板。

### 4.2 三类证据分开评估

| 类别 | 本 PR 提供了什么 | 评估 |
|---|---|---|
| **① CI 无害进程（同权限）** | 父子进程用户 SID 一致；子进程处于某个 Job 中；同名外部进程在 VIvian 的 Job 被终止后仍然存活 | 同名隔离测试**有效**：如果按名称结束进程，测试会失败。“属于 Job”这个断言证明力不足，见 M2 |
| **② 桌面交互式 UAC** | 只有验证步骤 | **未执行，Pending**（用户尚未授权）。PR 和文档都如实标注 |
| **③ 真实第三方引擎** | 无 | **未执行，Pending** |

### 4.3 进程安全核查 [阅读]

- **按名称结束进程**：产品代码中只有 `TerminateJobObject`（只作用于本次运行的 Job）和 `TerminateProcess`（只用于刚创建、仍处于挂起状态、尚未放入 Job 的那个进程句柄），没有按名称结束进程的路径。`ProcessSafetyGuardTests` 会扫描源码。**结论：无法因为同名而结束无关的用户进程。**
- **PID 复用**：进程监管持有 `CreateProcess` 返回的进程句柄和 Job 句柄，不按 PID 查找或结束进程；崩溃重启后的对账也不会按 PID 结束进程。**结论：不受 PID 复用影响。**
- **句柄继承**：`main` 上已经使用 `PROC_THREAD_ATTRIBUTE_HANDLE_LIST`（来自已合并的 #14），只继承两个管道句柄。本 PR 没有改动这部分。
- **提权子进程**：需要管理员权限的程序被 `CreateProcess` 直接启动时会失败，记为启动失败、`nothingStarted`。但如果引擎内部通过 UAC 或启动器另行拉起进程，新进程**很可能不在 Job 内**，这正是 U1 的风险。

## 5. PR #24：March7thAssistant 只读发现

### 5.1 只读保证 [阅读]

- `March7thReadOnlyInspector` 没有任何启动进程的调用；只以只读方式打开 `m7a.exe` 计算 SHA-256，用 `FileVersionInfo` 读取版本资源（以数据方式读取，不执行代码）。
- `CanQueryStatus` 和 `CanQueryTasks` 写死为 `false`；兼容性枚举只有 `NotInstalled / MissingCli / Unverified`，**不存在“已验证”这个值**，界面不可能显示“兼容”。
- 界面文字是“发行包未验证，暂不支持查询”，没有声称可以执行任务或已接入 CLI。**范围表述准确。**

### 5.2 上游引用核查 [本地，只读 git]

- `git ls-remote` 显示：上游标签 `v2026.10.31` 指向 `1e77d594ff4f887ea5b62e2c191c2b983cea1903`，与证据文档中的 `1e77d59` **一致**。
- 在发行标签对应的提交 `1e77d59` 中，`main.py` 确实定义了 `--status`、`--list`、`--json`；`utils/cli_entry.py` 中有 `run_task_elevated`。这两个文件在 `1e77d59` 和文档引用的 `21660281` 之间**没有差异**。
- 以上只是**源码层面**的事实，不代表发行包中的 `m7a.exe` 行为相同。PR 也没有这样声称。

## 6. 跨 PR 兼容性分析

| 检查项 | 结果 |
|---|---|
| 依次合并 #20 → #23 → #24 [本地] | 代码无冲突；**`docs/evidence/EVIDENCE_INDEX.md` 有文本冲突**（#23 与 #24 在同一位置新增章节，见 L8） |
| 组合构建 [本地] | 0 warning / 0 error |
| 组合测试 [本地] | 共 175 项：149 通过、21 跳过（Windows 专用）、5 失败。失败的是 4 项 `LogFileTailerTests`（L9，Linux 专属问题）和 1 项 M1 偶发失败 |
| SQLite 迁移 | 三个 PR 都没有改动模式；与 M1/M2 数据库兼容 |
| 依赖注入 / 共享契约 | 没有改动 `IGameAdapter` 等契约；PR #24 的检查器是静态类，未注册到 DI |
| 日志与敏感信息 | 没有新增记录路径或配置内容的日志；界面显示的是哈希和版本 |
| ADR | ADR-007 仍为“提案”，与 PR #23 的表述一致 |
| 文档与实现 | 基本一致；L3（索引中写着“待补录”）、M3（PR #24 文档声称的保守路径没有测试覆盖）除外 |

## 7. 安全评估

- **进程终止边界**：安全。只终止本次运行 Job 内的进程，不受同名或 PID 复用影响 [阅读 + CI]。
- **权限边界**：M3 最大的未知数。以普通权限创建的 Job **无法覆盖经 UAC 提权或经启动器拉起的进程**。March7thAssistant 源码中有 `run_task_elevated`，因此它的任务进程脱离 Job 的可能性很高。当前 `FakeEngineRun.ReconcileAsync` 的逻辑是“Job 中进程数为 0 就判为 Confirmed”，**这条规则不能被真实适配器沿用**（U1）。ADR-007 已经正确写明这一约束，但还没有对应的代码或测试。
- **PR #24 的只读性**：安全，但目前缺少防止将来误加入启动进程代码的自动检查（M4）。
- 没有发现读写游戏内存、注入、绕过反作弊或收集凭据的代码。

## 8. 测试覆盖评估

- **有效**：矛盾证据、阻断显示、同名外部进程隔离、M2 已有的 11 个故障场景。
- **证明力不足**：`DisposedCallerTokenDoesNotLeaveSchedulerRunning`（L1）；`HarmlessChildKeepsCallerIdentityAndBelongsToJob` 证明的是“在某个 Job 中”，不是“在 VIvian 的 Job 中”（M2）；PR #24 的“读取期间变动”和“符号链接”两条路径没有测试（M3）。
- **不确定**：`EmergencyStopDuringStartDoesNotLoseRequestOrStartNextGame` 本地运行 6 次失败 2 次（M1）。
- **缺失**：适配器不配合（R1）；写入运行记录失败（R2）；源码中不得启动进程的检查（M4）。

## 9. 发现汇总

### 9.1 已确认缺陷

#### H1（High）适配器调用不返回时，调度器被永久占用，紧急停止失效

- **所属**：M2 基线代码（`main` 上已存在，不是 PR #20 引入的）；对应 PR #20 必答问题 1。
- **位置**：`src/VIvian.Core/Execution/QueueScheduler.cs`（PR #20 head）第 165 行 `PreflightAsync`、第 195 行 `StartAsync`、第 275 行 `await firstRequest`（即 `RequestStopAsync`）、第 290 行 `ReconcileAsync`。这些调用都只把取消令牌传给适配器，调度器自身没有时限。
- **触发条件**：适配器的上述任一调用永远不返回，并且忽略取消。M3 真实适配器在等待外部进程或管道时完全可能出现这种情况。
- **证据 / 复现 [本地]**：复现测试 R1 中，4 个位置分别挂起后，通过令牌发出紧急停止，8 秒后队列仍未结束，`IsRunning == true`。
- **修复建议**：调度器对每个适配器调用**自己施加时限**：与紧急停止信号和超时一起 `WhenAny`，或使用 `WaitAsync(timeout)`。超时后放弃这次调用，释放运行资源（Job kill-on-close），结果记为 Indeterminate 或 Cancelled，清理记为 **Unconfirmed**，然后阻断队列。
- **回归测试**：在上述 4 个位置分别挂起一个忽略取消的适配器，断言队列在规定时间内结束、`IsRunning == false`、清理为 Unconfirmed、后续游戏未启动。

#### H2（High）写入“清理未确认”失败时，同一会话的下一个队列仍会启动

- **所属**：M2 基线代码；对应 PR #20 必答问题 3。
- **位置**：`QueueScheduler.cs`（PR #20 head）第 82～84 行：只依赖 `CountUnresolvedAsync`；`src/VIvian.Infrastructure/RunStore.cs`（main）第 77 行：只统计 `cleanup='Unconfirmed'`。写入失败时只把 `JournalFaulted` 设为 true，然后继续。
- **触发条件**：SQLite 写入最终的 Blocked/Unconfirmed 记录时失败（数据库被锁超过 5 秒、磁盘已满、被杀毒软件锁定、I/O 错误等）。
- **证据 / 复现 [本地]**：复现测试 R2 使用真实的 `LocalDatabase`，只让 Blocked 状态的写入失败。结果：第一个队列返回 `Blocked / cleanup_unconfirmed`、`journalFaulted=True`，数据库里仍是 `Reconciling / Pending`；**第二个队列 `Completed`，下一款游戏被启动（StartCount=1）**。重启后的崩溃对账可以纠正这条记录，但同一会话内的门禁被绕过。
- **修复建议**：① 调度器在内存中记录“本会话中清理未确认的运行”，在用户确认或重启对账之前拒绝新队列，不依赖数据库写入是否成功；② `CountUnresolvedAsync` 把“没有队列在运行时仍处于非终态（phase 不是 Finished/Blocked）的记录”也算作未解决。
- **回归测试**：把 R2 改为断言形式：第二个队列为 Blocked，原因是“有未解决的运行”，并且 `StartCount == 0`。

#### M1（Medium，PR #20）偶发失败的测试；紧急停止传到适配器取消令牌有异步延迟

- **位置**：`QueueScheduler.cs` 第 138 行 `NewSignal()` 使用 `RunContinuationsAsynchronously`；第 75～77 行通过 `ContinueWith(..., ExecuteSynchronously)` 取消 `stopToken`。由于该选项强制所有后续操作异步执行，取消会被排到线程池上。测试 `SchedulerTests.cs` 第 333～339 行在 `EmergencyStop()` 之后立即放行适配器；适配器在 `ScriptedEngine.cs` 第 39 行检查 `ct.IsCancellationRequested` 时可能还看不到取消。
- **证据 [本地]**：PR #20 head 上连续运行 6 次，失败 2 次（`Expected: NotRequired, Actual: Confirmed`）；组合合并后单独运行也失败过。此时安全规则仍然成立（结果是“已取消”，下一游戏未启动），断言的是清理状态。
- **影响**：CI 可能随机变红；对真实适配器来说，紧急停止之后的极短窗口内，适配器仍可能认为“没有被取消”而去启动进程。
- **修复建议**：在 `EmergencyStop()` 和令牌注册回调中**直接同步**调用 `stopToken.Cancel()`（例如把当前队列的 CTS 保存到字段里），不经过异步后续操作。
- **回归测试**：确定性测试：`EmergencyStop()` 返回后，适配器拿到的 `ct.IsCancellationRequested` 必须**立即**为 true；原测试连续运行 50 次不失败。

#### M2（Medium，PR #23）“属于 Job”的测试证明力不足

- **位置**：`tests/VIvian.Tests/Processes/SupervisorTests.cs` 第 80～81 行；`tests/VIvian.FakeEngine/Program.cs` 第 106～107 行。
- **问题**：`IsProcessInJob(GetCurrentProcess(), NULL, ...)` 判断的是“是否处于**任意** Job 中”。如果测试宿主本身已经在某个 Job 中（CI 环境中常见），即使 VIvian 没有把子进程放进**自己的** Job，子进程也会继承宿主的 Job，`job-member=True` 照样成立。此外，`in-job=` 输出的是 API 调用是否成功，不是成员关系，名称容易误导。
- **证据**：[阅读] Windows API 语义。CI 宿主是否处于 Job 中，本次**无法验证**。
- **修复建议**：由父进程用 VIvian 自己的 Job 句柄调用 `IsProcessInJob(子进程句柄, VIvian 的 Job 句柄)`；或者增加对照组：不经过监管器直接启动同一个探针，记录它的 `job-member` 结果。把 `in-job` 改名为 `api-ok`。
- **回归测试**：上述对照测试，并在证据文档中记录对照组的结果。

#### M3（Medium，PR #24）文档声称的保守路径没有测试；I/O 异常没有转为保守结果

- **位置**：`src/VIvian.Infrastructure/Engines/March7thReadOnlyInspector.cs` 第 37 行（重解析点）、第 43～49 行（读取期间变动）、第 43 行 `new FileStream` 没有捕获异常；`docs/evidence/M3_MARCH7TH_READONLY_EVIDENCE.md` 第 13 行声称“符号链接和读取期间变动也按保守状态提示”。
- **问题**：① 重解析点和读取期间变动这两条路径没有测试；② `m7a.exe` 无法读取（拒绝访问、被独占锁定）或在检查期间被删除时，`InspectAsync` 会直接抛异常，`MainViewModel` 第 116 行显示的是不相关的“请检查数据目录权限与磁盘空间”，卡片也保持旧状态。
- **修复建议**：捕获 `IOException` 和 `UnauthorizedAccessException`，返回 `Unverified` 并附上具体说明；补充测试。
- **回归测试**：重解析点（Windows 测试，创建符号链接或硬链接替身）；用 `FileShare.None` 占用文件，模拟拒绝访问；读取期间变动（为测试提供注入点）。

#### M4（Medium，跨 PR）没有自动检查保证发现代码永远不会启动进程

- **位置**：`tests/VIvian.Tests/Processes/ProcessSafetyGuardTests.cs` 第 26～29 行只检查结束进程的写法。
- **问题**：PR #24 的核心承诺是“不运行上游程序”，但目前只靠代码审查保证。将来在 `Engines/` 下加入 `Process.Start` 不会让任何测试失败。
- **修复建议**：新增源码检查：`Process.Start`、`ProcessStartInfo`、`CreateProcess`、`ShellExecute` 只允许出现在 `WindowsProcessSupervisor` 中。
- **回归测试**：上述检查本身，并做一次变异验证。

### 9.2 低严重度问题

| 编号 | PR | 位置 | 问题 | 建议 |
|---|---|---|---|---|
| L1 | #20 | `SchedulerTests.cs` 第 83 行 | `DisposedCallerTokenDoesNotLeaveSchedulerRunning` 在修复前同样通过，只是记录了运行时行为，并不是这次修复的回归测试 | 改名为“记录行为”的测试，或者在测试注释中说明“修复属于防御性，当前运行时下不可触发” |
| L2 | #20 | `QueueScheduler.cs` 第 204 行 | 适配器在被取消时抛出 `OperationCanceledException`（而不是 `EngineStartException(nothingStarted:true)`），会被记为清理未确认并阻断队列。这样做安全，但会给用户带来不必要的确认步骤 | 在 `IGameAdapter` 契约注释中写明：进程创建之前因取消而失败时，应抛出 `EngineStartException(..., nothingStarted: true)` |
| L3 | #23 | `docs/evidence/EVIDENCE_INDEX.md` 第 35 行 | 写着“CI 运行结果待补录”，但同一个 PR 的验证记录里已经填写了 | 更新这一行 |
| L4 | #23 | `FakeEngine` `probe-identity` | 只比较 SID，无法区分是否提权、完整性级别 | 输出 `TokenElevation` 和完整性级别，供桌面验证矩阵使用 |
| L5 | #24 | `MainWindow.xaml` 第 24 行 | `Evidence` 文本位于 DataTemplate 中，窗口级的隐式样式不会作用到模板内部（M2.5 的教训），因此**不会自动换行**，64 位哈希可能被截断 | 加上 `TextWrapping="Wrap"`；并在冒烟测试中执行一次检查，让截图和对比度检查覆盖这一行 |
| L6 | #24 | `MainViewModel.cs` 第 86 行、检查器第 52 行 | 检查的是文本框里**尚未保存**的路径；版本信息是在哈希之后另外按路径读取的，两者之间文件可能被替换；`Directory.Exists` 在界面线程上执行，网络路径可能导致卡顿 | 只检查已保存的路径，或在界面上标明“未保存”；把版本信息的读取移到二次校验之前；用 `Task.Run` 包装 |
| L7 | #24 | 证据文档 | 源码分析使用的是 HEAD `21660281`，不是发行标签对应的提交 `1e77d59` | 补记 `1e77d59`；审查人已核实这两个文件在两次提交之间没有差异（§5.2） |
| L8 | #23/#24 | `EVIDENCE_INDEX.md` | 两个 PR 依次合并时会发生文本冲突 | 后合并的 PR 先 rebase 到新的 main，保留两边的章节 |
| L9 | （main，不属于本次 PR） | `src/VIvian.Infrastructure/Logs/LogFileTailer.cs` 非 Windows 分支 | 在 Linux 上用 `File.GetCreationTimeUtc` 识别文件，写入后可能被误判为“换了新文件”，4 项测试失败。Windows CI 不受影响 | 在 Linux 上改用 inode，或把这几项测试标记为仅在 Windows 上运行 |

### 9.3 未验证的风险（不是已确认缺陷）

| 编号 | 风险 | 需要什么证据 |
|---|---|---|
| U1 | 引擎经 UAC 或启动器拉起的进程会脱离 Job，而“Job 为空”会被误判为“清理已确认” | 桌面实测（ADR-007 矩阵）；在真实适配器的设计中明确：对账必须核实已知的关键进程（例如游戏进程句柄），不能只看 Job 是否为空 |
| U2 | CI 测试宿主是否处于 Job 中（影响 M2 的判断） | M2 建议的对照测试 |
| U3 | 桌面 Windows 10/11 上 UAC 拒绝、同意、提权子进程的归属、执行器被结束 | **Pending：需要用户授权** |
| U4 | March7thAssistant 发行包的 CLI 实际行为 | 官方完整包、哈希，以及在隔离目录中的只读实测 |

## 10. 需要 Codex 修复的内容（按优先级）

| 优先级 | 编号 | 内容 | 阻断哪些合并 |
|---|---|---|---|
| P0 | H1 | 调度器对所有适配器调用施加自己的时限 | **任何真实适配器**合并之前 |
| P0 | H2 | 增加内存中的“未解决运行”门禁；非终态记录也算作未解决 | **任何真实适配器**合并之前 |
| P1 | M1 | 紧急停止同步取消适配器令牌；修复偶发失败的测试 | PR #20 |
| P1 | M2 | Job 成员关系改为针对 VIvian 自己的 Job 判断，或增加对照组 | PR #23 |
| P1 | M3 | 检查器捕获 I/O 异常并返回保守结果；补充测试 | PR #24 |
| P1 | M4 | 源码检查：禁止在监管器之外启动进程 | PR #24 |
| P2 | L1～L8 | 见 §9.2 | 不阻断 |
| P3 | L9 | 日志跟踪器在非 Windows 系统上的文件识别 | 不阻断 |

H1 和 H2 属于 M2 基线代码（原作者是 Claude），不是 PR #20 引入的，建议另开 Issue 处理，不要并入 PR #20 扩大其范围。

## 11. 待完成的 Windows 桌面验证

以下项目**全部没有执行，也不能根据 CI 推断为通过**：

- ADR-007 矩阵：UAC 拒绝、UAC 同意、提权进程的子进程归属、执行器被强制结束、用户手动打开同名引擎（需要用户授权）
- M2 的人工验收清单（缩放、多显示器、真人操作）
- PR #24：在真实的 March7thAssistant 安装目录上执行只读检查，核对哈希和版本显示

## 12. 各 PR 的审查结论

| PR | 结论 | 理由 |
|---|---|---|
| **#20** | **REQUEST CHANGES** | 修复内容正确，并且修掉了一个真实存在的“矛盾证据判为成功”缺陷。但新增的“启动中紧急停止”测试会偶发失败，其根因（取消异步传递）正好属于本 PR 的范围（M1）。修复 M1 后可以批准。H1/H2 另开 Issue，不作为本 PR 的阻断条件 |
| **#23** | **REQUEST CHANGES** | 作为“同权限无害进程验证”的范围是合适的，文档也如实标注桌面 UAC 尚未验证。同名隔离测试有效。但“属于 Job”这条证据的证明力弱于文档的表述（M2），本 PR 的目的就是提供证据，需要先修正。修复 M2 后可以按限定范围批准，**不代表 #16 完成，也不代表 ADR-007 被接受** |
| **#24** | **REQUEST CHANGES** | 只读性和范围表述都正确，没有过度声称。但文档声称的保守路径没有测试，I/O 异常没有转为保守结果（M3），也缺少防止启动进程的自动检查（M4）。修复后可以按“只读发现”的限定范围批准，**不代表 CLI 已接入，也不代表具备任务执行能力** |

GitHub 不允许 PR 作者对自己的 PR 提交“批准 / 要求修改”。本会话使用的账号与 PR 作者相同（`zcwjxy1412`），因此正式评审以 **COMMENT** 类型提交，结论写在评审正文中。

## 13. 最终集成建议

1. 三个 PR **暂不合并**。Codex 修复 M1～M4 后，由审查人进行第二轮独立审查。
2. 建议的合并顺序：#20 → #23 → #24。#24 合并前需要 rebase，解决 `EVIDENCE_INDEX.md` 冲突（L8）。
3. 另开 Issue 处理 H1 和 H2；**在它们关闭之前，不合并任何真实引擎适配器**（包括 PR #24 之后的 March7thAssistant 生命周期部分）。
4. 真实适配器的设计必须明确 U1：对账要核实已知关键进程，不能把“Job 为空”当作“全部已清理”。
5. 桌面 UAC 验证需要用户明确授权后才能进行；在此之前，#16 保持 Open，ADR-007 保持“提案”。

## 附录：审查人本地复现测试（未推送）

复现测试写在审查人的临时工作区中，基于 PR #20 head，没有推送到仓库：

- **R1**：`HangingAdapter` 分别在预检、启动、停止、对账四个位置永不返回 → 紧急停止后 8 秒，队列仍未结束，`IsRunning=True`（4/4 复现）
- **R2**：使用真实的 `LocalDatabase` 包装层，只让 Blocked 状态的写入失败 → 第二个队列 `Completed`，`nextStarted=1`
- **R3**：适配器启动时忽略取消，仍然返回运行 → 被紧急停止，结果为 Cancelled（通过）
- **R4**：适配器返回的证据列表为空引用 → 结果为 Blocked，不是 Success（通过）

需要的话，可以把这些复现测试整理成独立的 PR，交给 Codex 或 DeepSeek 作为修复的验收测试。
