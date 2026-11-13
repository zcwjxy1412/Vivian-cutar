# D BetterGI 离线审计首批

- 日期：2026-11-13（America/New_York），总任务 #59，D #64。
- SOURCE_BASE_SHA：`ce3ebeea79c507825e66a9e97097a6f80ede33cc`。
- BASE_SHA / 公共依赖：`8f857cf2cd16515d81f4c65f8e50b693374dccd4`，契约 v1；尚未吸收总控新集成提交。
- 分支：`feat/furina-genshin`；工作树：`C:\Users\胡桃\.codex\worktrees\furina-vnext-genshin\Vivian-source`。
- HEAD：本文件所属提交，精确 SHA 见草稿 PR / 聊天交付。
- 当前阶段：按总控“E 真实审查记录后推进 D 离线审计”执行。产品和测试源码零修改；交付差距报告及现有测试证据，不宣称 D 实现/验收完成。
- 已读本工作树 AGENTS、公共契约、所有权和原始任务书 D。E 两轮审查已记录在 E 分支，未重复 F 审查。

## 只读安装识别

本轮只读取候选程序元数据/哈希及资源目录是否存在，没有启动程序、取得共同桌面锁、读取账号库或改写用户配置。

- 程序：`C:\Program Files\BetterGI\BetterGI.exe`，存在，111164958 字节。
- FileVersion：`0.66.0.0`；ProductVersion：`0.66.0`。
- SHA256：`73732F440BC8FF3B0272F2ADBBF72ACADCB952D45BF7F5A62CF0E3573DBD7D73`，与 `BetterGiNativeDaily.ProgramDigest` 一致。
- Assets / GameTask 目录存在；没有验证目录内全部资源、实际运行权限、实际配置内容或游戏登录状态。匹配程序哈希不是整个安装或实机通道验收。

## 现有能力与离线验证

| 范围 | 源码 / 已执行测试 | 结论边界 |
| --- | --- | --- |
| 启动参数 | `BetterGiTaskRequest.ToArguments`；`LaunchUsesTheSupervisorWithAllowlistedArgumentsAfterTakingTheLogBaseline` | 已选路线为 `startOneDragon [name]`，另有 `--startGroups`；不假设 --daily 或 --config-dir。脚本组没有成功契约。 |
| 档案/账号绑定 | `BetterGiAccountBatch` 冻结每账号 source revision，`BetterGiNativeDaily` 按 accountId 映射档案；`EachAccountUsesItsOwnFrozenPlanAndTheNextWaitsForRestoration` | 模拟证明账号独立与恢复阻断；未做真实 A→B。 |
| 不存在/歧义/变化配置 | `BetterGiLaunchGuardTests.ANameThatDoesNotExistNeverReachesTheSupervisor`、`AFileNameMatchIsInsufficientAndDuplicateLogicalNamesAreAmbiguous`、`AProfileRemovedAfterPreflightIsRecheckedAtLaunch` | 同名文件不等于有效逻辑档案，启动时重查；无需另造配置层。 |
| 日志基线与身份 | `BetterGiLogFileSourceTests.ContentWrittenBeforeOpenIsNeverReadAndNewLinesAre`、`HistoricalCompletionLinesNeverCount`、`AttributionRequiresPrimaryOwnedPidAndARecentInstanceStart` | 开启日志基线后读新增内容，Primary + PID + 启动时间归属；旧成功行不能满足当前运行。 |
| IPC/退出码/提权重启 | `ExitCodeZeroWithoutAnyLogIsIndeterminate`、`ExitZeroWithNoAttributedStartupCouldHaveForwardedTheTask`、`ElevatedSelfRestartIsIndeterminateAndCleanupStaysUnconfirmed`、`ForeignBetterGiActivityDuringTheRunKeepsCleanupUnconfirmed` | 退出 0 不等于完成；其他实例/脱离 Job 时保持未知清理。 |
| 停止/超时/进程边界 | `UserCancellationTerminatesOnlyTheOwnedGroupAndIsNotReportedAsFailure`、`TimeoutPathSendsNothingGracefullyThenTerminatesOwnedProcesses`、`BetterGiWindowsStopTests.EmergencyStopClearsTheRealWindowsJobIncludingItsChild` | 无害模拟进程和 Job 验证，不运行真实游戏；无已验证 graceful 或暂停通道，不开放运行中安全失焦聊天。 |
| 配置恢复 | `BetterGiProfileOverrideTests`、`BetterGiDomainOverrideTests`，包括 `ANewUserEditBlocksRecoveryAndIsPreserved`、`FieldMergeRecoveryResumesAcrossEitherAtomicMove` | 恢复保留用户新编辑与冲突，不能把文件替换成功当作批次已恢复。 |
| 原生流程完成 | `BetterGiNativeDailyTests.DelegatedCompletionDoesNotClaimInventoryVerification`、`IncompleteConflictingOrForeignResultsCannotAdvanceAccounts` | 原生通道采用 owned Primary、流程结束、无 adverse/foreign、退出0；不要求独立库存核验，不能对外把它称作奖励到账或树脂消耗已独立证实。 |

通用 `BetterGiAdapter` 真实执行门仍未验收；`BetterGiNativeDaily` 是独立受控原生交接路径。不能用通用门关闭推断所有入口都无法执行，也不能用原生流程日志反推通用门已验收。

## 审计发现（未修复，不放行）

### D-1：最终资源释放失败没有传到原生恢复判定（高，源码发现）

`BetterGiRun.DisposeAsync` 与 E 修复前相似：监管组 Dispose 异常被吞，只有 `_lastCleanup` 降为 Unconfirmed 并通知 registry。`BetterGiNativeDaily.RunAsync` 已将 Reconcile 结果存到本地 cleanup；await using 结束后，finally 可能仍按旧 Confirmed 恢复配置、ConfirmClean 并删除 native-recovery.json。回调仍执行，但 `BetterGiRunRegistry.Release` 只在 Confirmed/NotRequired 时清除 active，故内部占用仍在，与持久桌面标记被清除可能不一致。

尚未通过故障注入复现完整原生路径，不能把静态数据流发现写成实机故障。现有 347 项测试未覆盖最终 Dispose 失败；应在修复批次增加模拟注入接缝后 RED→GREEN。修复不能只照搬 E 的“Dispose 抛错”：还要覆盖 NativeDaily 的 finally 与 DailyAcceptance 调用方，保证单一释放路径、首次错误保留、释放失败不恢复/解锁，不能引入双重错误覆盖。

### D-2：清理未确认仍写入 Finished（低，终态一致性残余）

`BetterGiAccountBatch.RecordDailyAsync` 固定发布 `RunPhase.Finished`，即使 cleanup 未确认，与统一调度的 Blocked 终态语义不一致。返回的 `CleanupConfirmed=false` 仍阻断 orchestrator，不能据此声称下一账号会启动。

已撤销初始怀疑“Success 分支优先导致恢复未确认还显示成功文案”：进一步读取 `BetterGiDailyAcceptanceResult.Outcome` 与 `ScopedCompletionConfirmed` 证明二者先要求 Restored + Confirmed，恢复未确认时不会进入 Success 分支。此修正是作者追查完整数据流后的结果，不能把已撤销怀疑列为产品缺陷。

建议后续专属修复只对齐终态：清理未确认发布 Blocked；用临时 LocalDatabase 验证业务成功证据+清理失败、恢复失败、正常取消，保留现有降级与阻断。该项不单独作为安全放行阻塞，D-1 仍需处理。

## 验证记录

仅当前命令设置 `DOTNET_ROOT=E:\auto agent game\.dotnet` 和 PATH，未改 global.json。模拟安装/数据库来自测试唯一临时目录，输出在本工作树 artifacts/tests-d。

```powershell
dotnet test tests/VIvian.Tests/VIvian.Tests.csproj -c Release --filter 'FullyQualifiedName~BetterGi|FullyQualifiedName~Genshin' --results-directory artifacts/tests-d --logger 'trx;LogFileName=d-targeted.trx'
dotnet test tests/VIvian.Tests/VIvian.Tests.csproj -c Release -p:M5DailyTestBuild=true --filter 'FullyQualifiedName~BetterGi|FullyQualifiedName~Genshin' --results-directory artifacts/tests-d --logger 'trx;LogFileName=d-m5-targeted.trx'
```

- 默认针对性：347/347，通过，0 失败、0 跳过。
- M5 针对性：347/347，通过，0 失败、0 跳过。
- 本轮为文档审计交付，没有运行全量/WPF 冒烟，也没有借用总控全量结果作为本分支新证据。
- D 的 DeepSeek 文档审计首审：有效 1 轮，`deepseek-v4-pro` / thinking disabled；E 的两轮不计作 D 审查。没有产品实现改动，未开启实现修复复审。

## D 首审核实

- 原始响应：`artifacts/deepseek-d/response-1.json`，SHA256 `58BA484EC1194D970E5E2EFF4D0A87001BD00517FB9E5F1DB8928FFB3F7EE149`。只发送新增报告及相关产品源码，无凭据/账号信息。
- D-1 被支持；关于“回调被调用不等于 registry 已释放”的措辞已补全：`BetterGiRunRegistry.Release` 的真实条件确认 Unconfirmed 保留 active。
- 首审输入是报告初稿，尚未包含作者随后补读 `BetterGiDailyAcceptanceResult` 后撤销的成功文案怀疑。模型给出的 scopedSuccess=true + Cleanup=Unconfirmed 路径不可达：`ScopedCompletionConfirmed` 依赖 `DiagnosticPassed`，后者要求 Restored && Cleanup==Confirmed；`Outcome` 同样先做清理/恢复检查。该意见已驳回，不把审查输出作为源码事实。D-2 仅保留固定 Finished 的低优先级一致性问题。
- 模型无法独立验证测试数量；347/347 × 两构建来自本轮实际 TRX，不是模型证明。
- 审计文档已核实；D-1 未做故障注入复现/修复，产品独立审查与实机验收尚不能标通过。后续修复属于新的明确变更审查，不能将本次文档审计当作未来实现审查。

## 下一步与未验证

1. 总控登记 D-1/D-2，安排后续专属最小修复与故障注入测试；当前审计不构成可发布结论。
2. I 公共契约仍承担幂等提交、RunId/账号/config revision 绑定、输入前安全许可。D 不改公共文件。
3. 修复及审查后再由总控串行预约：单账号登录门确认→原生完整任务→进程退出→配置恢复→A→B；另测停止/恢复冲突、无奖励证据、IPC 转发和版本失配。
4. 未启动真实游戏/BetterGI、未改正式安装、未部署、未做真实耗材/奖励验证；F/E 分支均保持本轮审计开始时的状态。
