# M3 权限与 Job 边界验证记录

状态：**部分验证，桌面 UAC / 跨权限路径 Pending**。关联 [#16](https://github.com/zcwjxy1412/Vivian-cutar/issues/16)、[ADR-007](../architecture/ADR-007-M3-PRIVILEGE-BOUNDARY.md)。本文件只记录无害程序与已有模拟工程的覆盖范围，不作为游戏实测证据。

## Windows CI 可执行的范围

| 场景 | 自动化证据 | 结论范围 |
|---|---|---|
| 普通启动者身份与 Job 归属 | `SupervisorTests.HarmlessChildKeepsCallerIdentityAndBelongsToJob` 比较父子 Windows SID，并让子进程调用 `IsProcessInJob` | 仅同权限、直接 `CreateProcess` 的 FakeEngine；不证明提权分叉归属 |
| 同名外部进程隔离 | `SupervisorTests.SameNamedExternalProcessSurvivesJobTermination` 自行创建两个同名 FakeEngine，终止 VIvian 所属 Job 后按外部进程句柄确认仍运行 | 不按名称结束进程；外部测试进程由测试用其精确句柄在 `finally` 清理 |
| 孤儿子进程与 Job 清理 | `SupervisorTests.OrphanedChildStaysInsideTheJobAndIsTerminated`、`FakeEngineScenarioTests.S06` | 仅未脱离 Job 的直接子进程 |
| 句柄白名单 | `SupervisorTests.UnrelatedInheritableHandleIsNotVisibleInChild` | 不应继承无关句柄；不证明上游提权后句柄语义 |
| UI/宿主强制结束与数据库恢复 | `FakeEngineScenarioTests.S10` 强制结束测试宿主，检查 Job kill-on-close、SQLite 对账与后续任务阻断 | 非真实 WPF 界面、非提权执行器 |
| 停止与未知清理 | `FakeEngineScenarioTests.S08/S09` 及 `SchedulerTests` 的未确认清理场景 | 未确认则阻断，不表示桌面上的一切关联进程已结束 |

[PR #23 Windows CI](https://github.com/zcwjxy1412/Vivian-cutar/actions/runs/41785327652)：168/168 通过，0 失败/跳过，构建和 WPF smoke 完成。此前失败运行 `41784998546` 暴露测试探针错误：`IsProcessInJob` 不能用空的进程句柄；已改用 `GetCurrentProcess` 并由上述运行验证。此记录仅适用于 Windows CI 的同权限环境。

## 桌面 Windows 10/11 待授权验证

1. 在隔离的测试目录中准备仅打印 PID、Windows SID、完整性级别和 Job 归属的无害可执行文件；记录 Windows 版本与 SHA-256。测试前后保存进程 PID、创建时间与原始输出。
2. 普通权限启动 UI 与该测试程序，记录同权限进程树；随后从普通权限请求系统 UAC，**由用户本人**拒绝，确认没有生成运行中子进程、没有下一任务误启动。
3. 再由用户本人批准 UAC，记录提权进程及其子进程的身份、完整性级别、Job 归属与退出回执。若跨权限进程无法确认归属，记录 `CleanupState.Unconfirmed` 并阻断后续任务。
4. 分别强制结束测试 UI 与测试执行器，重启 VIvian 并核查进程句柄、Job 状态和数据库对账；不可按进程名清理。另起同名但不属于 VIvian 的测试进程，确认其未被结束。
5. 记录每一步原始日志与失败路径。不要在仓库保存真实账号、系统用户名或私人配置；提交脱敏后的 PID/时间、摘要和判定。

**阻断**：本轮没有获授权的桌面 UAC 会话或真实发行包。ADR-007 仍为提案，无法据此启用需要提权的生产任务或宣布 #16 完成。
