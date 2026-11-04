# ZenlessZoneZero-OneDragon（绝区零一条龙）M3 上游引擎深度技术审计报告 (M3_ONEDRAGON_ANALYSIS.md)

> Gemini 原始研究。文中的版本状态、示例和建议保留原貌，实测状态与冲突见[审查记录](../reviews/M3_GEMINI_RESEARCH_VALIDATION.md)。

文档路径：docs/research/M3_ONEDRAGON_ANALYSIS.md

核查日期：2026-11-03

上游仓库：[OneDragon-Anything/ZenlessZoneZero-OneDragon](https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon)

官方文档：[one-dragon.com/zzz/zh/](https://one-dragon.com/zzz/zh/)（[快速开始](https://one-dragon.com/zzz/zh/quickstart.html) 与 [千机链配置](https://one-dragon.com/tools/zh/script_chainer.html)）

审计基准版本：正式稳定版 v2.6.0（Commit 5f2e473，2026-10-30 发布）及 GitHub Actions 构建配置 [build-release.yml](https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon/blob/main/.github/workflows/build-release.yml)

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
<td><p>官方文档已说明，但尚未实际测试</p></td>
<td><p>支持。官方提供两大启动器入口：OneDragon-RuntimeLauncher.exe（集成启动器）与 OneDragon-Launcher.exe（原始启动器）。支持命令行传参启动。</p></td>
<td><p>- <a href="https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon/blob/main/.github/workflows/build-release.yml">build-release.yml 构建规范</a></p>
<p>- <a href="https://one-dragon.com/zzz/zh/quickstart.html">快速开始-高级启动参数</a></p></td>
</tr>
<tr class="odd">
<td><p>2</p></td>
<td><p>是否支持命令行指定日常任务</p></td>
<td><p>官方文档已说明，但尚未实际测试</p></td>
<td><p>支持一条龙级别调用与多账号实例指定。官方参数：-o, --onedragon（直接执行一条龙）、-c, --close-game（结束后关闭游戏）、-i INSTANCE, --instance INSTANCE（指定账号实例如 -i 1,2）。不支持在 CLI 单行中动态指定细分子任务（子任务需读取 config/ 下的配置）。</p></td>
<td><p>- <a href="https://one-dragon.com/zzz/zh/quickstart.html">快速开始-高级启动参数</a></p>
<p>- <a href="https://one-dragon.com/tools/zh/script_chainer.html">千机链预设清单</a></p></td>
</tr>
<tr class="even">
<td><p>3</p></td>
<td><p>是否提供 API、IPC 或外部控制接口</p></td>
<td><p>已从源码确认</p></td>
<td><p>1. MCP 后端服务：源码 src/zzz_od/backend/entry/server.py 提供 HTTP MCP 接口（http://127.0.0.1:23001/mcp），向外部 Agent 暴露窗口状态、截图、OCR、进游戏能力。</p>
<p>2. 启动器版本与帮助查询：-v, --version 与 -h, --help。</p></td>
<td><p>- <a href="https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon/blob/main/docs/develop/setup/quickstart.md">docs/develop/setup/quickstart.md §③</a></p>
<p>- <a href="https://one-dragon.com/zzz/zh/quickstart.html">快速开始-高级启动参数</a></p></td>
</tr>
<tr class="odd">
<td><p>4</p></td>
<td><p>是否支持静默运行</p></td>
<td><p>必须在 Windows 实机验证</p></td>
<td><p>部分支持/待实机验证。当带 -o 运行时直接执行一条龙；但启动器底层调用 PySide6 应用，在启动时是否会在任务栏或桌面闪烁主界面，需实机验证；后台模式（测试版）依赖虚拟手柄（ViGEmBus）+ PrintWindow 截图。</p></td>
<td><p>- <a href="https://one-dragon.com/zzz/zh/setting/setting_game.html">setting_game.html 后台模式说明</a></p>
<p>- <a href="https://one-dragon.com/zzz/zh/quickstart.html">快速开始</a></p></td>
</tr>
<tr class="even">
<td><p>5</p></td>
<td><p>是否支持定时或批量任务</p></td>
<td><p>官方文档已说明，但尚未实际测试</p></td>
<td><p>支持。通过 -i 1,2 可批量连续执行多个账号实例；官方专门开发了多脚本链式调用工具 <a href="https://one-dragon.com/tools/zh/script_chainer.html">千机链 (ScriptChainer)</a> 实现定时与批量调用。</p></td>
<td><p>- <a href="https://one-dragon.com/tools/zh/script_chainer.html">千机链文档</a></p></td>
</tr>
<tr class="odd">
<td><p>6</p></td>
<td><p>能否可靠判断任务执行完成</p></td>
<td><p>必须在 Windows 实机验证</p></td>
<td><p>需结合进程退出与游戏关闭状态判定。使用 -o -c 时，运行结束会自动退出游戏（ZenlessZoneZero.exe）并关闭启动器进程。在千机链中，其判定逻辑为“监听目标进程曾出现并随后关闭”。具体退出码是否严格区分错误需在 Windows 实机验证。</p></td>
<td><p>- <a href="https://one-dragon.com/tools/zh/script_chainer.html">千机链 2.4 运行逻辑</a></p>
<p>- <a href="https://one-dragon.com/zzz/zh/quickstart.html">快速开始-高级启动参数</a></p></td>
</tr>
<tr class="even">
<td><p>7</p></td>
<td><p>能否获得明确的失败原因</p></td>
<td><p>已从源码确认</p></td>
<td><p>通过 .log/ 日志捕获。日志输出模块级日志行，包含 [one_dragon_app.py ...] 与异常堆栈；此外近期 PR #2886 分离应用入口并默认创建运行记录（app_run_record）。</p></td>
<td><p>- <a href="https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon/blob/main/.gitignore">.gitignore</a></p>
<p>- <a href="https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon/issues/2545">Issue #2545 日志样例</a></p></td>
</tr>
<tr class="odd">
<td><p>8</p></td>
<td><p>是否支持安全停止</p></td>
<td><p>必须在 Windows 实机验证</p></td>
<td><p>缺乏专用命令行停止指令；外部程序主要依靠检测超时并调用 Windows API 清理启动器与游戏进程树。</p></td>
<td><p>- <a href="https://one-dragon.com/tools/zh/script_chainer.html">千机链 2.2 运行超时与进程清理</a></p></td>
</tr>
<tr class="even">
<td><p>9</p></td>
<td><p>是否支持外部程序查询执行状态</p></td>
<td><p>已从源码确认</p></td>
<td><p>稳定运行主要依赖增量读取 .log/ 目录；若启动了 MCP Server（:23001），外部可查询基础感知状态。</p></td>
<td><p>- <a href="https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon/blob/main/docs/develop/setup/quickstart.md">docs/develop/setup/quickstart.md</a></p></td>
</tr>
<tr class="odd">
<td><p>10</p></td>
<td><p>是否需要管理员权限</p></td>
<td><p>已从源码确认</p></td>
<td><p>强制要求管理员权限。启动器启动时弹出 UAC 提升窗口，因为需向 ZenlessZoneZero.exe 发送模拟输入或加载虚拟手柄驱动。</p></td>
<td><p>- <a href="https://one-dragon.com/zzz/zh/quickstart.html">快速开始 第 3 节</a></p>
<p>- <a href="https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon/blob/main/pyproject.toml">pyproject.toml dev 依赖包含 pyuac</a></p></td>
</tr>
<tr class="even">
<td><p>11</p></td>
<td><p>是否支持多个实例</p></td>
<td><p>已从源码确认</p></td>
<td><p>原生支持多账号实例配置。配置目录下通过 config/01/、config/02/ 隔离多账号，命令行通过 -i 1,2 连续调度。同一时刻在单台桌面仍为单进程运行。</p></td>
<td><p>- <a href="https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon/blob/main/.gitignore">.gitignore 包含 config/[0-9]*/</a></p>
<p>- <a href="https://one-dragon.com/zzz/zh/quickstart.html">快速开始-高级启动参数</a></p></td>
</tr>
<tr class="odd">
<td><p>12</p></td>
<td><p>是否有独立的配置管理机制</p></td>
<td><p>已从源码确认</p></td>
<td><p>完善的 YAML 配置目录体系。主配置位于 config/ 目录（env.yml, game.yml, one_dragon.yml, zzz_one_dragon.yml）。</p></td>
<td><p>- <a href="https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon/blob/main/.gitignore">.gitignore</a></p>
<p>- <a href="https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon/blob/main/pyproject.toml">pyproject.toml 包含 pyyaml==6.0.3</a></p></td>
</tr>
</tbody>
</table>

## 2. 深入任务与配置系统：功能与调用对照

|                     |                                     |                                   |                      |                                        |                                               |
|---------------------|-------------------------------------|-----------------------------------|----------------------|----------------------------------------|-----------------------------------------------|
| 自动化功能          | 官方功能名称                        | 配置存储路径                      | 外部启动与调用方式   | VIvian 单独调用能力                    | 限制与前置要求                                |
| 每日签到            | 每日签到 (吼吼饼铺/卦象集录/刮刮卡) | config/\[实例\]/ (YAML)           | 包含在一齐龙流水线中 | 支持（通过预先配置该实例的子任务开关） | 需对应商铺处于可交互状态                      |
| 录像店营业          | 录像店营业                          | config/\[实例\]/ (YAML)           | 包含在一齐龙流水线中 | 支持                                   | 每天自动传送到录像店柜台办理营业              |
| 咖啡店              | 咖啡店                              | config/\[实例\]/ (YAML)           | 包含在一齐龙流水线中 | 支持                                   | 每天自动前往咖啡店喝咖啡恢复电量              |
| 电量消耗 / 体力刷本 | 体力刷本 (训练模块)                 | config/\[实例\]/ (YAML，体力计划) | 包含在一齐龙流水线中 | 支持                                   | 自动打开快捷手册-训练，按预设体力计划刷材料   |
| 活跃度奖励          | 活跃度奖励                          | config/\[实例\]/ (YAML)           | 包含在一齐龙流水线中 | 支持                                   | 建议排在一条龙后半段，自动领取每日 400 活跃度 |
| 零号空洞 / 周期挑战 | 迷失之地 / 枯萎之都                 | config/lost_void_challenge/       | 包含在一齐龙周常模块 | 支持                                   | 基于 YOLO/大模型寻路，对机器算力要求较高      |
| 恶名狩猎            | 恶名狩猎                            | config/\[实例\]/ (YAML)           | 包含在一齐龙周常模块 | 支持                                   | 每周完成 3 次周期挑战领奖                     |
| 丽都城募 / 丽都周纪 | 丽都城募 / 丽都周纪                 | config/\[实例\]/ (YAML)           | 包含在一齐龙流水线中 | 支持                                   | 自动打开大月卡界面领取积分与道具              |
| 随便观 / 锄大地     | 随便观 / 锄大地                     | config/world_patrol_route_list/   | 包含在一齐龙流水线中 | 支持                                   | 传送至指定街区清理野怪获取基础材料            |

关键架构结论：ZZZ-OneDragon 的外部调用模式与 BetterGI 类似，不支持在 CLI 传参指定具体子任务，但支持在 CLI 指定账号实例（-i）。子任务的增删与排序依赖配置文件。VIvian 需在拉起启动器前读取或写入对应的实例 YAML 配置。

## 3. 关键路径约束与进程拓扑风险

1.  极其严苛的安装路径硬约束【已从源码与文档验证】：

- 官方快速开始文档与 Issue 明确强调：安装路径必须为全英文、无空格、无中文字符、长度不过长的固态硬盘目录（如 D:\ZZZ-OD）。
- 若路径包含中文或空格，会导致内嵌 Python 虚拟环境加载失败、PySide6 动态链接崩溃或 OpenCV 无法读取图片。
- 架构要求：VIvian 在引导下载或绑定本地目录时，必须执行正则表达式强校验，包含中文或空格直接拦截并提示用户。

2.  启动器进程与实际工作进程的拓扑差异【已从文档验证】：

- 若用户使用的是“原始启动器（OneDragon-Launcher.exe）”，启动器会自动创建子进程 python.exe 运行核心脚本。这会导致千机链或 VIvian 如果仅监控启动器，可能会误判完成，且 python.exe 容易与系统其他 Python 工具混淆。
- 若用户使用的是“集成启动器（OneDragon-RuntimeLauncher.exe）”，执行进程即为其自身，生命周期单一明确。
- 架构对策：VIvian V1.0 强制绑定集成启动器版本（WithRuntime-Full），彻底避免跨项目 python.exe 进程名混淆问题。

3.  启动器内置自动更新截断【已从文档验证】：

- 启动器默认在启动时检查并自动拉取 GitHub/CNB 代码更新。为确保 VIvian 控制下的运行环境稳定，VIvian 必须在 config/env.yml 中显式设置 auto_update: false，由 VIvian 统一管理更新时机。
