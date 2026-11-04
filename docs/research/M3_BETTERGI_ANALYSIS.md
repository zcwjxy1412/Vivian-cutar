# BetterGI（更好的原神）M3 上游引擎深度技术审计报告 (M3_BETTERGI_ANALYSIS.md)

> Gemini 原始研究。文中的版本状态、示例和建议保留原貌，实测状态与冲突见[审查记录](../reviews/M3_GEMINI_RESEARCH_VALIDATION.md)。

文档路径：docs/research/M3_BETTERGI_ANALYSIS.md

核查日期：2026-11-03

上游仓库：[babalae/better-genshin-impact](https://github.com/babalae/better-genshin-impact)

官方文档：[bettergi.com](https://www.bettergi.com/)（旧域名 bgi.huiyadan.com 已迁移，文档源码位于 [huiyadanli/bettergi-docs](https://github.com/huiyadanli/bettergi-docs)）

审计基准版本：正式稳定版 v0.66.0（Commit 0af68c2，2026-09-26 发布）与重构主分支 refactor/20260930（Commit b31da1c914 / 819d45f，2026-10 ~ 2026-11）

## 1. 上游实际调用接口深度分析（12 项核心维度）

<table>
<colgroup>
<col style="width: 20%" />
<col style="width: 20%" />
<col style="width: 20%" />
<col style="width: 20%" />
<col style="width: 20%" />
</colgroup>
<tbody>
<tr class="odd">
<td><p>序号</p></td>
<td><p>审计维度</p></td>
<td><p>结论分类</p></td>
<td><p>详细技术结论与行为机制</p></td>
<td><p>源码位置 / 官方文档 / 可复现证据</p></td>
</tr>
<tr class="even">
<td><p>1</p></td>
<td><p>是否支持命令行启动</p></td>
<td><p>已从源码确认</p></td>
<td><p>支持。程序为 WPF 应用，入口在 App.xaml.cs，使用 .NET Generic Host。启动时在 MainWindowViewModel.cs 中检测 Environment.GetCommandLineArgs().Length &gt; 1，若带参数启动，自动跳过更新检查弹窗、首次欢迎弹窗和目录迁移逻辑。</p></td>
<td><p>- <a href="https://gitback.org/babalae/better-genshin-impact/src/commit/2a2acb5b0df635a5293507916fa1d1db3cab4aaf/BetterGenshinImpact/App.xaml.cs">BetterGenshinImpact/App.xaml.cs</a></p>
<p>- <a href="https://gitback.org/babalae/better-genshin-impact/src/commit/900b6aec84911b8af00042e60984beb7ffbc4a0a/BetterGenshinImpact/ViewModel/MainWindowViewModel.cs">BetterGenshinImpact/ViewModel/MainWindowViewModel.cs L78-83</a></p></td>
</tr>
<tr class="odd">
<td><p>2</p></td>
<td><p>是否支持命令行指定日常任务</p></td>
<td><p>已从源码确认</p></td>
<td><p>支持。核心参数为 startOneDragon（或 --startOneDragon，因为代码采用 args[1].Contains("startOneDragon")），支持后接配置名如 BetterGI.exe startOneDragon 默认配置。同时支持 --startGroups 组1 组2 顺序执行指定配置组。</p></td>
<td><p>- <a href="https://gitback.org/babalae/better-genshin-impact/src/commit/436a8d207ee302763edb207a545093b4cf22340d/BetterGenshinImpact/ViewModel/Pages/OneDragonFlowViewModel.cs">BetterGenshinImpact/ViewModel/Pages/OneDragonFlowViewModel.cs L572-598</a></p>
<p>- 调度器参数：<a href="https://gitback.org/babalae/better-genshin-impact/src/commit/a9150054c461d22824bba84bad2ba8e02c74de38">Commit a9150054c4</a></p></td>
</tr>
<tr class="even">
<td><p>3</p></td>
<td><p>是否提供 API、IPC 或外部控制接口</p></td>
<td><p>已从源码确认</p></td>
<td><p>1. URI Protocol：启动时调用 UrlProtocolHelper.RegisterAsync() 注册 bettergi:// 协议。</p>
<p>2. 多实例 IPC：Service/Instance/InstanceIpcProtocol.cs 用于主进程与分身进程通信。</p>
<p>3. ExternalAccess 服务（refactor/20260930 分支新增）：包含 HTTP / WebSocket / MCP 接口。</p></td>
<td><p>- <a href="https://gitback.org/babalae/better-genshin-impact/src/commit/2a2acb5b0df635a5293507916fa1d1db3cab4aaf/BetterGenshinImpact/App.xaml.cs">App.xaml.cs</a></p>
<p>- <a href="https://gitback.org/babalae/better-genshin-impact/src/commit/7883cc2d2927016b4d838b05abd7c08345fb1165/BetterGenshinImpact/Helpers">Helpers/UrlProtocolHelper.cs</a></p>
<p>- <a href="https://github.com/babalae/better-genshin-impact/blob/refactor/20260930/CONTEXT.md">CONTEXT.md</a></p></td>
</tr>
<tr class="odd">
<td><p>4</p></td>
<td><p>是否支持静默运行</p></td>
<td><p>必须在 Windows 实机验证</p></td>
<td><p>部分支持/带限制。无专用无界面 --headless 参数；带参数启动仍会创建 WPF 主窗口，但若配置了完成后退出，窗口最小化或隐藏在后台。此外，v0.63.0+ 提供了“桌面分身（ChildSession）”，利用 Windows RDP 子会话实现不占用前台键鼠运行。</p></td>
<td><p>- 桌面分身源码：BetterGenshinImpact/Service/ChildSession/</p>
<p>- 社区实机分析：<a href="https://github.com/moesnow/March7thAssistant/issues/1180">Issue #1180</a></p></td>
</tr>
<tr class="even">
<td><p>5</p></td>
<td><p>是否支持定时或批量任务</p></td>
<td><p>已从源码确认</p></td>
<td><p>支持。内部一条龙本质即任务串行管道；调度器通过 --startGroups 支持批量执行；稳定版外部依赖 Windows 任务计划程序，开发分支正在引入内置 Pulonia 定时调度系统。</p></td>
<td><p>- <a href="https://github.com/babalae/better-genshin-impact/issues/1678">Issue #1678</a></p>
<p>- <a href="https://gitback.org/babalae/better-genshin-impact/src/commit/436a8d207ee302763edb207a545093b4cf22340d/BetterGenshinImpact/ViewModel/Pages/OneDragonFlowViewModel.cs">OneDragonFlowViewModel.cs</a></p></td>
</tr>
<tr class="odd">
<td><p>6</p></td>
<td><p>能否可靠判断任务执行完成</p></td>
<td><p>已从源码确认</p></td>
<td><p>不能仅靠退出码，必须依赖日志标记。进程正常关闭退出码为 0；一条龙运行完成的唯一可靠证据是日志中输出 一条龙和配置组任务结束（NotificationEvent.DragonEnd）。</p></td>
<td><p>- <a href="https://gitback.org/babalae/better-genshin-impact/src/commit/436a8d207ee302763edb207a545093b4cf22340d/BetterGenshinImpact/ViewModel/Pages/OneDragonFlowViewModel.cs">OneDragonFlowViewModel.cs L675-690</a></p></td>
</tr>
<tr class="even">
<td><p>7</p></td>
<td><p>能否获得明确的失败原因</p></td>
<td><p>已从源码确认</p></td>
<td><p>可以从日志中捕获。日志输出明确事件：如 任务启动失败：当前存在正在运行中的独立任务、任务被取消，退出执行、PaddleOcr预热异常、执行配置组任务时失败，未处理异常输出 UnHandle Exception [FATAL]。</p></td>
<td><p>- <a href="https://gitback.org/babalae/better-genshin-impact/blame/commit/5f02b26cd295fd97bc1ee3968cc322055eee7a69/BetterGenshinImpact/GameTask/TaskRunner.cs">TaskRunner.cs</a></p>
<p>- <a href="https://gitback.org/babalae/better-genshin-impact/src/commit/b2631ed362907b786d7a63f360509f9f119e0d52/BetterGenshinImpact/App.xaml.cs">App.xaml.cs L286-345</a></p></td>
</tr>
<tr class="odd">
<td><p>8</p></td>
<td><p>是否支持安全停止</p></td>
<td><p>已从源码确认</p></td>
<td><p>内部支持，外部缺乏专用 CLI 停止命令。内部通过全局热键触发 CancellationContext.Instance.Cts.Cancel()；外部若调用 taskkill 强制终止，不会释放残留按键状态，VIvian 适配器终止后需调用 Win32 释放键鼠。</p></td>
<td><p>- <a href="https://gitback.org/babalae/better-genshin-impact/blame/commit/5f02b26cd295fd97bc1ee3968cc322055eee7a69/BetterGenshinImpact/GameTask/TaskRunner.cs">Simulation.ReleaseAllKey()</a></p>
<p>- <a href="https://gitback.org/babalae/better-genshin-impact/src/commit/436a8d207ee302763edb207a545093b4cf22340d/BetterGenshinImpact/ViewModel/Pages/OneDragonFlowViewModel.cs">CancellationContext.cs</a></p></td>
</tr>
<tr class="even">
<td><p>9</p></td>
<td><p>是否支持外部程序查询执行状态</p></td>
<td><p>已从源码确认</p></td>
<td><p>稳定版仅能通过实时读取共享日志文件获取状态；开发分支 refactor/20260930 的 ExternalAccessHost 正在增加 HTTP / WebSocket 状态推送。</p></td>
<td><p>- <a href="https://gitback.org/babalae/better-genshin-impact/src/commit/b2631ed362907b786d7a63f360509f9f119e0d52/BetterGenshinImpact/App.xaml.cs">App.xaml.cs</a></p>
<p>- <a href="https://github.com/babalae/better-genshin-impact/blob/refactor/20260930/CONTEXT.md">CONTEXT.md</a></p></td>
</tr>
<tr class="odd">
<td><p>10</p></td>
<td><p>是否需要管理员权限</p></td>
<td><p>已从源码确认</p></td>
<td><p>强制要求管理员权限。App.xaml.cs 注册 .UseElevated()。非管理员启动时调用 ShellExecute("runas") 重启自身，当前进程立即以退出码 559 退出。</p></td>
<td><p>- <a href="https://gitback.org/babalae/better-genshin-impact/src/commit/4bf302b01325400342e09fd679532e78e2609be1/BetterGenshinImpact/Helpers/RuntimeHelper.cs">RuntimeHelper.cs L81-102</a></p></td>
</tr>
<tr class="even">
<td><p>11</p></td>
<td><p>是否支持多个实例</p></td>
<td><p>已从源码确认</p></td>
<td><p>稳定版为单实例强互斥锁（UseSingleInstance("BetterGI")，冲突退出码 0xFFFF）；v0.63+ 引入多实例框架，但同一 Windows 会话仅允许一个 primary 实例，其余为 RDP 分身或云原神实例。</p></td>
<td><p>- <a href="https://gitback.org/babalae/better-genshin-impact/src/commit/4bf302b01325400342e09fd679532e78e2609be1/BetterGenshinImpact/Helpers/RuntimeHelper.cs">RuntimeHelper.cs L104-115</a></p>
<p>- <a href="https://github.com/babalae/better-genshin-impact/releases">Releases 0.63.0</a></p></td>
</tr>
<tr class="odd">
<td><p>12</p></td>
<td><p>是否有独立的配置管理机制</p></td>
<td><p>已从源码确认</p></td>
<td><p>有完善的 JSON 配置机制。包含 User/config.json（全局配置）、User/OneDragon/*.json（一条龙配置）、User/ScriptGroup/*.json（调度器配置组）。</p></td>
<td><p>- <a href="https://gitback.org/babalae/better-genshin-impact/src/commit/2a2acb5b0df635a5293507916fa1d1db3cab4aaf/BetterGenshinImpact/App.xaml.cs">ConfigService.cs</a></p>
<p>- <a href="https://gitback.org/babalae/better-genshin-impact/src/commit/436a8d207ee302763edb207a545093b4cf22340d/BetterGenshinImpact/ViewModel/Pages/OneDragonFlowViewModel.cs">OneDragonFlowConfigFolder</a></p></td>
</tr>
</tbody>
</table>

## 2. 深入任务与配置系统：功能与调用对照

|                   |                                               |                                                                               |                              |                                              |                                                                                        |
|-------------------|-----------------------------------------------|-------------------------------------------------------------------------------|------------------------------|----------------------------------------------|----------------------------------------------------------------------------------------|
| 自动化功能        | 真实内部名称                                  | 配置存储位置                                                                  | 启动与调用方式               | VIvian 单独选择能力                          | 限制与前置条件                                                                         |
| 每日委托 / 历练点 | 领取每日奖励 (凯瑟琳领奖+历练点兑换+探索派遣) | User/OneDragon/\*.json (TaskEnabledList\["领取每日奖励"\])                    | 勾选一条龙子项或独立任务     | 支持（通过预先写入配置文件指定子任务布尔值） | 需角色位于大世界且无剧情对话阻碍                                                       |
| 树脂消耗 / 合成   | 合成树脂 (GoToCraftingBenchTask)              | User/OneDragon/\*.json (TaskEnabledList\["合成树脂"\] + CraftingBenchCountry) | 一条龙子项                   | 支持（支持指定枫丹/璃月/蒙德/稻妻合成台）    | 需背包有晶核与浓缩图纸                                                                 |
| 秘境挂机          | 自动秘境 (AutoDomainTask)                     | User/OneDragon/\*.json (TaskEnabledList\["自动秘境"\] + DomainName)           | 一条龙子项或独立任务         | 支持                                         | 分辨率 ![原文内联图示](assets/gemini-m3/ae39badde5d0.png) (16:9)，需配置好自动战斗策略 |
| 首领讨伐          | 自动首领讨伐 (AutoBossTask)                   | User/OneDragon/\*.json (TaskEnabledList\["自动首领讨伐"\])                    | 一条龙子项 (v0.62+)          | 支持                                         | 需配置首领名称、队伍及树脂补充上限                                                     |
| 探索派遣          | 包含在 领取每日奖励 内                        | User/config.json (AutoDailyRewardConfig)                                      | 随每日奖励执行               | 不支持独立运行，绑定于凯瑟琳对话流           | 需预先在游戏内派遣完成一次                                                             |
| 尘歌壶收集        | 领取尘歌壶奖励 (SereniteaPotTask)             | User/OneDragon/\*.json (TaskEnabledList\["领取尘歌壶奖励"\])                  | 一条龙子项                   | 支持                                         | 支持大地图传送或小道具进入                                                             |
| 地脉花挑战        | 自动刷地脉花 (AutoLeyLineOutcrop)             | User/OneDragon/\*.json (TaskEnabledList\["自动刷地脉花"\])                    | 一条龙子项                   | 支持                                         | 依赖冒险之证定位                                                                       |
| 幽境危战          | 自动幽境危战 (AutoStygianOnslaught)           | User/OneDragon/\*.json (TaskEnabledList\["自动幽境危战"\])                    | 一条龙子项 (v0.63+)          | 支持                                         | 主要用于难度三刷取圣遗物                                                               |
| 纪行奖励          | CheckRewardsTask / ClaimBattlePassRewardsTask | 硬编码在一齐龙尾部                                                            | 一条龙走完后自动执行         | 不支持取消，为一条龙结束时默认检测           | 自动按 F4 打开纪行并领奖                                                               |
| 自定义路径追踪    | AutoPathing (采集/挖矿/锄地)                  | User/AutoPathing/\*.json 挂载至 User/ScriptGroup/\*.json                      | 挂载进一条龙或 --startGroups | 支持                                         | 小地图必须设为“锁定方向”，禁止任何滤镜                                                 |

关键架构结论：BetterGI 不支持在 CLI 参数行内直接指定具体的子任务开关，CLI 仅能传递“一条龙配置组名称”。VIvian 实现“独立任务勾选”的唯一正确途径是：在调用 BetterGI.exe startOneDragon \<ConfigName\> 之前，由 VIvian 适配器在磁盘上动态生成或打补丁修改目标配置组 JSON 文件（User/OneDragon/\<ConfigName\>.json 中的 TaskEnabledList）。

## 3. 日志、证据链与完成状态判定

1.  日志文件实际位置与编码：

- 路径：{EngineRoot}\log\better-genshin-impact{yyyyMMdd}.log
- 编码：UTF-8 无 BOM。
- 轮转：Serilog 日级滚动（rollingInterval: RollingInterval.Day），支持多进程只读共享读（shared: true）。

2.  能否区分当前任务与上一次任务：

- 能区分。VIvian 在启动进程前记录当前日志文件大小（Byte Offset）；启动后读取新偏移之后的增量日志。
- 启动时会输出：\[INF\] ... 启用一条龙配置：\<Name\>。

3.  结构化完成与失败证据：

- 完成证明：必须顺序出现 启用一条龙配置：... ![原文内联图示](assets/gemini-m3/15a864e59603.png) 逐项进度 一条龙任务执行: X/Y ![原文内联图示](assets/gemini-m3/15a864e59603.png) 终末确认 一条龙和配置组任务结束。
- 中断与失败证明：出现 任务被取消，退出执行 或 UnHandle Exception \[FATAL\] 或长时间（8 分钟）无任何新日志输出。

4.  退出码陷阱：

- 若 OneDragonFlowConfig.CompletionAction 设为 "关闭游戏和软件"，正常完成退出码为 0；
- 但若子任务报错被内部 catch 吞掉后正常退出，退出码依然是 0。因此 Codex 绝不能将退出码 0 单独作为成功依据，必须校验日志终末标记。

## 4. 进程拓扑与权限风险

1.  游戏启动行为：若配置了联动启动原神，BetterGI 会自动定位注册表中的原神安装路径并拉起 YuanShen.exe（国服）或 GenshinImpact.exe（国际服）。
2.  提权脱离 JobObject 风险：

- 若 VIvian 未以管理员权限运行而拉起 BetterGI.exe，BetterGI 内部会触发 RuntimeHelper.RestartAsElevated()，通过 Windows Shell runas 重新以管理员拉起一个独立新进程，并让当前被 VIvian 监控的子进程以代码 559 退出。这会导致新进程彻底脱离 VIvian 的 Windows Job Object 进程树管控！
- 架构对策：VIvian 本身必须强制要求管理员启动（app.manifest 配置 requireAdministrator），确保拉起 BetterGI 时直接继承管理员令牌，禁止触发任何二次提权。

3.  单实例互斥冲突：若系统中已存在一个运行中的 BetterGI.exe（如托盘残留），再次带参数调用将直接被单实例锁击杀（退出码 65535）。VIvian 必须在启动前主动扫描并清理残留进程。
