# March7thAssistant（三月七小助手）M3 上游引擎深度技术审计报告 (M3_MARCH7TH_ANALYSIS.md)

> Gemini 原始研究。文中的版本状态、示例和建议保留原貌，实测状态与冲突见[审查记录](../reviews/M3_GEMINI_RESEARCH_VALIDATION.md)。

文档路径：docs/research/M3_MARCH7TH_ANALYSIS.md

核查日期：2026-11-03

上游仓库：[moesnow/March7thAssistant](https://github.com/moesnow/March7thAssistant)

官方文档：[m7a.top](https://m7a.top/)（[使用教程](https://m7a.top/docs/)）

审计基准版本：正式稳定版 v2026.10.31（Commit 1e77d59，2026-10-31 发布）及构建规范 [March7th Assistant.spec](https://github.com/moesnow/March7thAssistant/blob/main/March7th%20Assistant.spec)

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
<td><p>完全原生支持。包含两个核心控制台入口：</p>
<p>1. m7a.exe：v2026.10.31 新增的免提权 CLI 入口（console=True, uac_admin=False），专门面向外部程序与 AI Agent。</p>
<p>2. March7th Assistant.exe：管理员权限核心任务执行器（console=True, uac_admin=True），打包自 main.py。</p>
<p>3. March7th Launcher.exe：GUI 启动器，亦支持带参启动（如 main -e）。</p></td>
<td><p>- <a href="https://github.com/moesnow/March7thAssistant/blob/main/March7th%20Assistant.spec">March7th Assistant.spec L85-115</a></p>
<p>- <a href="https://github.com/moesnow/March7thAssistant/releases">Releases v2026.10.31</a></p>
<p>- <a href="https://m7a.top/docs/">使用教程·命令行启动</a></p></td>
</tr>
<tr class="odd">
<td><p>2</p></td>
<td><p>是否支持命令行指定日常任务</p></td>
<td><p>已从源码确认</p></td>
<td><p>完全原生支持。可直接通过 CLI 参数指定任务 ID：</p>
<p>- main：完整运行（串行执行全部已刷新日常与周常）</p>
<p>- routine：日常全流程（忽略刷新状态）</p>
<p>- daily：每日实训</p>
<p>- power：清体力（按体力计划刷指定副本）</p>
<p>- currencywars：货币战争积分奖励</p>
<p>- universe：差分宇宙/模拟宇宙</p>
<p>- fight：锄大地（大世界清野）</p></td>
<td><p>- <a href="https://github.com/moesnow/March7thAssistant/blob/main/README.md">README.md 源码运行与帮助</a></p>
<p>- <a href="https://m7a.top/">官网 m7a.top 交互规范</a></p></td>
</tr>
<tr class="even">
<td><p>3</p></td>
<td><p>是否提供 API、IPC 或外部控制接口</p></td>
<td><p>已从源码确认</p></td>
<td><p>1. 结构化 JSON CLI 查询：m7a.exe --status 返回 JSON 状态；m7a.exe --list --json 返回可用任务列表。</p>
<p>2. 日志实时追溯流：m7a.exe --follow latest。</p>
<p>3. 第三方通知通道：支持 OneBot、QQ 开放平台机器人、Webhook、Gotify、Server酱等 20+ 推送。</p></td>
<td><p>- <a href="https://github.com/moesnow/March7thAssistant/blob/main/March7th%20Assistant.spec">March7th Assistant.spec</a></p>
<p>- <a href="https://m7a.top/">官网 m7a.top 说明</a></p>
<p>- <a href="https://github.com/moesnow/March7thAssistant/blob/main/assets/config/config.example.yaml">assets/config/config.example.yaml</a></p></td>
</tr>
<tr class="odd">
<td><p>4</p></td>
<td><p>是否支持静默运行</p></td>
<td><p>已从源码确认</p></td>
<td><p>完全支持。调用 m7a.exe 或 March7th Assistant.exe 为纯控制台模式（无 PySide6 GUI 弹窗），标准输出（stdout/stderr）直接回传至父进程管道；v2026.10.31 起新增 Windows 本地后台操作模式与云游戏无窗口模式。</p></td>
<td><p>- <a href="https://github.com/moesnow/March7thAssistant/blob/main/March7th%20Assistant.spec">March7th Assistant.spec（excludes=['PySide6']）</a></p>
<p>- <a href="https://github.com/moesnow/March7thAssistant/releases">Releases v2026.10.31</a></p></td>
</tr>
<tr class="even">
<td><p>5</p></td>
<td><p>是否支持定时或批量任务</p></td>
<td><p>已从源码确认</p></td>
<td><p>原生支持。内置 scheduled_tasks 计划任务系统（支持按星期配置、冲突跳过 scheduled_on_conflict: skip、链式任务 scheduled_chain_continue_on_failure）；外部程序可直接按需调用单个或批处理 CLI。</p></td>
<td><p>- <a href="https://github.com/moesnow/March7thAssistant/blob/main/assets/config/config.example.yaml">assets/config/config.example.yaml L418-468</a></p></td>
</tr>
<tr class="odd">
<td><p>6</p></td>
<td><p>能否可靠判断任务执行完成</p></td>
<td><p>已从源码确认</p></td>
<td><p>完全可靠（需非阻塞配置配合）。进程返回标准退出码：0 代表成功完成，非 0 代表失败或异常（已在 v2026.8.28 PR #1176 中修复非零退出码逻辑）。关键配置依赖：必须在 config.yaml 设 pause_after_success: false 与 exit_after_failure: true，且附加 --after-finish None。</p></td>
<td><p>- <a href="https://github.com/moesnow/March7thAssistant/releases">PR #1176 / Release v2026.8.28</a></p>
<p>- <a href="https://github.com/moesnow/March7thAssistant/blob/main/assets/config/config.example.yaml">assets/config/config.example.yaml L44-48</a></p></td>
</tr>
<tr class="even">
<td><p>7</p></td>
<td><p>能否获得明确的失败原因</p></td>
<td><p>已从源码确认</p></td>
<td><p>支持。stdout 与 logs/ 日志包含显式错误事件（如 发生错误{error}、清体力未完成、每日实训未完成、遗器背包已满），且失败时保存截图证据。</p></td>
<td><p>- <a href="https://github.com/moesnow/March7thAssistant/blob/main/assets/config/config.example.yaml">assets/config/config.example.yaml 消息模板段</a></p></td>
</tr>
<tr class="odd">
<td><p>8</p></td>
<td><p>是否支持安全停止</p></td>
<td><p>已从源码确认</p></td>
<td><p>1. 热键停止：全局热键（默认 F10，配置项 hotkey_stop_task: f10）。</p>
<p>2. 热键暂停/继续：全局热键（默认 F8，hotkey_pause_task: f8）。</p>
<p>3. 外部进程终止：通过标准进程树回收机制强杀。</p></td>
<td><p>- <a href="https://github.com/moesnow/March7thAssistant/blob/main/assets/config/config.example.yaml">assets/config/config.example.yaml L410-416</a></p>
<p>- <a href="https://github.com/moesnow/March7thAssistant/releases">Releases v2026.9.30</a></p></td>
</tr>
<tr class="even">
<td><p>9</p></td>
<td><p>是否支持外部程序查询执行状态</p></td>
<td><p>已从源码确认</p></td>
<td><p>支持。.\m7a.exe --status 返回 JSON 对象：包含 first_run_completed（免责声明确认状态）、game_path_exists、active_run（当前运行任务信息）等。</p></td>
<td><p>- <a href="https://github.com/moesnow/March7thAssistant/blob/main/March7th%20Assistant.spec">March7th Assistant.spec</a></p>
<p>- <a href="https://m7a.top/">官网 m7a.top AI 助手说明</a></p></td>
</tr>
<tr class="odd">
<td><p>10</p></td>
<td><p>是否需要管理员权限</p></td>
<td><p>已从源码确认</p></td>
<td><p>任务执行必须提权，状态查询免提权。</p>
<p>- m7a.exe --status 不需要管理员权限；</p>
<p>- 实际执行自动化时，因需要向 StarRail.exe 注入键鼠，必须提升至管理员权限（uac_admin=True）。VIvian 本身以管理员运行时调用可无缝继承。</p></td>
<td><p>- <a href="https://github.com/moesnow/March7thAssistant/blob/main/March7th%20Assistant.spec">March7th Assistant.spec L85-115</a></p></td>
</tr>
<tr class="even">
<td><p>11</p></td>
<td><p>是否支持多个实例</p></td>
<td><p>官方文档已说明，但尚未实际测试</p></td>
<td><p>本地游戏依赖单个 StarRail.exe 窗口，因此本地模式单机仅支持单实例执行；云游戏模式下支持多容器/多实例并发，但本地桌面场景下 VIvian 统一遵循单通道串行。</p></td>
<td><p>- <a href="https://m7a.top/docs/">后台运行与 Docker 部署文档</a></p></td>
</tr>
<tr class="odd">
<td><p>12</p></td>
<td><p>是否有独立的配置管理机制</p></td>
<td><p>已从源码确认</p></td>
<td><p>基于纯扁平 YAML 配置。主文件为 {EngineRoot}\config.yaml，首次安装提供 assets\config\config.example.yaml 模板。键名清晰、无复杂多层嵌套，非常易于 VIvian 实施 AST/键值级补丁。</p></td>
<td><p>- <a href="https://github.com/moesnow/March7thAssistant/blob/main/assets/config/config.example.yaml">assets/config/config.example.yaml</a></p></td>
</tr>
</tbody>
</table>

## 2. 任务与配置系统矩阵分析

|          |                                                                    |                                                                                              |                                          |                          |                                                    |
|----------|--------------------------------------------------------------------|----------------------------------------------------------------------------------------------|------------------------------------------|--------------------------|----------------------------------------------------|
| 任务分类 | 内部任务 ID / 配置键                                               | 配置文件映射                                                                                 | 启动与调用方式                           | VIvian 单独调用能力      | 限制与前置要求                                     |
| 完整运行 | main                                                               | 串联 config.yaml 中所有 \*\_enable: true 且处于已刷新周期的子项                              | m7a.exe main --after-finish None         | 原生支持                 | 最推荐的日常全自动化入口                           |
| 每日实训 | daily / daily_enable                                               | config.yaml (daily_enable: true)                                                             | m7a.exe daily --after-finish None        | 原生支持                 | 自动识别并完成实训                                 |
| 清体力   | power / power_enable                                               | instance_type, instance_names, power_plan, build_target_enable                               | m7a.exe power --after-finish None        | 原生支持                 | 支持指定花萼/凝滞虚影/侵蚀隧洞/饰品提取            |
| 货币战争 | currencywars / currencywars_enable                                 | currencywars_type, currencywars_rank_difficulty, currencywars_strategy                       | m7a.exe currencywars --after-finish None | 原生支持                 | 周常玩法，支持超频博弈与速通模式                   |
| 差分宇宙 | weekly_divergent_enable                                            | weekly_divergent_type, weekly_divergent_level, divergent_station_priority                    | 随 main 运行或调用子流程                 | 支持（通过配置开关启用） | 周常积分奖励与自动提取饰品                         |
| 历战余响 | echo_of_war_enable                                                 | echo_of_war_enable: true, echo_of_war_start_day_of_week                                      | 随 main 运行，支持设置每周从周几开始     | 支持（通过配置开关启用） | 每周消耗 3 次减半体力                              |
| 领取奖励 | reward_enable                                                      | 细分为 reward_dispatch_enable (委托), reward_mail_enable (邮件), reward_srpass_enable (勋礼) | 随 main 或 routine 执行                  | 支持                     | 统一在所有副本挑战后领取                           |
| 锄大地   | fight / fight_enable                                               | 挂载 ./3rdparty/Fhoe-Rail                                                                    | m7a.exe fight --after-finish None        | 原生支持                 | 大世界自动跑图清野，耗时较长（需适当放宽超时阈值） |
| 逐光捡金 | 混沌回忆 forgottenhall, 虚构叙事 purefiction, 末日幻影 apocalyptic | 各自独立开关、关卡范围与两队编队                                                             | 随 main 运行                             | 支持（通过配置开关启用） | 建议在原版 GUI 先调好配队阵容                      |

关键架构结论：March7thAssistant 是三款引擎中外部调用契约最规范、CLI 支持最成熟的项目。其支持直接以命令行传参运行具体子任务（main, daily, power, currencywars, fight 等），无需依赖 GUI 界面，且原生输出控制台文本与退出码。

## 3. 日志与任务完成证据提取

1.  日志位置与格式：

- 目录：{EngineRoot}\logs\\每日生成独立 log 文件。
- 控制台流：进程启动时将 StandardOutput 与 StandardError 设为 UTF-8 重定向，可通过管道实时读取与 UI 展示。

2.  退出码判断：

- 当配置 pause_after_success: false 与 exit_after_failure: true 时，成功返回 0，失败返回非 0。

3.  完成与进度特征文本：

- 启动：开始执行：每日实训 或 开始执行：完整运行。
- 完成：每日实训已完成 · 退出码 0、模拟宇宙已完成、清体力已完成。
- 失败：发生错误、清体力未完成。

## 4. 外部调用限制与安全风险

1.  首次免责声明约束（硬阻断点）：

- 官方源码与规范明确规定：用户必须在原版 GUI 界面中本人阅读并点击确认免责声明。未确认前，m7a.exe --status 返回 first_run_completed: false。
- 架构要求：VIvian 严禁通过直接修改配置伪造免责声明，检测到未确认时应弹出友好提示引导用户打开原版界面点击。

2.  高危选项熔断：

- 配置文件中包含 genshin_starRail_fps_unlocker_allow: false（解锁帧率工具）。因涉及游戏进程内存修改，VIvian 适配器必须硬编码强制禁用此功能，绝不调用任何内存修改插件。
