# Vivian V1.0 开源许可证合规与游戏账号风险审计 (LICENSE_AND_RISK.md)

**文档路径**：`docs/research/LICENSE_AND_RISK.md`  
**合规原则**：严格遵守 GPL-3.0 开源许可证条款与衍生品分发规范；实事求是披露自动化脚本的账号封禁风险，**绝不承诺“零封号风险”**，**绝不设计或引入任何绕过游戏反作弊系统（Anti-Cheat）、修改游戏内存或注入游戏进程的方案**。

---

## 1. 三款上游项目及核心依赖许可证核查表

| 项目 / 依赖组件 | 所属范围 | 许可证类型 | 版权与来源验证链接 | 传染性 / 关键合规义务 |
| :--- | :--- | :--- | :--- | :--- |
| **BetterGI (更好的原神)** | 上游主项目 | **GNU GPL v3.0** | [LICENSE](https://github.com/babalae/better-genshin-impact/blob/main/LICENSE) / [README](https://github.com/babalae/better-genshin-impact/blob/main/README.md) | 强 Copyleft：修改或打包分发需开源并提供完整源码，保留原作者版权与免责声明。 |
| **March7thAssistant (三月七小助手)** | 上游主项目 | **GNU GPL v3.0** | [LICENSE](https://github.com/moesnow/March7thAssistant/blob/main/LICENSE) | 强 Copyleft：同上；严禁任何商业售卖或付费代练分发。 |
| **ZenlessZoneZero-OneDragon (绝区零一条龙)** | 上游主项目 | **GNU GPL v3.0** | [GitHub 仓库主页](https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon) / [SignPath](https://signpath.org/projects/zenlesszonezero-onedragon/) | 强 Copyleft：GPL-3.0 only；项目 LOGO 版权归巡夜子及全体开发者所有，严禁商业售卖。 |
| **Auto_Simulated_Universe / Fhoe-Rail** | March7th 绑定的 `3rdparty` 子项目 | **GPL-3.0 / 开源** | [March7th README](https://github.com/moesnow/March7thAssistant/blob/main/README.md) | 随 March7thAssistant 完整包分发，须保留各自仓库署名。 |
| **PyQt-Fluent-Widgets / PySide6-Fluent-Widgets** | March7th & ZZZ-OneDragon UI 依赖 | **GPL-3.0** (开源免费版) | [March7th README](https://github.com/moesnow/March7thAssistant/blob/main/README.md) / [ZZZ pyproject.toml](https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon/blob/main/pyproject.toml) | 强 Copyleft：使用开源版 PySide6/PyQt-Fluent-Widgets 的项目必须以 GPLv3 开源。 |
| **PySide6 (Qt for Python)** | March7th & ZZZ-OneDragon GUI 运行时 | **LGPL v3.0 / GPL v3.0** | [ZZZ pyproject.toml](https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon/blob/main/pyproject.toml) | 动态链接遵循 LGPLv3。 |
| **WPF-UI (`Lepoco.WpfUi` 4.0.2)** | BetterGI 及 Vivian 推荐 UI 库 | **MIT License** | [BetterGI AGENTS.md](https://github.com/babalae/better-genshin-impact/blob/refactor/20260930/AGENTS.md) | 宽松许可：仅需保留版权与 MIT 许可声明。 |
| **OpenCvSharp4 / RapidOCR / ONNXRuntime** | 视觉与推理底层依赖 | **Apache-2.0 / MIT** | [BetterGI csproj](https://www.google.com/search?q=https://gitback.org/babalae/better-genshin-impact/src/commit/8aef28598b532a87e833a487538eb8443bfd361/BetterGenshinImpact/BetterGenshinImpact.csproj) / [March7th README](https://github.com/moesnow/March7thAssistant/blob/main/README.md) | 宽松许可：保留版权声明与 NOTICE 文件。 |

---

## 2. 三种分发与调用模式的法律与许可证差异评估

针对 Vivian V1.0 如何获取、调用与分发三款上游项目，对以下三种模式进行合规性对比分析：

| 评估维度 | 模式 A：独立下载 + 独立进程调用（推荐方案） | 模式 B：整合打包二进制再分发（All-in-One 捆绑包） | 模式 C：源码级引用或修改上游 DLL/Python 代码 |
| :--- | :--- | :--- | :--- |
| **具体实现方式** | Vivian 安装包**仅包含 Vivian 自身二进制代码**（约 15~30 MB）。用户首次使用时，由 Vivian 引导从各上游官方 GitHub Releases / CNB / Mirror酱独立下载官方原版压缩包至本地独立目录，运行时通过 OS 命令行（`Process.Start`）和标准配置文件进行进程间松耦合调用。 | 将 BetterGI、March7thAssistant、ZZZ-OneDragon 的编译后二进制文件直接打包进 Vivian 的单一安装包（总包体 > 1.2 GB）中一并分发给用户。 | 直接将 BetterGI 的 C# 源码/DLL 编译进 Vivian，或直接修改 March7th / OneDragon 的 Python 源码并重新打包分发。 |
| **GPL-3.0 传染性判定** | 根据 FSF [GPL FAQ 中关于 Aggregate 与 Fork/Exec 的解释](https://www.google.com/search?q=https://www.gnu.org/licenses/gpl-faq.html%23MereAggregation)，通过命令行参数（CLI）启动独立可执行文件、读写标准公开配置文件及解析日志文件属于**独立程序间的正常进程通信**，不构成 GPL 意义上的衍生作品（Derivative Work）。但**强烈建议 Vivian 自身也采用 GPL-3.0 开源**，以实现与社区生态的完全兼容与透明。 | 属于对 GPL-3.0 受保护二进制作品的直接再分发（Conveying Non-Source Forms，GPLv3 第 6 条）。分发者**必须同时提供或承诺提供对应上游版本及全部 GPL 依赖的完整源代码（Corresponding Source）**，完整保留所有上游 `LICENSE` 文件，且不得附加任何限制用户权利的条款。 | 明确构成 GPL-3.0 **修改与衍生作品（Modified Version / Derivative Work，GPLv3 第 5 条）**。整个 Vivian 项目必须以 GPL-3.0 开源，且必须在所有修改过的文件中显著标注修改日期与变更说明。 |
| **上游社区关系与反倒卖合规** | **最友好**：所有下载流量与 Release 统计归属上游官方仓库（或官方 Mirror酱分成渠道），尊重上游作者的《免责声明》确认流程与品牌署名（如 ZZZ-OneDragon 明确声明 LOGO 版权归原作者所有）。 | **存在社区摩擦风险**：三款上游项目均饱受闲鱼/淘宝非法商业倒卖困扰，对未经授权的第三方整合包高度警惕；且每当上游日更时，捆绑包会导致版本滞后。 | **维护成本与合规风险最高**：一旦修改上游代码，上游将拒绝提供 Issue 支持，且极易在游戏更新时产生合并冲突。 |

### 合规落地设计要求（Vivian V1.0 必做项）
1. **Vivian V1.0 自身采用 `GNU GPL v3.0` 开源发布**，彻底消除任何开源协议兼容性争议，并在仓库根目录与软件「关于」页明确声明**完全免费、严禁任何形式的商业售卖、付费代练或捆绑收费**。
2. **坚持采用「模式 A：独立下载 + 独立进程调用」**：
   - Vivian 发行包内**不捆绑**三款上游引擎的二进制文件，也不内嵌或篡改上游项目的受版权保护 LOGO。
   - 在用户通过 Vivian 下载任意引擎时，直连上游官方 GitHub Release / CNB / Mirror酱接口，并在解压后完整保留其 `LICENSE`、`README.md` 及版权文件。
   - 严格尊重上游首次运行机制：例如 `March7thAssistant` 要求用户本人在 GUI 中阅读并确认免责声明（见 [官网说明](https://m7a.top/)），Vivian 检测到未确认时将直接拉起原版 GUI 由用户本人确认，**绝不在后台通过修改配置文件伪造 `first_run_completed: true` 绕过上游免责声明**。

---

## 3. 游戏账号风险评估与技术安全红线

### 3.1 上游项目技术原理与官方风险声明核查
经对三款上游项目的源码审计，三者在本地运行模式下均遵循 **“纯外部计算机视觉（CV）+ 操作系统级输入模拟”** 路线：
1. **BetterGI**：通过 `BitBlt` 或 `Windows Graphics Capture (WGC)` 捕获窗口画面，使用 `OpenCvSharp` / `ONNXRuntime` / `PaddleOCR` 识图，通过 Win32 `User32.SendInput` 与 `PostMessage` 发送键鼠消息（见 [`WindowsInputMessageDispatcher.cs`](https://gitback.org/babalae/better-genshin-impact/blame/commit/192f1744fcc1ec822b29b3d1fd1eae84d130b5bc/Fischless.WindowsInput/WindowsInputMessageDispatcher.cs)）。官方 FAQ 明确指出（见 [BetterGI README](https://github.com/babalae/better-genshin-impact/blob/main/README.md)）：
   > “BetterGI 不会做出任何修改游戏文件、读写游戏内存等任何危害游戏本体的行为，单纯依靠视觉算法和模拟操作实现。但是 mhy 是自由的，用户条款上明确说明第三方软件/模拟操作是封号理由之一。当前方案还是存在被检测的可能。”
2. **March7thAssistant**：通过窗口截图、`RapidOCR` 与 `PyAutoGUI` / Win32 消息完成自动化。官方首页明确声明（见 [m7a.top](https://m7a.top/)）：
   > “使用自动化工具有风险吗？有。《崩坏：星穹铁道》的公平游戏宣言禁止使用脚本等第三方工具，违规可能导致扣除收益乃至封禁账号。小助手主要通过识别游戏画面、模拟键鼠操作来完成任务，但这不代表没有风险。”
   * **特别高危项预警**：March7thAssistant 的 `3rdparty` 目录中包含可选工具 `Genshin_StarRail_fps_unlocker`（默认 `genshin_starRail_fps_unlocker_allow: false`，见 [`config.example.yaml`](https://github.com/moesnow/March7thAssistant/blob/main/assets/config/config.example.yaml)）。由于解锁帧率工具通常涉及进程内存写入，风险显著高于纯视觉日常任务，**Vivian V1.0 必须在配置补丁中强制保持 `genshin_starRail_fps_unlocker_allow: false`，且不在 UI 中暴露任何帧率解锁入口**。
3. **ZenlessZoneZero-OneDragon**：通过系统截图、OCR、YOLO 模型与 `pyautogui` / `pynput` / `vgamepad`（`ViGEmBus` 虚拟手柄驱动）执行操作。官方 README 明确声明（见 [ZZZ-OneDragon README](https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon)）：
   > “本项目仅供学习交流……使用本工具产生的一切风险需自行承担。”

### 3.2 Vivian V1.0 架构与安全绝对红线（Strictly Prohibited）
为保护用户账号安全与项目法律合规，Vivian V1.0 在架构设计与后续开发中必须恪守以下 **5 条绝对红线**：
1. **严禁承诺“零封号风险”或“100% 安全防封”**：在 README、安装包向导、首次启动弹窗及「设置与风险提示」页面中，必须以醒目文字客观告知用户：任何第三方自动化脚本均违反游戏《用户协议》与《公平游戏宣言》，存在被检测并导致警告、扣除收益、短期或永久封禁账号的风险。
2. **严禁触碰反作弊系统（Anti-Cheat）与游戏进程空间**：
   - 严禁读取、写入或扫描游戏进程虚拟内存（`ReadProcessMemory` / `WriteProcessMemory`）。
   - 严禁向游戏进程注入任何 DLL、Hook DirectX/Unity 渲染管线或拦截网络封包。
   - 严禁试图挂起、篡改、降权、伪造或绕过游戏反作弊内核驱动与服务（如 `HoYoKProtect.sys`、`mhyprot2.sys`、`ACE` 等）。
3. **严禁伪造隐藏进程特征**：Vivian 作为正大光明的开源桌面任务编排器，不使用任何恶意软件常见的进程镂空（Process Hollowing）、随机伪装进程名或内核驱动隐藏手段；仅使用标准的 Windows `Process.Start` 与 `JobObject` 管理子进程生命周期。
4. **严禁启用高风险第三方内存修改插件**：在适配器层硬编码拦截并禁用任何涉及内存修改的附属功能（如帧率解锁器 `fps_unlocker`）。
5. **严禁明文存储或外传用户敏感凭证**：若用户在配置中使用了 Mirror酱 CDK、Webhook Token 或通知密钥，Vivian 在本地备份与诊断包导出时必须自动执行正则脱敏（替换为 `***REDACTED***`），且 Vivian 自身绝不内置任何联网遥测（Telemetry）或数据收集服务器。
