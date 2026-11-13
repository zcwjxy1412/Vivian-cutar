# E 绝区零首批离线交付

- 日期：2026-11-13（America/New_York）；总任务 #59，E #65。
- SOURCE_BASE_SHA：`ce3ebeea79c507825e66a9e97097a6f80ede33cc`。
- BASE_SHA / 公共依赖：`8f857cf2cd16515d81f4c65f8e50b693374dccd4`，公共契约 v1。
- 分支：`feat/furina-zzz`；工作树：`C:\Users\胡桃\.codex\worktrees\furina-vnext-zzz\Vivian-source`。
- HEAD：本文件所属提交；精确 SHA 由 PR 与聊天交付记录给出，避免自引用。
- 范围：总控已授权 E 离线审计与专属修复；F 保持稳定，不吸收尚未通知的公共提交，不开展 D。
- 状态：一项已复现的清理失败传播缺陷已最小修复，离线首批交付完成；不等于真实绝区零验收通过。

## 执行台账与已证实修复

读取了本工作树 AGENTS、基线、公共契约、所有权和原始任务书 E。沿用 Superpowers 的已批准计划执行流程，本文件作为 E 的持久记录；未重新要求设计确认。

缺陷：`OneDragonRun.DisposeAsync` 在监管进程组释放失败时将 `_lastCleanup` 设为 Unconfirmed，却吞掉异常。`OneDragonAccountBatch.RunAsync` 已在 `await using` 结束前发布 Finished/Confirmed 并构造 clean 结果。释放失败后，安装 registry 虽保持 busy，外层仍可能收到 clean 并清除共同桌面恢复标记，导致运行记录与资源清理事实不一致。

复现：模拟本轮 start/entered/coffee success/end success，根进程退出，Reconcile 返回 Confirmed，然后让模拟进程组 Dispose 抛 IOException。新增 `DisposalFailureCannotReturnACleanBatchResultOrPersistConfirmedCleanup` 在原行为上真实失败：预期 IOException，但未抛异常（`e-red.trx`）。

最小修复：

1. `OneDragonRun.DisposeAsync` 保留 Unconfirmed、通过 finally 通知 registry，并向调用方传播释放异常。
2. `OneDragonAccountBatch.RunAsync` 在发布最终清理状态前显式等待 Dispose；失败则记录 Blocked/Unconfirmed、保留已有业务 Outcome 并重抛，不能返回 clean。外层 `handoffClean` 在调用前已置 false，因而不会清除桌面标记。
3. 原有 `await using` 仍保证其他异常路径释放；第二次 Dispose 由现有 `_disposed` 门短路。未添加新运行入口；单轮执行 helper 由 private 改为 internal 供同程序集契约测试调用，不扩展 public API。

## 本轮证据与能力边界

| 项目 | 源码/离线证据 | 能力及限制 |
| --- | --- | --- |
| 固定启动通道 | `OneDragonAdapter` / `OneDragonContract`；`LaunchUsesTheIntegratedLauncherWithAllowlistedArguments` | 使用 `OneDragon-RuntimeLauncher.exe`，不改后端/MCP。通用适配器启动门仍关闭；M5 管理员批次有单独受控 handoff 路线，不能把通用门关闭误报为所有调用均不会执行。 |
| 固定版本/首次初始化 | `OneDragonInstallationInspector`、`OneDragonAccountBatch.ValidateInstallationAsync`、`OneDragonInstalledSource`；`BundledLayoutDoesNotImplyFirstCheckoutOrExplicitUpdateSetting`、`CodeAutoUpdateOrAnotherVersionPreventsSuccess` | 检查固定启动器、目录、明确更新设置、分支/ref、交接入口和 update_service 哈希；目录存在不等于可运行。未读取或验证本机正式安装，也未把这组检查宣称为整个上游源码树的完整性证明。 |
| 账号实例 | `OneDragonSourceProfile`、`OneDragonAccountBatch.ExecuteAsync`；`StoresOnlyTheBindingAndRejectsSharedInstanceOrIdentity`、`WritesDirectlyToOneSourceProfileAndRejectsAStaleRevision` | 账号绑定实例/根目录，拒绝重复实例，登录配置必须有脱敏身份；每账号执行前重读任务 revision 与游戏路径。详细实例配置仍由 OneDragon 维护。 |
| 本轮完成/跳过 | ce3ebee 的 `OneDragonHandoffEvidence`、`vivian_handoff.py` 原样保留；既有 `CurrentRunSeparatesSourceConfirmedSkipsFromMissingResults`；新增 batch lifecycle owned 用例 | 成功必须有本 RunId、owned PID、对应实例、entered、各任务结果和 end success；success 与 already_done 的进度和汇总分别显示。全部源记录跳过可以完成任务计划，但不是本轮新执行/新奖励证明。 |
| 旧日志/错实例 | 新增 `BatchKeepsTaskEvidenceIdentityAndCleanupSeparate`：foreign_run、foreign_pid、foreign_instance、missing_task、duplicate_task | 错 RunId 不被接纳，错误 PID/实例不能证明 worker 在本进程；缺任务或重复结果不能判成功。输出事件携带实际 RunId，终态关联当前 batchId。 |
| 结果与清理分离 | 同一新增 theory 的 cleanup_failed；释放失败回归测试 | 业务 Success + 清理 Unconfirmed 必须 Blocked 且 registry 不释放。完成文案不替代安全交接。 |
| 停止/异常退出 | 既有 `CancellationTerminatesOnlyOwnedProcessesAndIsNeverFailure`、`CancellationBeforeTheWorkerIsProvenInProcessIsUnconfirmed`、`TerminationThatCannotBeConfirmedIsUnconfirmed`、`LauncherExitingBeforeTheWorkerIsNeverCompletion` | 无绑定本次运行的 graceful 通道；Emergency 仅终止自己 Job；没看到 worker 身份时，Job 空也不能宣称清理完成。 |
| 游戏进程归属 | `ZenlessProcessAttributionTests.ProcessAttributionUsesTheExactExecutableAndLeavesUnownedProcessesAlone` | 无害模拟进程验证精确程序身份、不结束外部进程；不等于游戏实机验收。 |
| 暂停/聊天 | OneDragonRun 不实现 `IPausableEngineRun` | 暂停/继续不支持；不能以这轮测试为依据开放运行中聊天输入。等待 I 的公共安全许可，不改宿主。 |
| 配置恢复 | 批次不通过临时覆盖改写任务源配置；源配置编辑有 revision 校验 | `ConfigurationRestored=true` 表示此 handoff 没有创建需还原的临时配置，不表示对用户全部配置做过恢复。源项目自身运行记录/更新行为需实机核验。 |

新增 8 个测试用例：一个释放异常回归 + 七个 batch lifecycle theory 分支。沿用真实 OneDragonAdapter、OneDragonRun、HandoffEvidence、AccountBatch 单轮方法与内存 journal；仅进程组和安装文件为临时模拟。没有启动 dummy exe、真实引擎或游戏，没有获取正式桌面锁。

## 验证

仅当前命令设置 `DOTNET_ROOT=E:\auto agent game\.dotnet` 并加入 PATH；未改 global.json、项目文件或构建工作流。测试安装/数据库均在各测试唯一临时目录，输出在本工作树 `artifacts/tests-e`。

```powershell
dotnet test tests/VIvian.Tests/VIvian.Tests.csproj -c Release --filter 'FullyQualifiedName~OneDragon|FullyQualifiedName~Zenless' --results-directory artifacts/tests-e --logger 'trx;LogFileName=e-targeted-final.trx'
dotnet test tests/VIvian.Tests/VIvian.Tests.csproj -c Release --results-directory artifacts/tests-e --logger 'trx;LogFileName=e-default.trx'
dotnet test tests/VIvian.Tests/VIvian.Tests.csproj -c Release -p:M5DailyTestBuild=true --results-directory artifacts/tests-e --logger 'trx;LogFileName=e-m5.trx'
```

- RED：1 个失败，原因确为原实现没有传播释放异常（不是编译错误）。
- 修复后针对性：139/139，通过，0 失败、0 跳过。
- 默认全量：1122/1122，通过，0 失败、0 跳过。
- M5 全量：1122/1122，通过，0 失败、0 跳过。默认/M5 各约 1 分 9 秒。
- `git diff --check` 通过；公共 Core/App、安装脚本、HandoffEvidence 和 Python 桥相对 T0 无差异；ce3ebee 仍在祖先链。
- 普通只读改动检查：未发现重要遗留问题；已检查释放时序、重复 Dispose 门和测试边界。不是 DeepSeek。
- DeepSeek：本聊天仍未提供 `deepseek_review`，**blocked，0 轮**；总控已告知另行安排真实审查，不能提前标通过。

## 交总控与后续

- 未改公共 DTO、App、数据库、安装脚本或正式 OneDragon 安装/配置；保留 ce3ebee 结果回传和桥接哈希，不需要重生成或部署 Python 桥。
- 公共缺口继续采用 F 的方案：请求幂等、RunId 与账号/配置绑定、输入前安全许可。当前 OneDragon 专属批次未通过 QueueScheduler 统一超时/日志异常管理；本次修复释放失败不等于已解决全部宿主/超时场景，后续消费 I 契约时应覆盖停机超时、日志写入失败、挂起的原生调用。
- 未验证：正式 v2.6.0 安装当前状态、首次初始化/更新拦截实况、真实单账号登录确认与任务奖励、真实跳过来源、A→B、取消/强停、异常退出及共同桌面恢复。
- 实机由总控串行安排：先固定单账号、显式记录本轮运行/跳过列表及证据；确认停止和清理后再安排第二账号。原神/星铁/绝区零混合队列不在本轮执行。
- D 待总控合流/审查安排后再开始，不在 E 分支改 BetterGI 或重新实现共享执行链。
