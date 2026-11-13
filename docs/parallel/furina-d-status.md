# D BetterGI 释放失败阻断修复与离线审计

> 最新进度：总控已在审计交付后授权 D-1 实现增量，见文末。以下审计历史保留；其中“产品零修改/未复现”描述的是 7e128e6 审计提交，不代表最新实现状态。

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

## D-1 实现增量（总控随后明确授权）

- 实现基线：审计提交 `7e128e6cd9bd2281390b0614f8d82c8dee36fee1`；公共基线仍 T0，未吸收集成分支新接口。
- 复现链：临时 SQLite 两账号 → 真实 MultiAccountOrchestrator → BetterGiNativeDaily 的事务核心 → 真实 BetterGiRun（模拟监管组/日志）→ 真实 RecordDailyAsync 落库 → 回到账号编排。生产入口仍先 VerifyCommonOwner；仅提取注入受监管启动器的内部事务核心，测试使用临时桌面锁，不取得正式共同锁。
- 原行为 RED：正常成功对照通过；注入首账号组 Dispose IOException 后，实际进入第二账号任务入口，违反只执行首账号的断言。日志 `artifacts/tests-d/release-red.log` / `d-release-red.trx`。此前一次 JSON 测试字符串编译错误和重复合成登录身份导致的夹具失败已修正，不计作产品 RED。
- 修复：BetterGiRun 仅在 Dispose 完成并通知 registry 后提供 `CleanupConfirmedAfterDisposal`。原生与验收调用方在 await using 完成后的 finally 重新检查此确认，未确认则将 cleanup 降为 Unconfirmed，保留配置恢复文件/native marker/桌面 dirty 状态。没有把原来吞掉的释放异常改为抛出，因此没有引入 E 的隐式释放遮蔽首个错误问题。
- 测试覆盖四个组合：正常完成、释放失败、正常取消、取消+释放失败；真实恢复文件与临时锁拒绝新运行均断言；失败后第二账号任务入口绝不进入，成功对照确实执行两账号并恢复。`RecordDailyAsync` 只改 internal 可见性供真实链路测试，不改结果语义。
- D-2 保持原样作为低优先级终态一致性残余；此次不扩展修复范围。正式验收入口的新增 guard 通过编译与现有验收测试覆盖，未调用会接触实际协议注册/桌面的端到端验收入口。
- 初轮针对性 349/349（前两个组合）通过；加入取消组合后默认/M5 全量各 1118/1118，0 失败、0 跳过，证据 `artifacts/tests-d/d-implementation-default.trx`、`d-implementation-m5.trx`。首审后只增加 Cancelled 显式断言和注释，两构建最终边界测试各 4/4：`d-final-boundary-default.trx`、`d-final-boundary-m5.trx`；产品代码未再修改。
- DeepSeek 实现首审、复审均有效，共两轮，已达到上限。此前 D 文档审计 1 轮不冒充实现审查；E 两轮残余不变、不另名补审。
- 新桥接进程前运行 `verify-deepseek-wire.mjs`：PASS，确认 thinking enabled/disabled 位于请求 JSON 根，无网络/凭据使用。

### 实现独立审查核实

- 首审响应 `artifacts/deepseek-d-implementation/response-1.json`，SHA256 `0A9A864EDCFB60FA56754910EF4943185911F40F8EB1F880FA2EC40F4D166B70`。认可 D-1 两处最终 guard，但 Critical/Medium 误将取消与清理失败等同，建议取消时也必须保留恢复标记。源码中的 RecordDailyAsync 明确允许已清理且已恢复的取消结果返回 Cancelled；编排停止后续账号，与恢复配置并不矛盾。两构建四组合实际通过，故不采纳其改动清理语义建议。增加 Cancelled 显式断言与说明，确保取消路径确实执行。
- 首审遗漏实际 Registry 源码：其定义位于 BetterGiAdapter.cs，误按单文件名取上下文导致空片段。复审补充完整 Adapter/Registry 与实际增量，纠正上下文。实际 Release 只在相同 RunId 且 Confirmed/NotRequired 时释放 active，测试使用的就是该生产 Registry，非另写测试替身。
- 首审 Low 的 disposal 中途异常使最终确认保持 false 属于预期保守阻断，无需修改。既有 D-2 继续披露。
- 复审响应 `artifacts/deepseek-d-implementation/response-2.json`，SHA256 `CD1C5CEFAA3BF267CD4CDF725EEB0F3866C3C3218D44459DADBD361721991684`。逐项核实：
  - Moderate“LaunchAsync 启动取消没有 catch 导致占用泄漏”不成立：Adapter 的 StartAsync 与第二次 probe 均在 try 中，catch(Exception) 将未知启动失败转为 NothingStarted=false，故保留占用是无法证明未启动时的预期阻断。CheckLaunchPlanAsync 在 TryAcquire 之前；已确认未启动的 EngineStartException 才释放。拒绝其一律 NotRequired 释放建议。
  - Low“finally 在 await using 释放之前读取”不成立：using 声明位于 try 的内部词法作用域，在退出该 try 时先隐式 Dispose，再进入外部 finally；四个回归用例直接验证这一顺序和恢复文件状态。
  - “成功用例没有释放/没有第二次取得”不成立：用例断言 Registry.IsBusy=false，正常成功确实执行第二账号并再次 TryAcquire；异常用例则断言保持占用。
  - 未确认释放保持 false、启动前 NotRequired 路径没有新覆盖、既有恢复写盘中断等是保守行为或原有边界，不是本次新增缺陷。取消语义与既有报告行为保持原样。
- 作者核实结果：D-1 实现及故障注入验证通过；两轮模型意见已全部核对，没有确认的新增未修复问题。不能将模型原始输出称为“无意见通过”；D-2 低优先级残余和实机验收缺口仍开放，不声称 D 整体无缺陷或可发布。
