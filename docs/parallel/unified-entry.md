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

请从 Explorer 原生会话运行正式数据准备。MSIX 可能把 AppData 的读写转到包的 `LocalCache`，即使显示的路径与普通会话相同。源码使用 SQLite 和 Windows 目录的实际句柄核对来源、辅助文件、暂存和发布结果，并用自动删除的临时文件核对新文件写入的位置。重定向时拒绝继续。已安装的旧构建需要在原生会话核对真实路径。[MSIX 文件虚拟化](https://learn.microsoft.com/windows/msix/desktop/flexible-virtualization)

开发侧用 `scripts/Test-UnifiedCutover.ps1 -PreviousPackageDirectory <旧程序目录> -CandidatePackageDirectory <合流候选目录>` 重跑。脚本在临时目录核对快捷方式目标及 EXE 哈希，用固定 SDK 宿主运行目标同目录的程序 DLL，保留旧版所需的临时环境变量。覆盖模拟替换失败恢复、缺库阻断、有准备库启动和回滚。提权提示由正式安装验收单独核对。

候选 `3242768` 的 Windows M5 回归 937/937，WPF 冒烟通过。以 `aff9a76` 的程序副本演练，临时快捷方式的目标始终相同。失败替换和最终回滚都恢复了旧 EXE 哈希，并重新生成启动证据。正式数据目录尚未发布。

## 首页日常与桌面交接

- 游戏卡片带游戏 ID，只跑该游戏的勾选账号。「一键日常」先跑星铁，再跑原神。每个游戏按保存的账号顺序执行；同一 App 操作锁和停止信号覆盖整批。每个引擎分别取得同一物理桌面锁，持锁期间其他工作树无法操作桌面。阶段切换时若锁已被占用或有待恢复标记，后续引擎无法启动。
- 星铁沿用既有准入和临时配置流程。任一阶段的结果未知、停止或清理未确认时，中止后续阶段。原神账号勾选和顺序沿用 `GameAccount.AutoRun` 和 `Order`，直接保存到合流库。
- 原神执行接口只接受源档案唯一启用「领取每日奖励」。固定包从程序目录的父级 `M4D-downloads/BetterGI_v0.66.0.7z` 读取。正式按钮需等启动、完成信号及清理验收。当前原神驱动会保留客户端，需补进程退出证据，并复验取消或超时后的恢复。
- 「核对桌面交接」先核对物理锁是否属于本档案的原神日常。命中时按本次原神记录恢复，失败时保留锁；无本档案原神待恢复标记时沿用星铁流程。恢复成功显示对应 RunId。

## 单账号原神领奖验收候选

使用 `scripts/Build-M5_2Test.ps1 -UnifiedInstall -GenshinRewardAcceptance -SkipSmoke -Target <新目录>` 构建独立候选。候选须带合流及领奖验收两个构建标记。BetterGI 固定发行包仍从候选目录的父级 `M4D-downloads/BetterGI_v0.66.0.7z` 读取，由后端核对摘要。

首轮参数为 `--genshin-reward-acceptance <验收GUID> <原神A账号GUID> <账号revision>`。两个 GUID 使用连字符格式，revision 为数据库中的非负整数。正式合流程序和原神客户端须先退出，从交互式 Windows 管理员会话启动候选。候选从正式合流库生成一致性快照并核对物理路径、版本和完整性，记录写入 `%LOCALAPPDATA%\VIvian-M6-GenshinAcceptance\<32位验收GUID>`。同一编号的目录已存在时拒绝再次运行。可用 `--genshin-reward-review <验收GUID>` 打开该档案核对恢复。

启动和查阅沿用完整统一主窗口。侧栏标记隔离验收和验收编号，首页显示执行进度，运行日志可查阅隔离快照。配置编辑、常规日常、外部引擎启动和通用清理确认在隔离档案中关闭。原神恢复从首页「核对桌面交接」进入，仍按本次运行证据处理。

窗口就绪且 F10 注册成功后，调用与首页「一键日常」共用的入口，只执行该原神账号，固定 `rewardOnly: true`。原神账号的正式勾选、顺序和任务计划都不参与写入。执行时限 10 分钟，F10 和停止按钮发出同一取消信号。关窗最多等待 90 秒，清理仍未返回时保留窗口。只有后端确认领奖任务、BetterGI 清理和协议恢复，且本轮启动的原神客户端退出后，界面才显示「领奖验收通过」。以隔离档案内 `/acceptance` 规则的运行记录和同一验收编号的日志为证。若需证明本轮首次到账，应先核对 A 的奖励尚未领取。

命令行发起的候选在批次结束、进程清理均已确认、无待恢复记录且可取得干净的桌面锁时自动关闭。验收通过返回退出码 `0`，清理确认但验收未通过或已停止返回 `1`。缺少清理证据时保留窗口，验收日志和 `--genshin-reward-review` 可用于后续核对。
