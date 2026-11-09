# M6 原神 BetterGI 接入 · 精简交接

**共同基线** `c44dde18c8fbb21caba770f8799d33b880f8f843`。原神分支 `feature/m6-genshin-bettergi`，草稿 [PR #55](https://github.com/zcwjxy1412/Vivian-cutar/pull/55)。配置界面由共享层接入 [合流 PR #57](https://github.com/zcwjxy1412/Vivian-cutar/pull/57)。正式崩铁继续用 `aff9a76` 与 SQLite schema v6，M6 测试版使用隔离库。

## 当前可用

| 能力 | 状态 |
| --- | --- |
| 逐账号配置 | 隔离 M6 版的「游戏管理 → 原神」支持新增或选择账号，分别保存每日奖励、合成浓缩树脂和自动秘境。三项默认关闭。按账号内部编号保存、回读和审计。 |
| 计划编译 | `BetterGiAccountPlans.Compile` 冻结当前账号的字段。保存时校验选项与次数。隔离档案用不同名称，固定顺序为合成、秘境、领奖，仅包含启用的任务。 |
| 隔离验收入口 | `render-account` 只导出计划；准备脚本支持只读预览。诊断启动前重编译计划并比较完整配置，核对路径、权限、前台客户端和桌面锁。账号诊断需要操作员先确认当前账号。 |
| 执行与恢复 | 拒绝缺失或重复配置、已有实例、跨安装 IPC、进程归属不明。紧急停止只处理本次 Windows Job。具有完整清理证据时，`recover-daily` 能核对并恢复隔离运行改变的协议目标。遇到新修改或证据不全时保留恢复标记。 |
| 批次与识别 | 已实现原神登录卡片的保守识别与离线状态机。合成 OCR 覆盖账号歧义、验证页和异常画面。模拟 A→B 使用独立快照；A 的恢复未确认时阻断 B。 |

## 真实证据与运行限制

- 本机安装为 BetterGI 0.66.0。EXE SHA-256 `73732f440bc8ff3b0272f2adbbf72acadcb952d45bf7f5a62cf0e3573dbd7d73` 与官方留档包提取件一致。[官方发布](https://github.com/babalae/better-genshin-impact/releases/tag/0.66.0)
- 2026-11-09 的两次桌面尝试均在 BetterGI 启动前结束。第一轮发现 Windows 包重定向锁路径，吸收共享修复 `4420cd1` 后锁身份可验证。第二轮发现原神拒绝枚举模块，改用仅查询可执行路径的接口，已对同一客户端完成只读核对。共同锁位于 `%USERPROFILE%\.vivian-m6\desktop-operation.lock`。
- Windows Job 急停使用无害替身验证。BetterGI 实机领奖、协议恢复、登录下拉列表、入场身份与退出仍待验证。已有截图只用于理解账号确认页，未进入仓库。
- 0.66.0 的自动秘境在没有树脂选择弹窗时可能绕过指定次数。可编辑和预览秘境配置，实机执行维持关闭。合成树脂日志只有尝试或跳过，结果缺少入包证据时保持未确认。[秘境源码](https://github.com/babalae/better-genshin-impact/blob/0af68c28f6b0e714d5304625ce644736d508022e/BetterGenshinImpact/GameTask/AutoDomain/AutoDomainTask.cs) · [合成源码](https://github.com/babalae/better-genshin-impact/blob/0af68c28f6b0e714d5304625ce644736d508022e/BetterGenshinImpact/GameTask/Common/Job/GoToCraftingBenchTask.cs)
- 正式原神调度、自动 A→B、暂停和继续保持关闭。恢复报告尚未落盘时需要人工对账。单次诊断不会把进程退出当作任务完成。

## 配置与离线预览

打开合流提交 `a504105` 的隔离版，进入「游戏管理 → 原神」，选择或新增账号，设置后点「保存原神计划」。切换另一账号确认其设置独立。每个账号的方案明确后再安排真机。

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
- 离线导出、只读预览、正确账号绑定、错账号阻断、摘要重算后的危险字段阻断已通过。恢复入口通过 18 项离线守卫检查。
