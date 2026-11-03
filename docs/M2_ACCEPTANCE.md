# VIvian V1.0 — M2 验收材料：执行内核（模拟模式）

日期：2026-11-03　应用版本：0.2.0　基线：`main` @ `06d2cda`　计划：`docs/M2_PLAN.md`（已批准）

**结论：M2 执行内核已实现，在 GitHub `windows-latest` 上完成构建、158 项自动化测试（0 失败、0 跳过）和 WPF 冒烟验证。计划 §4 规定的 11 个故障场景全部用真实 Windows 进程执行通过。M2 只包含模拟模式，不接入任何真实游戏引擎，不能据此宣称真实游戏自动化可用。视觉、高 DPI、多显示器等人工验收仍待执行（§7）。**

## 1. 交付内容

| 计划要求 | 实现 | 主要文件 |
|---|---|---|
| 统一适配器契约 | `IGameAdapter` / `IEngineRun` / `IProcessSupervisor` / `IRunJournal` / `IRunHistory` | `src/VIvian.Core/Execution/Contracts.cs` |
| 三层状态模型 | `RunPhase` / `RunOutcome` / `CleanupState`，状态机校验合法转换 | `ExecutionModel.cs` |
| 串行调度 | `QueueScheduler`：严格串行、任务超时、无输出卡死检测、停止升级、对账 | `QueueScheduler.cs` |
| 进程归属管理 | Windows Job Object：挂起创建 → 放入 Job → 恢复；kill-on-close | `src/VIvian.Infrastructure/Processes/` |
| SQLite 持久化与恢复 | 模式 v2（runs / run_transitions / run_events）、崩溃对账、用户确认 | `LocalDatabase.cs`、`RunStore.cs` |
| 单实例保护 | 每个数据目录一把命名互斥锁；可识别遗弃锁、拒绝访问 | `SingleInstanceGuard.cs` |
| 紧急停止 | 立即发出、无倒计时、先于任何磁盘写入 | `QueueScheduler.cs`、`QueueViewModel.cs` |
| FakeEngine 集成测试 | 可编排的测试引擎程序 + 崩溃宿主程序 | `tests/VIvian.FakeEngine`、`tests/VIvian.CrashHost` |
| 队列监控界面 | “模拟队列”标签页，全部结果带【模拟】标签 | `QueueViewModel.cs`、`MainWindow.xaml` |
| Windows CI 验证 | 每个 PR 必须通过；冒烟测试覆盖界面响应、紧急停止、对比度 | `.github/workflows/windows.yml`、`App.xaml.cs` |

不在 M2 范围内（按计划）：真实游戏适配器、读写游戏内存、反作弊绕过、引擎自动更新。

## 2. 提交与合并的 PR

| PR | 内容 | 合并提交 | Windows CI |
|---|---|---|---|
| [#1](https://github.com/zcwjxy1412/Vivian-cutar/pull/1) | M2 计划（已批准） | `7d2400e` | [通过](https://github.com/zcwjxy1412/Vivian-cutar/actions/runs/41669785580) |
| [#2](https://github.com/zcwjxy1412/Vivian-cutar/pull/2) | M1 基线复核记录 | `0dedbf6` | [通过](https://github.com/zcwjxy1412/Vivian-cutar/actions/runs/41670231657) |
| [#3](https://github.com/zcwjxy1412/Vivian-cutar/pull/3) | M2.1 契约、状态模型、调度器 | `dfeac39` | [通过](https://github.com/zcwjxy1412/Vivian-cutar/actions/runs/41673506038) |
| [#4](https://github.com/zcwjxy1412/Vivian-cutar/pull/4) | M2.2 SQLite v2、运行记录、崩溃对账 | `d9a50db` | [通过](https://github.com/zcwjxy1412/Vivian-cutar/actions/runs/41676559090) |
| [#5](https://github.com/zcwjxy1412/Vivian-cutar/pull/5) | M2.3 进程监管、FakeEngine、11 个场景 | `6fb160e` | [通过](https://github.com/zcwjxy1412/Vivian-cutar/actions/runs/41678705692) |
| [#6](https://github.com/zcwjxy1412/Vivian-cutar/pull/6) | M2.4 单实例锁（含事件写入修复） | `1186028` | [通过](https://github.com/zcwjxy1412/Vivian-cutar/actions/runs/41680107701) |
| [#7](https://github.com/zcwjxy1412/Vivian-cutar/pull/7) | M2.5 模拟队列界面（含对比度修复） | `fd6d995` | [通过](https://github.com/zcwjxy1412/Vivian-cutar/actions/runs/41682790717) |
| [#8](https://github.com/zcwjxy1412/Vivian-cutar/pull/8) | 验收前复查修复 | `06d2cda` | [通过](https://github.com/zcwjxy1412/Vivian-cutar/actions/runs/41683935004) |

每个 PR 都是独立分支，Windows CI 通过后才压缩合并。

## 3. 自动化测试结果（Windows CI，`06d2cda`）

构建：0 warning / 0 error（`TreatWarningsAsErrors`，核心库强制 CA2007）。测试：**158 通过、0 失败、0 跳过**。

| 测试类 | 数量 | 覆盖内容 |
|---|---|---|
| SchedulerTests | 37 | 串行、证据规则、超时/卡死（虚拟时钟）、停止升级、紧急停止、日志写入门禁、故障注入 |
| StateModelTests | 23 | 状态转换全表、结果只能决定一次、证据判定 |
| CommandLineTests | 16 | Windows 命令行转义往返（中文、空格、引号、反斜杠、`& \| >`） |
| SimulationModeTests | 15 | 5 种模拟情形、【模拟】标签全覆盖、原因码中文化 |
| PersistenceTests | 14 | M1 原有持久化测试，全部保留并通过 |
| RunStoreTests | 12 | v1→v2 迁移、迁移失败回滚、记录不可覆盖、崩溃对账、用户确认 |
| FakeEngineScenarioTests | 11 | 计划 §4 的 11 个场景（真实进程） |
| FakeEngineProtocolTests | 9 | 输出解析、旧证据降级、自由文本不当证据 |
| SupervisorTests | 7 | Job Object、子进程清理、kill-on-close、参数传递、标准输入停止 |
| SingleInstanceGuardTests | 7 | 双开、遗弃锁、拒绝访问、8 线程并发 |
| ProcessSafetyGuardTests | 7 | 扫描产品源码：禁止按名称结束进程 |

在 Linux 上本地运行时，18 项需要 Windows 的测试会显示为**跳过**（不会被计为通过）。

### 冒烟测试（真实 Windows，`06d2cda` 的 PR 运行）

```
WPF content rendered; SQLite round-trip passed; second instance refused;
simulation queue 3/3 success persisted; emergency stop 219 ms; worst UI response 9 ms.
contrast ok for 76 text elements (28 over non-solid backgrounds not checked).
```

| 验收项 | 结果 | 上限 |
|---|---|---|
| 第二个实例被拒绝 | 通过 | — |
| 模拟队列 3/3 完成、已写入、带【模拟】标签 | 通过 | — |
| 紧急停止（点击到队列取消） | 219 ms | 3 秒 |
| 界面线程最慢响应（队列运行全程） | 9 ms | 1 秒 |
| 文字对比度（WCAG AA，4.5:1） | 76 处全部达标 | — |

## 4. 11 个故障场景（计划 §4）

全部通过 `QueueScheduler → FakeEngineAdapter → WindowsProcessSupervisor（Job Object）→ FakeEngine.exe` 的真实链路执行。

| # | 场景 | 判定 | 测试 |
|---|---|---|---|
| 1 | 正常完成并给出证据 | Success / Confirmed | S01 |
| 2 | 退出码 0 但没有证据 | **Indeterminate**（不当成功） | S02 |
| 3 | 明确报失败 | Failed | S03 |
| 4 | 日志里同时出现 ERROR / 失败 / 成功字样 | 按结构化证据判定，不按关键词一票否决 | S04 |
| 5 | 上一次运行残留的证据 | 被忽略 → Indeterminate | S05 |
| 6 | 父进程退出、子进程仍在运行 | Job 清理子进程 → Confirmed | S06 |
| 7 | 长时间无输出 | 判为卡死并停止 | S07 |
| 8 | 忽略正常停止请求 | 升级为终止 Job | S08 |
| 9 | 用户紧急停止 | 3 秒内结束，Cancelled，后续游戏不启动 | S09 |
| 10 | VIvian 在运行中崩溃 | 引擎随之结束；重启后对账为 Indeterminate + Unconfirmed；新队列被阻断，直到用户确认 | S10 |
| 11 | 已有不属于本次运行的同名进程 | 队列阻断，**该进程不受影响** | S11 |

## 5. 可靠性要求对照（授权文件 Phase 3）

| 要求 | 落实方式 | 证据 |
|---|---|---|
| 三层状态分离 | 独立枚举；状态机禁止非法转换；结束必须有结果且清理已确认 | StateModelTests |
| 退出码 0 不算成功 | 只有本次 RunId 的明确完成证据才算成功；适配器空口声称成功会被降级 | S02、`AdapterClaimingSuccessWithoutEvidenceIsDowngraded` |
| 只终止本次会话拥有的进程 | Job Object 边界；终止 API 只出现在监管器中 | SupervisorTests、ProcessSafetyGuardTests |
| 绝不按进程名结束进程 | 源码扫描测试（已做变异验证） | ProcessSafetyGuardTests |
| 清理未确认则阻断后续任务 | 单个队列内阻断；跨队列、跨重启也阻断，直到用户确认 | `UnconfirmedCleanupBlocksTheRestOfTheQueue`、S10 |
| 崩溃后诚实对账 | 已启动的运行转为 Blocked + Unconfirmed，绝不标为成功 | `CrashReconciliationNeverInventsSuccess`、S10 |
| 紧急停止立即生效、无倒计时 | 先发出停止，再写记录；界面通过启动前创建的令牌停止 | `EmergencyStopIsSentBeforeSlowJournalWrites`（变异验证）、S09 |
| 不确定的任务不自动重跑 | 调度器没有任何重试路径 | 代码审查 |

## 6. 开发中发现并修复的缺陷

| 严重度 | 问题 | 发现方式 | PR |
|---|---|---|---|
| High | 调度器的等待会回到调用方线程。从界面启动时，紧急停止和卡死检测都要排队等界面线程空闲 | 调查一项偶发失败的测试 | #4 |
| High | 每条引擎输出都同步写一次数据库，输出密集时线程池饥饿，推迟卡死检测和紧急停止 | 同上（加计时日志定位） | #4 |
| High | M1 遗留：标签页、按钮、下拉框文字是浅色字压在浅色底上，几乎看不清 | CI 截图 | #7 |
| High | 标签页内的列表文字变成深色压在深色底上（对比度 1.1:1） | 新增的对比度自动检查 | #7 |
| High | 点击“开始”后的瞬间按下紧急停止，可能丢失这次停止 | 验收前复读代码 | #8 |
| Medium | 磁盘慢时，最后几条运行事件可能来不及写入 | Windows CI 上的一次失败 | #6 |
| Medium | 调度器内部异常会让运行记录停在“运行中” | 验收前复读代码 | #8 |

每个修复都有对应的回归测试；标注了“变异验证”的，均确认过：把缺陷故意改回去，测试会失败。

**当前没有已知且未解决的 Critical / High 缺陷。**

## 7. 尚未验证（需人工或后续里程碑）

| 项目 | 状态 |
|---|---|
| 100% / 125% / 150% / 200% 缩放、双屏、视觉细节 | ⏳ 待人工验收。CI 只在一种分辨率下截图，并检查对比度 |
| Windows 10/11 桌面版实机 | ⏳ CI 使用的是 GitHub 的 Windows Server 镜像，不等于桌面版 |
| 模拟队列的实际操作手感（真人点击、窗口关闭时停止） | ⏳ 待人工验收 |
| 任何真实引擎、游戏、UAC 行为 | ❌ 不在 M2 范围内，属于 M3 |
| 代码签名、安装器 | ❌ 属于 M6 |

### 人工验收清单（在 Windows 10/11 上执行）

- [ ] 解压 `v0.2.0-m2` 便携版，以普通权限启动，没有 UAC 提示；页眉显示“M2 · 模拟模式 · 未接入真实引擎”
- [ ] 再打开一次 VIvian：提示“已经在运行”，第二个窗口不出现
- [ ] “模拟队列”：三款游戏都选“完成并给出证据”，开始 → 依次完成，每条结果都带【模拟】
- [ ] 原神选“卡死”：约 8 秒后判为卡死并停止，后续游戏继续
- [ ] 运行中点击“紧急停止”：立即停止，未开始的游戏不会启动
- [ ] 选“完成但无法确认清理”：出现红色提示条；确认前不能开始新队列；点击确认后可以开始，原记录的结果不变
- [ ] 运行中直接关闭窗口，再重新打开：没有残留，或残留的运行显示为“结果未知”且需要确认
- [ ] 100%、125%、150%、200% 缩放下文字可读、按钮可点、可以滚动

## 8. 剩余技术风险

| 风险 | 影响 | 计划 |
|---|---|---|
| **真实引擎是否全程留在 Job 里未经验证**：引擎如果通过启动器、系统服务或 `ShellExecute` 拉起游戏，游戏进程可能不在 Job 内，导致清理显示“已确认”但游戏仍在运行 | 高（只影响 M3） | M3 必须对每款引擎实测进程树，见 `docs/M3_PREREQUISITES.md` |
| **权限模型未定（D04）**：引擎若需要管理员权限，以普通权限运行的 VIvian 无法把它放进 Job；经过 UAC 提权的进程会脱离 Job | 高（只影响 M3） | M3 之前先做提权执行器原型并作出决定 |
| 正常停止协议只对 FakeEngine 有效（标准输入发送 `stop`）。真实引擎没有已知的正常停止方式，只能在等待超时后终止整个 Job，游戏会被强制关闭 | 中 | M3 按引擎逐一核实 |
| 卡死检测依赖引擎输出。真实引擎可能只写日志文件、不写标准输出 | 中 | M3 增加日志文件跟踪作为事件来源 |
| 创建进程时 `bInheritHandles=true`：VIvian 自身可继承的句柄也会被引擎继承 | 中 | M3 前改用 `PROC_THREAD_ATTRIBUTE_HANDLE_LIST` |
| 外部进程检测只比较程序路径；拒绝访问的进程会被算作外部进程（安全，但可能导致不必要的阻断） | 低 | M3 按真实引擎调整 |
| 每次运行最多缓冲 10,000 条事件，超出部分丢弃，并记录丢弃数量 | 低 | 观察 |
| S08、S09 两项场景测试使用真实时钟；目前在 CI 上连续多次稳定通过 | 低 | 观察；若出现偶发失败再改用虚拟时钟 |
| 数据库升级到 v2 后，0.1.0 版会拒绝打开它（不会损坏数据），但也就无法再回退到 0.1.0 | 低 | 已在说明中注明 |
| 仓库目前是**公开**的 | 取决于用户 | 待用户决定 |

## 9. 预发布

建议发布 `v0.2.0-m2`，并标记为**预发布（pre-release）**，名称和说明中明确写“**仅模拟模式的开发构建，不包含任何真实游戏自动化**”。说明文字已备好：`docs/RELEASE_NOTES/v0.2.0-m2.md`。由用户在 GitHub Actions 中手动运行 Release 工作流后发布。

## 10. 下一步

M3（接入三款真实引擎）的前置条件见 `docs/M3_PREREQUISITES.md`。外部评审的材料见 `docs/reviews/M2_REVIEW_BRIEF.md`。
