# M3 权限与 Job 边界验证记录

状态：**部分验证，桌面 UAC / 跨权限路径 Pending**。关联 [#16](https://github.com/zcwjxy1412/Vivian-cutar/issues/16)、[ADR-007](../architecture/ADR-007-M3-PRIVILEGE-BOUNDARY.md)。本文件只记录无害程序与已有模拟工程的覆盖范围，不作为游戏实测证据。

## Windows CI 可执行的范围

| 场景 | 自动化证据 | 结论范围 |
|---|---|---|
| 普通启动者身份与权限级别 | `SupervisorTests.HarmlessChildKeepsCallerIdentityAndPrivilegeLevel`：父子进程的 Windows SID 相同，`elevated`（UAC 下是否以管理员角色运行）也相同 | 仅限同权限、直接 `CreateProcess` 启动的 FakeEngine；不能证明提权分叉的行为 |
| 属于 **VIvian 自己的** Job | `SupervisorTests.ChildIsCountedInViviansOwnJobAndControlProcessIsNot`：用新增的 `ISupervisedProcessGroup.OwnsProcess(进程句柄)`（即 `IsProcessInJob(进程, VIvian 的 Job 句柄)`）判断：VIvian 的子进程**在** VIvian 的 Job 中；同时运行的对照进程（不经监管器启动）和测试宿主自身都**不在**其中；子进程退出后，Job 中的进程数归零 | 判断针对 VIvian 自己的 Job，并且按进程句柄而不是 PID 判断。Job 中的进程数可能包含 Windows 自动创建的辅助进程（实测 CI 上为 2，推测是控制台宿主 conhost），因此不按进程数判断 |
| 同名外部进程隔离 | `SupervisorTests.SameNamedExternalProcessSurvivesJobTermination` 自行创建两个同名 FakeEngine，终止 VIvian 所属 Job 后按外部进程句柄确认仍运行 | 不按名称结束进程；外部测试进程由测试用其精确句柄在 `finally` 清理 |
| 孤儿子进程与 Job 清理 | `SupervisorTests.OrphanedChildStaysInsideTheJobAndIsTerminated`、`FakeEngineScenarioTests.S06` | 仅未脱离 Job 的直接子进程 |
| 句柄白名单 | `SupervisorTests.UnrelatedInheritableHandleIsNotVisibleInChild` | 不应继承无关句柄；不证明上游提权后句柄语义 |
| UI/宿主强制结束与数据库恢复 | `FakeEngineScenarioTests.S10` 强制结束测试宿主，检查 Job kill-on-close、SQLite 对账与后续任务阻断 | 非真实 WPF 界面、非提权执行器 |
| 停止与未知清理 | `FakeEngineScenarioTests.S08/S09` 及 `SchedulerTests` 的未确认清理场景 | 未确认则阻断，不表示桌面上的一切关联进程已结束 |

[PR #23 Windows CI](https://github.com/zcwjxy1412/Vivian-cutar/actions/runs/41785327652)：168/168 通过，0 失败/跳过，构建和 WPF smoke 完成。此前失败运行 `41784998546` 暴露测试探针错误：`IsProcessInJob` 不能用空的进程句柄；已改用 `GetCurrentProcess` 并由上述运行验证。此记录仅适用于 Windows CI 的同权限环境。

**按独立审查 M2 修正（2026-11-04）**：原测试用 `IsProcessInJob(当前进程, NULL)` 证明“属于 Job”。但这个调用判断的是“是否处于**任意** Job”，如果宿主本身已在 Job 中，即使 VIvian 没有把子进程放进自己的 Job，结果也照样为真，因此证明力不足。**[CI] 实测证实了这一点**：[run 41799596877](https://github.com/zcwjxy1412/Vivian-cutar/actions/runs/41799596877) 中，不经 VIvian 启动的对照进程报告 `any-job-member=True`，也就是说 CI 宿主本身就运行在某个 Job 中，原来的断言在 CI 上恒为真。现已改为从父进程一侧通过 VIvian 的 Job 句柄判断，并加入对照组。探针输出中原来的 `in-job` 实际表示 API 调用是否成功，已改名为 `api-ok`。

## 桌面 Windows 10/11 待授权验证

1. 在隔离的测试目录中准备仅打印 PID、Windows SID、完整性级别和 Job 归属的无害可执行文件；记录 Windows 版本与 SHA-256。测试前后保存进程 PID、创建时间与原始输出。
2. 普通权限启动 UI 与该测试程序，记录同权限进程树；随后从普通权限请求系统 UAC，**由用户本人**拒绝，确认没有生成运行中子进程、没有下一任务误启动。
3. 再由用户本人批准 UAC，记录提权进程及其子进程的身份、完整性级别、Job 归属与退出回执。若跨权限进程无法确认归属，记录 `CleanupState.Unconfirmed` 并阻断后续任务。
4. 分别强制结束测试 UI 与测试执行器，重启 VIvian 并核查进程句柄、Job 状态和数据库对账；不可按进程名清理。另起同名但不属于 VIvian 的测试进程，确认其未被结束。
5. 记录每一步原始日志与失败路径。不要在仓库保存真实账号、系统用户名或私人配置；提交脱敏后的 PID/时间、摘要和判定。

**阻断**：本轮没有获授权的桌面 UAC 会话或真实发行包。ADR-007 仍为提案，无法据此启用需要提权的生产任务或宣布 #16 完成。
