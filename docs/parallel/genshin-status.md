# M6 原神 BetterGI 接入 · 精简交接

**共同基线** `c44dde18c8fbb21caba770f8799d33b880f8f843`。原神分支 `feature/m6-genshin-bettergi`，草稿 [PR #55](https://github.com/zcwjxy1412/Vivian-cutar/pull/55)。统一程序由共享层接入 [合流 PR #57](https://github.com/zcwjxy1412/Vivian-cutar/pull/57)。星铁功能以 `aff9a76` 为实机基线，保留 SQLite schema v6 原库，合流程序读取统一库。

## 当前可用

| 能力 | 状态 |
| --- | --- |
| 逐账号配置 | 隔离 M6 版的「游戏管理 → 原神」支持新增或选择账号，分别保存每日奖励、合成浓缩树脂和自动秘境。三项默认关闭。按账号内部编号保存、回读和审计。 |
| 计划编译 | `BetterGiAccountPlans.Compile` 冻结当前账号的字段。保存时校验选项与次数。隔离档案用不同名称，固定顺序为合成、秘境、领奖，仅包含启用的任务。`RenderDailyAcceptance` 从已绑定的源档案读取领奖地区和队伍，只输出「领取每日奖励」。 |
| 隔离验收入口 | `render-account` 只导出计划；准备脚本支持只读预览。诊断启动前重编译计划并比较完整配置，核对路径、权限、前台客户端和桌面锁。账号诊断需要操作员先确认当前账号。 |
| 执行与恢复 | 拒绝缺失或重复配置、已有实例、跨安装 IPC、进程归属不明。紧急停止只处理本次 Windows Job。具有完整清理证据时，`recover-daily` 能核对并恢复隔离运行改变的协议目标。遇到新修改或证据不全时保留恢复标记。 |
| 截图建档 | 原神有独立的本地 OCR 入口。导入支持收起的单账号卡片和展开的保存账号列表，列表顶部摘要不计入实际账号。新档案以脱敏手机号命名，旧版自动编号的导入档案也按脱敏号显示。绑定已有档案须提供单个账号，保留原任务设置与自定义备注。UID 可留空，同标识的崩铁档案独立。 |
| 登录验收 | 「游戏管理 → 原神 → 登录验收」使用共同桌面锁。驱动验证国服客户端的 Windows 签名并自动启动或接管已运行客户端，按路径、进程号和启动时间识别窗口。展开下拉后要求可见列表中唯一匹配，重新读取收起后的账号卡片，随后确认大门与两帧连续世界画面。已有 UID 档案须读回一致。登录验收不启动 BetterGI。 |
| 源配置 | 用主程序已发现的 BetterGI 安装目录和账号编号定位源档案。连接时从该账号已保存的计划创建专属一条龙档案。已有档案直接读回，默认档案与全局设置保留。源设置回写检查文件版本、BetterGI 进程和桌面锁。 |
| 资源核验 | 离线验证合成提交前后的原粹和浓缩库存差值，及每次秘境领取前的分类预算。规则需要同一受控游戏会话提供画面和输入证据。 |

## 真实证据与运行限制

- 本机安装为 BetterGI 0.66.0。EXE SHA-256 `73732f440bc8ff3b0272f2adbbf72acadcb952d45bf7f5a62cf0e3573dbd7d73` 与官方留档包提取件一致。[官方发布](https://github.com/babalae/better-genshin-impact/releases/tag/0.66.0)
- 2026-11-09 的两次桌面尝试均在 BetterGI 启动前结束。第一轮发现 Windows 包重定向锁路径，吸收共享修复 `4420cd1` 后锁身份可验证。第二轮发现原神拒绝枚举模块，改用仅查询可执行路径的接口，已对同一客户端完成只读核对。共同锁位于 `%USERPROFILE%\.vivian-m6\desktop-operation.lock`。
- Windows Job 急停使用无害替身验证。BetterGI 实机领奖、协议恢复、原神保存账号下拉、入场身份与退出仍待真机验证。用户提供的原神登录照片已用于临时库导入及重复导入验证。
- 冷启动阶段的签名核验已对本机米哈游签名的 `YuanShen.exe` 和不同发行方的 EXE 各跑一次。桌面模拟核对启动失败后零输入，及 A→B 期间只执行一次启动准备。实际开服登录尚无结果。
- 登录后跑日常需要在同一桌面锁内把已核对的账号和当前游戏进程交给受监管 BetterGI，再验证结果、清理和配置恢复。现有正式安装尚无这条执行入口。
- 登录入口使用国服已打开的 16∶9 客户区和固定版本模板。模板来自本机 BetterGI 脚本仓库，代码记录已核对的摘要，不运行第三方脚本。[脚本来源](https://github.com/babalae/bettergi-scripts-list/tree/16737383d1e770e3f0f7c2b4186ac875c1770c70/repo/js/%E5%88%87%E6%8D%A2%E8%B4%A6%E5%8F%B7Dropdown)
- 0.66.0 的自动秘境在没有树脂选择弹窗时可能绕过指定次数。可编辑和预览秘境配置，实机执行维持关闭。合成树脂日志只有尝试或跳过，结果缺少入包证据时保持未确认。[秘境源码](https://github.com/babalae/better-genshin-impact/blob/0af68c28f6b0e714d5304625ce644736d508022e/BetterGenshinImpact/GameTask/AutoDomain/AutoDomainTask.cs) · [合成源码](https://github.com/babalae/better-genshin-impact/blob/0af68c28f6b0e714d5304625ce644736d508022e/BetterGenshinImpact/GameTask/Common/Job/GoToCraftingBenchTask.cs)
- 正式原神调度、暂停和继续保持关闭。恢复报告尚未落盘时需要人工对账。登录 A→B 和任务清理后的 A→B 分开验收。

## 首页执行接入

- `BetterGiAccountBatch.ExecuteSavedDailyAsync` 使用已保存的账号顺序和 BetterGI 专属源档案。所有账号预检通过后才自动启动游戏。引擎启动前再次核对源档案和入场身份。收尾经过进程清理、原配置摘要和协议读回；完成结果或恢复未确认会阻断续账号。
- 原神开发分支的固定执行范围为「领取每日奖励」，或「合成浓缩树脂（保留 0）→ 领取每日奖励」。本机两个已绑定账号都采用后者，当前原神执行勾选均关闭。受控验收选择账号使用隔离库，保留正式设置。启用了其他任务、源档案有续跑标记或顺序不一致时提前阻断。
- 合成前后均从同一原神窗口读取两帧一致的地图库存。只接受同一 BetterGI 受监管进程在计划合成步骤内的尝试或跳过记录。增量需与原粹树脂消耗相符；没有实际变化时，必须能独立证明库存已满或不足。浓缩图标缺失与识别失败暂不能区分，保持未确认。 BetterGI 0.66.0 的识别素材尚需在当前客户端实机校准。[固定版合成源码](https://github.com/babalae/better-genshin-impact/blob/0af68c28f6b0e714d5304625ce644736d508022e/BetterGenshinImpact/GameTask/Common/Job/GoToCraftingBenchTask.cs)
- 隔离安装从本机固定摘要的 BetterGI 0.66.0 官方归档准备。任务报告在管理员隔离目录，统一运行日志记入结果和报告路径。原神恢复使用专属锁标记，通用星铁交接流程不会清除它。
- A 成功且 BetterGI 进程退出、协议和配置恢复确认后才切 B。失败、取消、结果未确认均停止。收尾只结束本轮启动并保留了可信句柄的原神进程；预先存在的原神客户端保留并标记无法确认清理。
- `VerifySavedDailyAsync` 是同一批次实现的受控入口。首轮只验证 A 领奖，固定安装、任务证据及清理均通过后才验证保存的 A→B 任务。证据范围使用独立的 `/acceptance` 规则版本。正式执行标志仍关闭。当前安装 `0f41e2e` 尚未部署以上改动，本轮未在游戏中点击、领奖或消耗树脂。

## 配置与离线预览

2026-11-09 已将统一版 `3242768` 安装到原 `E:\auto agent game\VIvian\VIvian.exe`。两处「启动 furina」仍指向这一路径。统一版从 `%LOCALAPPDATA%\VIvian-UserData` 读取合流库。旧 v6/v7 原库摘要和归档的旧正式程序摘要已核对。现场启动检查通过，两个原神账号的计划与各自 BetterGI 档案顺序一致。旧测试产物已可逆归档。[单入口与数据合并](https://github.com/zcwjxy1412/Vivian-cutar/blob/integration/m6-dual-engine/docs/parallel/unified-entry.md)

同日已在原路径更新统一版 `0f41e2e`，保留 `3242768` 回滚副本。候选通过隔离目录的同路径替换和回滚演练，原生会话对实际合流库生成了新提交的启动证据。原快捷方式已打开新版主窗口，启动时原神未运行。远程输入无法点击管理员窗口，真机登录需要在统一版内触发一次验收按钮，之后由驱动负责启动游戏。

首次原快捷方式启动发现，打包的 Codex 会话将合流目录和其中一份旧库解析到了包私有路径。已从普通 Windows 会话核对原库的物理句柄并重新合流，正式库含三个原崩铁档案、两个原神档案及已连接任务。原物理库摘要一致，实际安装版在普通会话的启动检查通过，误写入的影子库已归档。

如需只读预览已导出的账号设置，使用普通 PowerShell 运行以下命令。JSON 只含账号任务参数，账号编号是 VIvian 的内部 GUID。

```powershell
$dotnet = 'E:\auto agent game\.dotnet\dotnet.exe'
$probe = 'tools\VIvian.BetterGiProbe'
& $dotnet build "$probe\VIvian.BetterGiProbe.csproj" -c Release -p:RestoreLockedMode=true
$dll = "$probe\bin\Release\net10.0-windows10.0.19041.0\VIvian.BetterGiProbe.dll"
& $dotnet $dll render-account $accountId $settingsJson $gameExecutable | Set-Content $previewJson -Encoding utf8
& "$probe\Prepare-Acceptance.ps1" -Archive $archive -GameExecutable $gameExecutable -AccountId $accountId -AccountPlanPath $previewJson -PlanOnly
```

## 验证

- 配置界面合流 `a504105` 的 Windows M5 回归 832 项通过。WPF 冒烟验证新增账号、保存、回读、原神账号隔离与星铁配置隔离。
- 原神开发提交 `74167531f6b7349954297b668fe50f4b93228034` 通过 Windows M5 Release 全量回归 828 项。BetterGI 诊断工具和 WPF Release 均构建成功，0 警告、0 错误。
- 原神开发提交 `def73a707a32731131249e06ee1c3319faed8cbe` 通过 Windows M5 Release 全量回归 858 项。前一开发提交 `acc0d23` 的 BetterGI 诊断工具和 WPF Release 构建通过，0 警告、0 错误。
- 合流包 `7c98cdcf6777ae13e8f5de3424dce09add7225e2` 通过 Windows M5 Release 全量回归 923 项和 WPF 冒烟。1024×768 渲染检查使用临时数据，ZIP 摘要已核对。[合流 PR #57](https://github.com/zcwjxy1412/Vivian-cutar/pull/57)
- 同一张用户照片验证新建、重复导入和绑定现有计划。测试库与正式数据隔离，原始照片与完整 OCR 未进入仓库。
- 3840×2160 展开列表截图在临时库成功导入两个账号，重导复用两条记录。截图导入、卡片识别和桌面登录定向回归 58 项通过。
- 原神提交 `a94a979` 通过 Windows M5 Release 回归 867 项。统一安装候选 `3242768` 通过 937 项回归、WPF 冒烟、同路径替换和回滚演练。此次只验收配置、安装和启动，原神实机任务未运行。
- 原神 `105ada8` 自动启动与 `3a6b949` 画面识别通过 Windows M5 Release 回归 877 项。用户提交的原始 4K 截图用本机 OCR 和摘要匹配的素材离线读出两个唯一账号。`4af9c3c` 通过全量 881 项回归。只读打开本机两个已绑定源档案，均能生成仅领奖的零树脂计划。
- 离线导出、只读预览、正确账号绑定、错账号阻断、摘要重算后的危险字段阻断已通过。恢复入口通过 18 项离线守卫检查。
