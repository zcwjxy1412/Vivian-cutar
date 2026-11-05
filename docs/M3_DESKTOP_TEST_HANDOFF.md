# M3 桌面测试交接（给 Codex）

本文件是预桌面阶段结束时交给 Codex 的唯一入口：说明要测哪个构建、按什么顺序测、哪些可以自动化、哪些必须由用户本人操作，以及如何报告证据。所有尚未在真实 Windows 10/11 桌面上观察到的内容，在代码中都是 **Pending** 或 **Unsupported**，真实执行被关闭。不要把 Windows Server CI 的结果当作桌面验证结果。

## 0. 被测构建

| 项 | 值 |
|---|---|
| 版本 | `0.4.0-dev`（程序内 `InformationalVersion` = `0.4.0-dev+<提交>`，界面“设置与诊断/关于”显示提交号） |
| 代码分支 | `feature/m3-dev-build`（堆叠：#38 → #39 → #40 → #41）；合并到 `main` 后改用 `main` 上对应提交 |
| 构建提交与 CI | 见本文件末尾“构建记录”；构建产物中的 `BUILD-INFO.txt` 记录了源提交和 CI 运行地址 |
| 下载位置 | Actions → **Windows validation** → 该提交的 **push** 运行 → Artifacts → `VIvian-dev-win-x64`（含 `VIvian-win-x64.zip`、`.sha256`、`BUILD-INFO.txt`） |
| 支持系统 | Windows 10 22H2 / Windows 11（x64），标准用户账户，默认 DPI 100%–200% |
| 安装 | 解压到新的空目录，运行 `VIvian.exe`；自包含，不需要 .NET；**不要以管理员身份运行** |
| 数据位置 | `%LOCALAPPDATA%\VIvian`（`vivian.db`、`logs\`、`exports\`）；删除该目录即重置 |

核对：`Get-FileHash VIvian-win-x64.zip -Algorithm SHA256` 必须等于 `.sha256` 文件；`(Get-Item VIvian.exe).VersionInfo.ProductVersion` 必须以 `0.4.0-dev+` 开头且提交号与 `BUILD-INFO.txt` 一致。

## 1. 需要的官方引擎发行包

| 引擎 | 固定版本 | 官方资产 | SHA-256 | 摘要来源 |
|---|---|---|---|---|
| March7thAssistant | v2026.10.31（`1e77d59`） | `March7thAssistant_full.zip`（719,711,875 字节） | `e03aaad4ea27c70f30bf5241ab3bfac8894de7675ddd30a9aef7f1e551af9985` | 官方 Release 元数据（[资产审计](evidence/M3_MARCH7TH_RELEASE_ASSET_AUDIT.md)） |
| March7thAssistant | 同上 | `March7thAssistant_full.7z`（474,333,248 字节） | `1fcaa1934e3d30d598ce7863756c49b9e9fb4f4fbb759066a62f52541a39dee0` | 同上 |
| BetterGI | v0.66.0 | `BetterGI_v0.66.0.7z` | `28d4756e55f32637538bf84427f5f31afd5b153bf996530c5975bd9c8c0b9343` | **仅研究报告**，需对照官方 Release 页面重新确认 |
| OneDragon | v2.6.0 | `ZenlessZoneZero-OneDragon-v2.6.0-WithRuntime-Full.zip` | `2fd5f6bfc6c01fcf6f21092dc6e18fa18e146d2614ca6ad130d5fc3057ba0ced` | **仅研究报告**，需重新确认 |

只从各项目官方 GitHub Release 下载。对每个文件记录：下载 URL、字节数、独立计算的 SHA-256、与官方公布值是否一致。BetterGI 与 OneDragon 的摘要确认后，更新 `src/VIvian.Core/Engines/EngineCatalog.cs` 中对应资产的 `DigestStatus`（并补充 `Length`）。

## 2. 分阶段验证

“自动”= Codex 可以在用户的桌面会话中用脚本完成，无需用户操作；“人工”= 必须由用户本人点击、批准或确认。每一阶段通过后才进入下一阶段；任何清理无法确认的结果都要停下来。

### A. 普通桌面安装与启动（自动，用户只需提供桌面）
1. 下载、核对摘要、解压、启动；确认无 UAC 提示、标题区显示“预桌面开发版”。
2. 运行 `VIvian.exe --smoke-test`（需设置 `VIVIAN_SMOKE_DATA` 为新的空目录，见 `scripts/Smoke.ps1`），得到 `smoke.ok` 与每个标签页截图；在 100%、150%、200% 缩放各做一次。
3. 人工：浏览各标签页，确认文字未截断、窗口可缩放到 640×480。
4. 记录：Windows 版本号（`winver`）、缩放比例、`smoke.ok` 全文、截图。

### B. 无害进程与 Job Object 验证（自动）
在桌面会话中运行 `dotnet test tests/VIvian.Tests -c Release`（或使用 CI 的测试产物），重点看 `SupervisorTests`、`FakeEngineScenarioTests`、`CliDiagnosticRunnerTests`、`FileIdentityTests`。这些只启动 FakeEngine。对比 Windows Server CI 结果，记录任何差异（尤其是 Job 中的进程数、`any-job-member`、杀毒软件导致的文件占用）。

### C. UAC 与跨权限验证（人工，需要用户明确授权每一步）
按 [M3_PRIVILEGE_BOUNDARY_VALIDATION.md](evidence/M3_PRIVILEGE_BOUNDARY_VALIDATION.md) 的“桌面待授权验证”执行：用户本人拒绝一次 UAC、批准一次 UAC；记录提权进程的 SID、完整性级别、是否在 VIvian 的 Job 中（`OwnsProcess`）、退出与清理状态。**无害探针程序由 Codex 在本阶段编写**（预桌面阶段没有开发 WindowsBoundaryProbe）。不得静默提权，不得绕过 UAC。

### D. 官方发行包与隔离 CLI 验证（核对自动；运行 CLI 需用户授权）
1. 自动：在 VIvian“引擎管理”中核对 `March7thAssistant_full.zip` → 期望“已验证”，并把 m7a.exe 的 SHA-256 记入证据；安装目录状态应变为“文件已核对 · 待桌面验证”。
2. 自动：解压官方包到全新目录，记录完整文件清单与 `m7a.exe` 是否存在（errata §1）。
3. 授权后：用 `IsolatedCopyBuilder` 复制到 VIvian 工作区，再用 `CliDiagnosticRunner` 依次运行 `--version`、`--list --json`、`--status`。这需要一个**只为该次实测**放行、绑定 m7a.exe 摘要与 `CliAuthorization` 的闸门实现（应用内的 `BuildCliExecutionGate` 始终拒绝，不要修改它的默认行为）。每次运行导出 `CliEvidenceExporter` JSON（`synthetic: false`）。
4. 报告真实 CLI 输出时：保存脱敏后的完整原始输出、退出码、耗时、`fileChanges`（新增/删除/修改）、清理状态；说明 `--status` 是否生成了 `config.yaml` 或其他文件。根据真实输出决定：版本/状态解析器是否保持、`--list --json` 结构是否足以提取任务标识。只有在此之后才能把对应能力从 Pending 改为 Verified（证据级别 A）。
5. BetterGI / OneDragon：只做发行包核对、解压清单、`FileVersionInfo` 版本、目录结构与 VIvian 就绪检查结果；不要启动 BetterGI.exe（会改写 `bettergi://` 协议注册），OneDragon 启动器可能联网更新代码，同样不在本阶段运行。

### E. 真实引擎生命周期验证（人工授权，每款引擎单独进行）
仅在 C、D 通过后。为单个引擎实现真实适配器（替换 `PendingEngineAdapter`，必须经 `IProcessSupervisor` 启动，不得按进程名查找或结束进程），并在不登录游戏账号的前提下验证：启动、输出/日志采集、正常结束、紧急停止、清理确认、提权分叉时 `Cleanup=Unconfirmed` 的阻断。调度器目前对所有非模拟适配器直接阻断（`QueueScheduler` 的 `RealAdapterUnverified`），放开前需要独立审查。

### F. 真实游戏自动化验证（需要用户另行单独授权）
涉及真实游戏与账号，本交接**不授权**。获授权后，按每款引擎的完成证据契约（`EngineProfile.CompletionContract`）逐项观察：只有本 RunId 的完成信号 + 退出码 0 + 无失败/致命信号同时成立时才能判定成功（`CompletionEvidenceEvaluator`，契约未验证时一律“结果未知”）。用游戏内状态由用户本人人工复核，并记录不一致。

## 3. 证据采集与报告

- **日志**：VIvian 内“设置与诊断 → 导出诊断包”得到已脱敏 ZIP（不含数据库、上游配置、账号）。提交前打开检查；不要提交未脱敏的 `%LOCALAPPDATA%\VIvian\logs`。
- **复现失败**：记录 Windows 版本、构建提交、操作步骤、时间（UTC）、诊断包、相关截图；尽量用 FakeEngine 场景在 CI 复现并补测试。
- **进程归属**：只依据 VIvian 持有的 Job 句柄判断（`ISupervisedProcessGroup.OwnsProcess`），并记录 PID + 创建时间；进程名、安装路径、窗口标题都不能作为归属依据。
- **清理是否已确认**：`CleanupState.Confirmed` 只说明 VIvian 的 Job 中已没有进程。经 UAC、任务计划程序或启动器脱离 Job 的进程不在其内；对这些进程无法确认时必须记录为 `Unconfirmed`，并确认后续队列被阻断、界面出现确认横幅。
- **真实 CLI 输出**：见 D.4；文件名使用 `docs/evidence/M3_<ENGINE>_CLI_<操作>_<日期>.json`，并在 `docs/evidence/EVIDENCE_INDEX.md` 登记。
- 每份证据注明：自动 / 人工、Windows 版本、构建提交、发行包摘要、是否获得用户授权（谁、何时、范围）。

## 4. 当前状态总览

| 类别 | 内容 |
|---|---|
| 已实现且自动测试（Windows CI） | 配置与任务计划持久化、数据库 v1→v3 迁移、日志保留、诊断包脱敏导出、三引擎只读就绪检查、星铁发行包核对、隔离副本与链接/硬链接防护、文件差异、CLI 运行器（FakeEngine 合成场景：成功、超时、取消、孤儿子进程、非零退出）、完成证据复合判定、模拟调度/紧急停止/崩溃对账、WPF 冒烟（截图与对比度） |
| 已实现、待桌面验证 | 高 DPI 显示、隔离 CLI 诊断的真实运行、跨权限进程归属与清理、桌面会话中的 Job 行为 |
| 上游能力未证实 | 三款引擎的全部启动参数、任务标识、完成信号、`--list --json` 结构、权限需求与进程拓扑（见各 `EngineProfile`） |
| 仍关闭的真实功能 | 真实任务执行、真实引擎启动、上游 CLI 运行、配置修改、游戏启动 |

## 构建记录

（由预桌面阶段最后一次提交填写；之后的纯文档提交不改变二进制内容。）

| 项 | 值 |
|---|---|
| 被测二进制的源提交 | `2799b3a1e892b20aa4f11d476c5d956e1da152f5`（分支 `feature/m3-dev-build`） |
| 程序版本 | `0.4.0-dev+2799b3a1e892b20aa4f11d476c5d956e1da152f5` |
| Windows CI（push） | [run 41933963065](https://github.com/zcwjxy1412/Vivian-cutar/actions/runs/41933963065)：346/346 测试通过，0 跳过；WPF 冒烟通过（模拟队列 3/3、紧急停止 241 ms、UI 最长响应 6 ms、引擎就绪状态、任务计划持久化、诊断包导出、163 个文本元素对比度合格） |
| 产物 | `VIvian-dev-win-x64`（Artifact ID 13373181875）；证据 `VIvian-evidence`（13373321640，含每个标签页截图） |
| `VIvian-win-x64.zip` SHA-256 | `dc6319d4644cf8c1fd2d99e976b4ca60022fda662cb7b3df75683495e2124912` |

程序内会写入构建所用的提交号，所以之后的任何提交（包括只改本文件）都会产生提交号和摘要不同的新包。测试时以上表这个构建为准；如果产物已过期（GitHub 默认保留 90 天），在同一提交上重新运行 Windows validation 后按 `BUILD-INFO.txt` 核对。
