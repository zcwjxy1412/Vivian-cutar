# VIvian 单入口与数据合并方案

## 已核对

- 桌面与项目根目录的「启动 furina」均直接指向 `E:\auto agent game\VIvian\VIvian.exe`，没有附加参数。当前安装为 `aff9a76`。
- 正式数据 `%LOCALAPPDATA%\VIvian-M5-DailyTest\vivian.db` 为 schema v6。原神数据在 `%LOCALAPPDATA%\VIvian-M6-StarRailTest\vivian.db`，为 v7。两库保存的 BetterGI 安装目录一致，目录中存在 `BetterGI.exe`。
- 2026-11-09 已用两库的内存副本演练。复用仓库的 v7 SQL，将两个非旧版占位原神账号及登录绑定、两份已保存设置、两条配置审计导入 v6 副本。所有原 v6 表行逐项相同，完整性检查通过，外键错误为 0。重复导入新增 0 个账号，模拟失败回滚恢复到导入前。此记录验证了现有数据可合并，安装与启动切换仍待实现验证。

## 原神配置和执行

保留「游戏管理 → 原神」的账号选择和一套任务字段。安装目录来自主程序已保存并核验的原神引擎设置，连接后自动解析所选账号的一条龙档案。缺少档案时显示「连接 BetterGI」，从该账号已保存的计划生成。已有可验证的旧绑定可读回，默认档案不参与自动认领。目录不可用时在同页显示原因，并链接到「引擎与环境」处理。

以下接口随原神提交 `b92ba6e` 合流，已接入同一任务页。没有指定账号时优先选择已识别且启用的原神账号。路径后补时自动核对当前账号。任务顺序只读显示，安装级共享树脂预算继续只读。登录验收从 `a94a979` 的接口读取 BetterGI 保存的国服客户端路径，缺少有效值时可手选。

```csharp
Task<BetterGiSourceSettings?> FindAccountProfileAsync(
    string installRoot, Guid accountId, CancellationToken ct = default);

Task<BetterGiSourceSettings> ConnectAccountProfileAsync(
    string installRoot, Guid accountId,
    IReadOnlyDictionary<string, string> savedSettings,
    CancellationToken ct = default);
```

连接后，一条龙档案提供已映射字段的当前值，VIvian 账号设置提供其余字段和该账号的资源预算。BetterGI 安装级树脂设置只读。档案编辑需要能表示已关闭的受支持任务，执行渲染仍仅纳入已启用且通过准入的任务。外部修改读回同一页，覆盖草稿前检查源 revision。底层按归属分别提交和回读，失败的编辑保留在草稿中。只使用已读回的配置生成有效计划。

共享层继续使用 `AccountConfigurationTarget(GameId.Genshin, accountId)` 与 `IAccountConfigurationProvider`。后续统一调度接收冻结后的有效计划及账号、SQLite 设置 revision、源档案身份和 revision。入队和启动前核对同一账号与版本，变更或恢复未确认时阻断。原神模块负责生成和定位档案、映射参数、验证并渲染执行配置。共享层负责 WPF、合并数据、统一调度、停止及恢复门控。实机和树脂操作暂停。

## 数据合并与安装切换

1. 发布前核对正式和旧测试进程均已退出，检查两套数据目录的单实例锁、共享桌面锁及未完成的清理和恢复。SQLite 用 `BackupDatabase` 取得一致快照，在新的暂存目录用 `LocalDatabase.InitializeAsync` 将正式库副本升到 v7。原库作为回滚来源保留。[SQLite Backup API](https://sqlite.org/backup.html)
2. 在目标副本的一个事务里导入 v7 的非占位原神账号，以及同账号的 `account_login_profiles`、`account_settings`、`account_task_plans` 和 `account_configuration_audit`。保留 GUID 和 revision。完全相同的记录可重试。ID、原神登录标识或审计 ID 冲突时停止并报告，保留双方。崩铁和绝区零沿用正式库的数据，隔离版的运行记录保留在来源库。
3. 用正式库副本核对原有账号、设置、历史和固定引擎路径。核对两个原神账号的登录绑定、计划和审计可读，并验证失败回滚、重复导入和安装中断。现有 `%LOCALAPPDATA%\VIvian` 已有旧库，正式合流数据目录暂定 `%LOCALAPPDATA%\VIvian-UserData`。核对无同名数据后再发布。正式构建只接受已准备并校验的目标库，缺失时提示恢复，避免误启动到空库。
4. 合流版通过 Windows 门禁及约定的实机验收后，校验自包含包并在同一盘暂存。将正式安装目录整体移入回滚归档，再将新包放进原 `E:\auto agent game\VIvian`，让现有快捷方式自动进入同一程序。切换失败时恢复旧目录。回滚到 `aff9a76` 时继续使用原 v6 库，保留合流版新增数据以便后续恢复。
5. 单入口验收后再清理已核对的重复测试包和多余快捷方式。正式依赖、素材、数据库、唯一回滚副本和验收证据列入保留清单。

## 离线候选与复验

开发侧构建使用 `scripts/Build-M5_2Test.ps1 -UnifiedInstall -Target <新目录>`。仅这个构建接受 `VIvian-UnifiedInstall.marker`。正常启动固定读 `%LOCALAPPDATA%\VIvian-UserData\vivian.db`，同时检查 v7 版本、合流标识及 SQLite 完整性。缺库或只放入普通 v7 库时拒绝启动。

两套源程序退出并且桌面交接完成后，可用合流候选的 `VIvian.exe --prepare-unified-profile <尚不存在的绝对暂存目录>` 生成可切换的数据。来源固定为上文 v6 和 v7 目录，输入只读。准备过程中同时持有两个来源的单实例锁和桌面锁。冲突、未清理运行或未完成的恢复会阻断；成功通过检查后才出现指定暂存目录。切换前需再核对两套源库没有被其他程序重新打开或修改。

开发侧用 `scripts/Test-UnifiedCutover.ps1 -PreviousPackageDirectory <旧程序目录> -CandidatePackageDirectory <合流候选目录>` 重跑。脚本在临时目录核对快捷方式目标及 EXE 哈希，用固定 SDK 宿主运行目标同目录的程序 DLL，保留旧版所需的临时环境变量。覆盖模拟替换失败恢复、缺库阻断、有准备库启动和回滚。提权提示由正式安装验收单独核对。

候选 `3242768` 的 Windows M5 回归 937/937，WPF 冒烟通过。以 `aff9a76` 的程序副本演练，临时快捷方式的目标始终相同。失败替换和最终回滚都恢复了旧 EXE 哈希，并重新生成启动证据。正式数据目录尚未发布。
