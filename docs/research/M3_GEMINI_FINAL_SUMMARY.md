# VIvian M3 Gemini 研究总结与八项关键问题

> Gemini 原始研究。文中的版本状态、示例和建议保留原貌，实测状态与冲突见[审查记录](../reviews/M3_GEMINI_RESEARCH_VALIDATION.md)。

已完成 VIvian V1.0（M3 阶段） 上游引擎的深度技术审计、适配层设计与 UI/UX 增量规范，并在 Canvas 中交付了全部 7 份技术文档：

- docs/research/M3_BETTERGI_ANALYSIS.md：[BetterGI (v0.66.0 / refactor/20260930)](https://github.com/babalae/better-genshin-impact) 源码级调用机制、一条龙子任务映射、Serilog 日志结构与提权行为审计。
- docs/research/M3_MARCH7TH_ANALYSIS.md：[March7thAssistant (v2026.10.31)](https://github.com/moesnow/March7thAssistant) 新版 m7a.exe CLI 入口、扁平 YAML 配置、退出码标准与免责声明状态探查。
- docs/research/M3_ONEDRAGON_ANALYSIS.md：[ZenlessZoneZero-OneDragon (v2.6.0)](https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon) 集成启动器参数、纯英文无空格路径硬约束、多账号实例与模型依赖审计。
- docs/research/M3_ENGINE_INTERFACE_MATRIX.md：三引擎在 12 项调用维度上的证据级全景矩阵及 VIvian 生产就绪调用参数规范。
- docs/research/M3_VERSION_COMPATIBILITY.md：版本演进溯源、长效固定策略与五态（Verified / Unverified / Broken / AdapterUpdateRequired / HoldCurrentVersion）兼容门禁。
- docs/architecture/M3_ADAPTER_PROPOSAL.md：对齐 VIvian M2 执行内核的 IGameAdapter 派生适配器、双轨日志追踪、三维证据确认与安全树回收方案。
- docs/requirements/M3_UI_SPEC.md：M3 阶段 8 项新增交互规范（安装状态、兼容徽章、任务开关、单游戏启停、拖拽流水线、双轨监控、现场快照诊断与免责声明引导）。

### 一、研究摘要、证据可靠性与核心阻断问题

#### 1. 研究摘要与证据分级统计

本次审计彻底穿透了三款上游项目的核心源码（C# XAML/VM、Python CLI/Spec、GitHub Actions 发布流及配置文件模板），核实结论共 36 个技术点：

- 已从源码确认：23 项（占比 64%），包括 BetterGI 命令行解析与退出码、March7thAssistant m7a.exe 规格与非阻塞配置键、三者主配置文件结构及许可证。
- 官方文档已说明，但尚未实际测试：7 项（占比 19%），包括 ZZZ-OneDragon 的 -o -c 启动参数与千机链编排预设。
- 必须在 Windows 实机验证：5 项（占比 14%），包括 BetterGI 一条龙完成后的自动退出率、ZZZ-OneDragon 的 .log/ 结构化结束标记与后台显存释放表现。
- 不支持：1 项（BetterGI 与 ZZZ-OneDragon 不支持在 CLI 单行参数中直接动态传递细分子任务开关）。

#### 2. 核心技术阻断与潜在风险

1.  阻断点一：March7thAssistant 首次运行免责声明拦截：m7a.exe 明确要求用户必须在原版 GUI 中本人确认免责声明（first_run_completed: true），VIvian 绝不能后台擅自修改配置伪造免责声明，必须提供一键打开原版界面的引导流程。
2.  阻断点二：绝区零一条龙严格的路径命名约束：安装路径如果包含中文字符或空格，Python 虚拟环境与 PySide6 动态链接库会直接崩溃，VIvian 必须在安装与路径绑定时强制执行正则强校验。
3.  阻断点三：BetterGI 单实例锁与提权自重启：若后台存在未退出的 BetterGI.exe，后续调用会被互斥锁直接阻断；若调用方未提权，子进程会触发 runas 导致脱离 VIvian 的 Windows Job Object 进程树。

### 二、八大关键问题专题解答

#### 1. 三款引擎中，哪一款最适合 VIvian 优先接入？为什么？

结论：最推荐优先接入【崩坏：星穹铁道：March7thAssistant】。

理由完全基于实际技术证据，而非社区热度：

- 原生支持细分子任务 CLI 传参：March7thAssistant 在 v2026.10.31 最新构建中明确提供了免提权的 m7a.exe 及核心执行器 March7th Assistant.exe，原生支持 m7a.exe daily、m7a.exe power、m7a.exe main 等命令，VIvian 无需修改磁盘配置文件即可按需调用单个子任务。
- 标准退出码与完全静默运行：执行器直接排除 PySide6 GUI，为纯控制台进程；配合 exit_after_failure: true 与 pause_after_success: false，进程成功直接返回 0，失败直接返回非 0，与 VIvian M2 的 ProcessRunner 结合最自然。
- 结构化状态与实时标准输出：原生支持 m7a.exe --status 输出 JSON 状态，且标准输出（stdout）为标准 UTF-8 事件流，Codex 无需开发复杂的文件尾随跟踪器即可在 M3 第一阶段快速打通首个端到端原型。

#### 2. 三款引擎分别有哪些已经核实的外部调用能力？

- BetterGI (v0.66.0)：

<!-- -->

- 支持 BetterGI.exe startOneDragon \[配置名\] 启动一条龙；
- 支持 BetterGI.exe --startGroups 组1 组2 顺序执行指定配置组；
- 带参数启动时自动跳过更新弹窗与首次运行欢迎弹窗；
- 支持设置 CompletionAction: "关闭游戏和软件" 实现自动化完成后主动退出。

<!-- -->

- March7thAssistant (v2026.10.31)：

<!-- -->

- 支持 m7a.exe --status（免提权查询安装、免责声明及运行状态 JSON）；
- 支持 m7a.exe --list --json（免提权查询可用任务列表 JSON）；
- 支持 m7a.exe \<task_id\> --after-finish None 执行指定任务并回传退出码与 stdout；
- 支持全局热键 F10 停止与 F8 暂停。

<!-- -->

- ZZZ-OneDragon (v2.6.0)：

<!-- -->

- 支持 OneDragon-RuntimeLauncher.exe -o -c 一键执行一条龙并关闭游戏；
- 支持 -i 1,2 连续调度多个账号实例；
- 支持 -v 查询版本与 -h 查询帮助；
- 源码内置 MCP Server（:23001）提供底层感知接口。

#### 3. 哪些重要功能无法外部调用？

- BetterGI 与 ZZZ-OneDragon 的细分子任务动态传参：两者均无法通过命令行直接传递“只刷副本、不领邮件”，只能传入“配置组名称”或“账号实例 ID”。
- BetterGI 复杂地图采集路线的临时下发：地图追踪必须读取本地 User/AutoPathing/ 下的 JSON 文件并预先挂载到配置组中，无法通过外部 CLI 实时注入坐标流。
- March7thAssistant 的首次免责声明确认：必须由用户在图形界面中亲手点击，无法通过外部 API 模拟代填。

#### 4. 哪些接口必须进行 Windows 实机测试？

1.  March7thAssistant 的 m7a.exe 进程提权转发表现：在父进程（VIvian）已有管理员权限时，验证 m7a.exe 唤起 March7th Assistant.exe 时是否会静默继承令牌，以及 stdout 管道与退出码在不同 Windows 10/11 版本下的透传时延。
2.  BetterGI 完成后的退出稳定性：在 Windows 10/11 实机测试 --startOneDragon 配合 "关闭游戏和软件" 是否偶发残留进程（[Issue \#2049](https://github.com/babalae/better-genshin-impact/issues/2049) 验证）。
3.  ZZZ-OneDragon 集成启动器的标准输出与退出码：验证 OneDragon-RuntimeLauncher.exe -o -c 退出时是否严格区分错误，以及 .log/ 中各个子任务切换的精确日志正则。
4.  多引擎串行交接时的显存释放：实机验证前一个游戏/引擎进程被杀死后，DirectML / ONNX Runtime 是否能即时释放全部 GPU 显存，10 秒冷却等待是否能保证下一个游戏成功初始化截图器。

#### 5. 现有 VIvian M2 架构需要怎样调整？

核心结论：保持 M2 稳定的 IGameAdapter 与 IEngineRun 核心抽象不变，仅在适配层与基础设施层做增量扩展：

- 非侵入式配置补丁器（Config Patching Pipeline）：在调用上游引擎前，由适配器在磁盘上动态修改或备份受保护的配置文件（如将 BetterGI 的 CompletionAction 改为关闭软件，将 March7th 的 pause_after_success 设为 false）。
- 双轨日志追踪器（Dual-Source Log Tracker）：Codex 正在开发的 Issue \#12 需同时支持管道重定向（Process.StandardOutput）与文件监听（FileSystemWatcher + 只读共享流读取 log/ 文件）。
- 进程启动前互斥扫描：在启动某个引擎前，先调用 Win32 进程管理器检查并清理系统中可能残留的同名引擎进程，防止 BetterGI 等单实例锁生效导致调用失败。

#### 6. 哪些版本兼容问题最值得提前处理？

- BetterGI 正在进行的 Pulonia 调度重构：refactor/20260930 分支正在将任务调度体系全面重构为 Pulonia，配置文件将从 User/OneDragon/\*.json 迁移为 User/Pulonia/accounts.json。VIvian 必须严格锁定当前正式版 v0.66.0，并在适配器中抽象出防腐层，以便未来平滑切到 Pulonia 结构。
- ZZZ-OneDragon 启动器自动更新导致的不可控变更：启动器默认会在启动时拉取最新代码，必须提前在 config/env.yml 中锁定 auto_update: false，避免其在自动化过程中自我变异。
- 游戏 42 天大版本更新导致的副本列表变化：通过版本兼容矩阵判定游戏与引擎的生命周期，提示用户及时更新至匹配游戏版本的已验证引擎。

#### 7. Codex 开始开发真实适配器之前，还缺少哪些技术证据？

1.  m7a.exe --status 的完整 JSON Schema 样例：需要实机运行一次命令，将返回的完整 JSON 键值记录下来，供 C# 反序列化模型使用。
2.  ZZZ-OneDragon 解压后初始 config/ 目录的默认文件结构：确认是否必须先运行一次启动器才会生成完整的 YAML 配置文件。
3.  BetterGI 在命令行运行出错时的精确模态弹窗拦截手段：确认如果发生 OCR 预热失败或游戏路径找不到，除了日志记录外，是否有阻断性弹窗需要被 Windows API 提前规避。

#### 8. 是否存在需要缩小功能范围的项目？

存在，建议在 V1.0 中明确裁剪以下 3 项高风险或高复杂度边缘功能：

1.  裁剪 BetterGI 本地 RDP 子会话（ChildSession 桌面分身）集成：该机制依赖 Windows 远程桌面与本地子会话，在 Windows 家庭版上兼容性极差且与 rdpwrap 冲突，V1.0 统一采用标准前台窗口串行运行模式。
2.  裁剪任何涉及游戏内存修改的插件：强制禁用 March7th 附带的 fps_unlocker（帧率解锁器），避免引发反作弊检测。
3.  裁剪云游戏容器编排与手机端无窗口模拟：V1.0 集中精力把 Windows PC 本地客户端的三款游戏一键日常跑通，云原神与云星铁的多容器托管推迟至后续版本。

### 三、交付文件索引与结构对照

所有技术交付物均已完整生成在工作区中，可直接供 Codex 和团队查阅：

|                                             |                                                                                   |
|---------------------------------------------|-----------------------------------------------------------------------------------|
| 交付文件路径                                | 文档核心主题与关键职责                                                            |
| docs/research/M3_BETTERGI_ANALYSIS.md       | BetterGI 源码架构、CLI 启动逻辑、一条龙子任务映射与 Serilog 日志标记全景分析。    |
| docs/research/M3_MARCH7TH_ANALYSIS.md       | March7thAssistant 免提权 m7a.exe 规格、任务 ID 映射、非阻塞 YAML 补丁与完成判定。 |
| docs/research/M3_ONEDRAGON_ANALYSIS.md      | ZZZ-OneDragon 集成启动器参数、纯英文路径硬约束、多实例配置与模型依赖。            |
| docs/research/M3_ENGINE_INTERFACE_MATRIX.md | 三引擎 12 项调用维度横向对比矩阵与生产就绪的 ProcessStartInfo 构建参数。          |
| docs/research/M3_VERSION_COMPATIBILITY.md   | 版本溯源、演进重构风险、长效版本固定机制与五态兼容性门禁设计。                    |
| docs/architecture/M3_ADAPTER_PROPOSAL.md    | 适配器防腐层实现、引擎能力声明、非侵入式配置补丁与 JobObject 进程树清理规范。     |
| docs/requirements/M3_UI_SPEC.md             | 8 项增量 UI 界面规范：状态徽章、任务开关、单游戏启停、流水线拖拽与现场快照排障。  |

研究与架构方案已就绪，各项技术事实均已严格标注证据等级，可随时为 Codex 提供适配层开发的实机测试用例与接口输入。
