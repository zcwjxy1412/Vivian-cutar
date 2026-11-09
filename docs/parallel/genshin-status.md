# M6 原神 BetterGI 接入 · 精简交接

**共同基线** `c44dde18c8fbb21caba770f8799d33b880f8f843`。原神分支 `feature/m6-genshin-bettergi`，草稿 [PR #55](https://github.com/zcwjxy1412/Vivian-cutar/pull/55)。配置界面由共享层接入 [合流 PR #57](https://github.com/zcwjxy1412/Vivian-cutar/pull/57)。已安装统一版为 `3242768`，使用合流 v7；旧崩铁 v6 保留用于回滚。冷启动登录是后续源码增量，尚未部署。

## 当前可用

| 能力 | 状态 |
| --- | --- |
| 逐账号配置 | 隔离 M6 版的「游戏管理 → 原神」支持新增或选择账号，分别保存每日奖励、合成浓缩树脂和自动秘境。三项默认关闭。按账号内部编号保存、回读和审计。 |
| 计划编译 | `BetterGiAccountPlans.Compile` 冻结当前账号的字段。保存时校验选项与次数。隔离档案用不同名称，固定顺序为合成、秘境、领奖，仅包含启用的任务。 |
| 隔离验收入口 | `render-account` 只导出计划；准备脚本支持只读预览。诊断启动前重编译计划并比较完整配置，核对路径、权限、前台客户端和桌面锁。账号诊断需要操作员先确认当前账号。 |
| 执行与恢复 | 拒绝缺失或重复配置、已有实例、跨安装 IPC、进程归属不明。紧急停止只处理本次 Windows Job。具有完整清理证据时，`recover-daily` 能核对并恢复隔离运行改变的协议目标。遇到新修改或证据不全时保留恢复标记。 |
| 截图建档 | 原神有独立的本地 OCR 入口。导入支持收起的单账号卡片和展开的保存账号列表，列表顶部摘要不计入实际账号。新档案以脱敏手机号命名，旧版自动编号的导入档案也按脱敏号显示。绑定已有档案须提供单个账号，保留原任务设置与自定义备注。UID 可留空，同标识的崩铁档案独立。 |
| 登录验收 | 统一版从「游戏管理 → 原神 → 登录验收」进入，沿用原 EXE 启动时的 Windows 授权。驱动在桌面锁内验证国服客户端的 Windows 签名并自动启动或接管已运行客户端，按路径、进程号和启动时间识别窗口。展开下拉后要求可见列表中唯一匹配，重新读取收起后的账号卡片，随后确认大门与两帧连续世界画面。已有 UID 档案须读回一致。支持取消与 F10，结束后保留游戏客户端。实机 A→B 待验。 |
| 源配置 | 用主程序已发现的 BetterGI 安装目录和账号编号定位源档案。连接时从该账号已保存的计划创建专属一条龙档案。已有档案直接读回，默认档案与全局设置保留。源设置回写检查文件版本、BetterGI 进程和桌面锁。 |
| 资源核验 | 离线验证合成提交前后的原粹和浓缩库存差值，及每次秘境领取前的分类预算。规则需要同一受控游戏会话提供画面和输入证据。 |

## 真实证据与运行限制

- 本机安装为 BetterGI 0.66.0。EXE SHA-256 `73732f440bc8ff3b0272f2adbbf72acadcb952d45bf7f5a62cf0e3573dbd7d73` 与官方留档包提取件一致。[官方发布](https://github.com/babalae/better-genshin-impact/releases/tag/0.66.0)
- 2026-11-09 的两次桌面尝试均在 BetterGI 启动前结束。第一轮发现 Windows 包重定向锁路径，吸收共享修复 `4420cd1` 后锁身份可验证。第二轮发现原神拒绝枚举模块，改用仅查询可执行路径的接口，已对同一客户端完成只读核对。共同锁位于 `%USERPROFILE%\.vivian-m6\desktop-operation.lock`。
- Windows Job 急停使用无害替身验证。BetterGI 实机领奖、协议恢复、原神保存账号下拉、入场身份与退出仍待真机验证。用户提供的原神登录照片已用于临时库导入及重复导入验证。
- 冷启动阶段的签名核验已对本机米哈游签名的 `YuanShen.exe` 和不同发行方的 EXE 各跑一次。桌面模拟核对启动失败后零输入，及 A→B 期间只执行一次启动准备。实际开服登录尚无结果。
- 登录入口使用国服已打开的 16∶9 客户区和固定版本模板。模板来自本机 BetterGI 脚本仓库，代码记录已核对的摘要，不运行第三方脚本。[脚本来源](https://github.com/babalae/bettergi-scripts-list/tree/16737383d1e770e3f0f7c2b4186ac875c1770c70/repo/js/%E5%88%87%E6%8D%A2%E8%B4%A6%E5%8F%B7Dropdown)
- 0.66.0 的自动秘境在没有树脂选择弹窗时可能绕过指定次数。可编辑和预览秘境配置，实机执行维持关闭。合成树脂日志只有尝试或跳过，结果缺少入包证据时保持未确认。[秘境源码](https://github.com/babalae/better-genshin-impact/blob/0af68c28f6b0e714d5304625ce644736d508022e/BetterGenshinImpact/GameTask/AutoDomain/AutoDomainTask.cs) · [合成源码](https://github.com/babalae/better-genshin-impact/blob/0af68c28f6b0e714d5304625ce644736d508022e/BetterGenshinImpact/GameTask/Common/Job/GoToCraftingBenchTask.cs)
- 正式原神调度、暂停和继续保持关闭。恢复报告尚未落盘时需要人工对账。登录 A→B 和任务清理后的 A→B 分开验收。

## 配置与离线预览

原神账号和计划当前位于 `%LOCALAPPDATA%\VIvian-M6-StarRailTest`。正式入口切换前，将其与现有崩铁库做一致性备份，并核对账号、计划、设置和回滚。单入口方案由合流分支维护。[单入口与数据合并](https://github.com/zcwjxy1412/Vivian-cutar/blob/integration/m6-dual-engine/docs/parallel/unified-entry.md)

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
- 原神提交 `b92ba6e` 通过 Windows M5 Release 回归 866 项。定向验证从两份已有计划生成独立源档案、核对实际任务顺序、重复连接保留外部修改，以及文件冲突和引擎占用阻断。当前实机和正式入口切换暂停。
- 离线导出、只读预览、正确账号绑定、错账号阻断、摘要重算后的危险字段阻断已通过。恢复入口通过 18 项离线守卫检查。
