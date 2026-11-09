# VIvian 项目计划与接手手册

基线核对日期为 2026-11-09（America/New_York）。读完本页即可开始接手。具体登录画面和验收记录在对应文档中，历史讨论不作为启动前置条件。

## 目标与当前版本

VIvian（用户侧名称 furina）是 Windows 上的三游戏日常控制中心。它提供一处账号管理、任务设置和运行入口，通过适配器调用现有引擎。近期目标是把星铁日常做到可验证、可停止、可回滚，再按同一契约接入原神 BetterGI 和绝区零 OneDragon。

| 项目 | 接手基线 |
|---|---|
| 源码 | [zcwjxy1412/Vivian-cutar](https://github.com/zcwjxy1412/Vivian-cutar)，本机 `E:\auto agent game\Vivian-source` |
| 功能开发 | `feature/m5-2-auto-account-switch`，[PR #52](https://github.com/zcwjxy1412/Vivian-cutar/pull/52)。本轮开始时的分支头为 `3512ca4d44cb551b61bc9c3666d9e150cd450bbe`，在验收提交上补了文档 |
| 整理与交接 | `chore/m5-2-workspace-cleanup`，[PR #53](https://github.com/zcwjxy1412/Vivian-cutar/pull/53)，基于 #52，收纳本文、旧入口删除和 CI 产物精简。本机整理后检出此分支 |
| 实机与当前安装 | `aff9a76d7fc0884c35ff3656f0433cb3adfcefd4`。桌面或项目根目录的「启动 furina」都指向 `E:\auto agent game\VIvian\VIvian.exe`。以该目录 `BUILD-INFO.txt` 判断安装版本 |
| 本机安装包与回滚 | `项目归档\安装包\VIvian-M5.2-aff9a76-win-x64.zip` 及 `.sha256`；最近可回滚目录 `项目归档\回滚版本\VIvian-32b0102-实机复验前`。回滚包是旧行为，不能当作验收版 |
| 运行依赖 | Windows x64、WPF、.NET SDK `10.0.403`（`global.json`，本机 `E:\auto agent game\.dotnet\dotnet.exe`）；星铁助手固定为 `v2026.10.31` |
| 账号和运行数据 | `%LOCALAPPDATA%\VIvian-M5-DailyTest`，SQLite schema v6。实机验收使用独立目录 `%LOCALAPPDATA%\VIvian-M5-DesktopAcceptance-r2`。历史 DB/日志仅本机保留 |

正式发布看 `main` 和 [Releases](https://github.com/zcwjxy1412/Vivian-cutar/releases)。远端依赖核对为 `main → #47 → #48 → #49 → #50 → #51 → #52 → #53`，#46 是同样以 `main` 为基底的并列集成预览。#46–#53 均为开放草稿。处理堆叠 PR 前应分别确认 base、head、差异及门禁，不把链尾直接合到 `main`。旧提交、PR 和 Issue 里的 Pending 只能说明当时状态。

## 项目进展与后续顺序

| 阶段 | 当前进度 | 下一步与验收条件 |
|---|---|---|
| 0 · Windows 启动与部署 | 固定启动入口可用。当前包的 448 个文件安装时逐一校验，部署前后用户库 SHA-256 一致；相同数据目录的重复启动通过 WPF 冒烟 | 有新版本时仍用固定目录、先验证后替换、留一份可回滚版。以快捷方式目标、安装 `BUILD-INFO.txt`、校验值和正常启动证据收尾 |
| 1 · 星铁单账号回归 | 用户已确认原有日常可用，当前功能提交 Windows M5 测试 733/733；暂停、继续、强制停止、配置恢复的完整真机回归待测 | 用已授权单账号记录每个操作的实际状态、上游进程退出、临时字段恢复和失败原因。确认不了进程归属或配置恢复即阻断后续运行 |
| 2 · 星铁保存账号自动切换 | `aff9a76` 实机 A→B 通过。r7 依序核对卡片和世界就绪，两账号均有本次 500 分证据并写入成功，清理完成后才切换，批次内 0 人工、0 UAC | 优先补三账号和各账号不同任务设置的真机验证。三账号需要实际第三个已授权保存账号；没有则记录缺口。再测遮挡、加载延迟、验证码、拒绝 UAC、宿主退出、强制停止和崩溃恢复。每项记录是否阻断、是否落账、是否误触及是否残留进程 |
| 3 · 原神 BetterGI | 已有适配器生命周期、日志解析、预检和模拟验证；真实执行门未开放 | 核验固定版官方包和安装身份，观察实际启动、UAC/Job 归属、停止及本次任务完成信号。用受控单账号试跑，成功、失败、取消和清理各有可归属的证据后再扩大范围 |
| 4 · 绝区零 OneDragon | 已有适配器、CLI 约定和模拟验证；真实执行门未开放。旧现场记录有路径准入问题 | 重新核对安装路径、固定版本、自动更新状态、非管理员转发及工作进程归属。确认实际的启动、完成、失败和停止信号后，再做受控真机试跑 |
| 5 · 三游戏集成与发布 | 尚未验收 | 三个适配器各自通过后再测混合队列、跨游戏停止与崩溃恢复。发布评审逐项核对构建、真机证据、数据兼容与回滚，并按堆叠 PR 依赖顺序处理 |

星铁 r7 的批次编号是 `820f8a0a-77da-47ea-a708-556fa35e6bc7`。验收档案提前勾选 A、B，启动 UAC 由用户在批次开始前完成，验收程序调用了正式首页同一个 `OneClickCommand`。批次完成 2、失败 0；这是同日重复核验，前一轮 r4 还见到 B 领取新奖励。不同配置隔离和完整异常矩阵仍待真机核验。详情见 [M5.2 验收](M5_2_ACCEPTANCE.md) 与本机 `开发资料\测试证据\M5.2-双账号实机验收-2026-11-09.md`。

Windows CI 的 [功能提交 `aff9a76`](https://github.com/zcwjxy1412/Vivian-cutar/actions/runs/42591871033/attempts/2) 首次打包冒烟超时，第二次完整通过并上传测试包。[文档基线 `3512ca4`](https://github.com/zcwjxy1412/Vivian-cutar/actions/runs/42593911539) 也已通过。CI、模拟、真机、用户自述分别记录。真实日常验收以可归属的游戏证据及清理结果为准。

## 从代码哪里开始

| 问题 | 入口 |
|---|---|
| 用户看到什么，首页如何启动 | [`MainWindow.xaml`](../src/VIvian.App/MainWindow.xaml)、[`MainViewModel.cs`](../src/VIvian.App/MainViewModel.cs) 的 `OneClickCommand` / `RunAccountBatchAsync`、[`AccountViewModel.cs`](../src/VIvian.App/AccountViewModel.cs) |
| 授权、单实例、输入宿主 | [`App.xaml.cs`](../src/VIvian.App/App.xaml.cs)、[`app.admin.manifest`](../src/VIvian.App/app.admin.manifest)、[`SingleInstanceGuard.cs`](../src/VIvian.Infrastructure/SingleInstanceGuard.cs)、[`InputHostClient.cs`](../src/VIvian.Infrastructure/Accounts/InputHostClient.cs) 与 [`InputHostServer.cs`](../src/VIvian.Infrastructure/Accounts/InputHostServer.cs) |
| OCR、选择、连续登录核验 | [`LoginFlow.cs`](../src/VIvian.Core/Accounts/LoginFlow.cs)、[`LoginRecognition.cs`](../src/VIvian.Core/Accounts/LoginRecognition.cs)、[`StarRailLoginDriver.cs`](../src/VIvian.Infrastructure/Accounts/StarRailLoginDriver.cs)、[`GameSessionVerifier.cs`](../src/VIvian.Infrastructure/Accounts/GameSessionVerifier.cs)、[画面流程](M5_2_ACCOUNT_SWITCH_SCREEN_FLOW.md) |
| 批次顺序、配置与停止 | [`MultiAccountOrchestrator.cs`](../src/VIvian.Infrastructure/Accounts/MultiAccountOrchestrator.cs)、[`UnifiedTaskControl.cs`](../src/VIvian.App/UnifiedTaskControl.cs)、[`TemporaryConfiguration.cs`](../src/VIvian.Infrastructure/Engines/TemporaryConfiguration.cs)、[`QueueScheduler.cs`](../src/VIvian.Core/Execution/QueueScheduler.cs) |
| 真实日常及归属证据 | [`EngineModel.cs`](../src/VIvian.Core/Engines/EngineModel.cs) 的 `RealExecutionPolicy`、[`March7thAdapter.cs`](../src/VIvian.Infrastructure/Engines/March7thAdapter.cs)、[`March7thUpstreamActivity.cs`](../src/VIvian.Infrastructure/Engines/March7thUpstreamActivity.cs)、[`March7thDailyEvidence.cs`](../src/VIvian.Infrastructure/Engines/Cli/March7thDailyEvidence.cs) |
| 账号数据与脱敏诊断 | [`LocalDatabase.cs`](../src/VIvian.Infrastructure/LocalDatabase.cs)、[`LocalDatabase.Accounts.cs`](../src/VIvian.Infrastructure/LocalDatabase.Accounts.cs)、[`DiagnosticBundle.cs`](../src/VIvian.Infrastructure/Diagnostics/DiagnosticBundle.cs) |
| 后续两款游戏 | [BetterGI 适配器现状](m4/M4_BETTERGI_ADAPTER.md)、[OneDragon 适配器现状](m4/M4_ONEDRAGON_ADAPTER.md)。文档中的版本和实测限制是固定基线，改上游版本要重新核验契约 |
| 对应测试与探针 | [`tests/VIvian.Tests/Accounts`](../tests/VIvian.Tests/Accounts)、[`tests/VIvian.Tests/Engines`](../tests/VIvian.Tests/Engines)、[`tests/VIvian.Tests/Processes`](../tests/VIvian.Tests/Processes)、[`tools/VIvian.LoginProbe`](../tools/VIvian.LoginProbe) |

批次先冻结本次所选账号的配置和顺序，再用实时截图核对保存卡片。世界就绪需要可靠的画面证据，标题、版本号或加载页 UID 不能替代。可读 UID 只是额外身份证据。确认当前账号结束、受监管进程退出且临时设置恢复后才切换。进程身份按 PID 和创建时间核对；助手完成证据须绑定本次运行。任务用户侧称为「日常」，`M5RealTask` 在当前版本实际调用的是上游 `routine`，单独 `daily` 不包括清体力。输入宿主没有独立窗口，批次结束后应退出。

合法的验证码、密码、协议或权限请求由本人处理。未知画面、身份不符、配置恢复未确认和进程不明都有停止分支。保留这些门禁和诊断脱敏，避免使用保存的登录凭据、反作弊绕过、进程注入或按名称批量终止。默认开发构建只运行模拟引擎；原神和绝区零维持真实执行门关闭，直到各自证据齐备。

## 接手首日操作

1. 在 `E:\auto agent game\Vivian-source` 运行 `git status --short --branch`、`git remote -v`、`git fetch origin`、`git log -5 --oneline`。核对 PR #52/#53 的 head、base 与 CI。修产品以 #52 的功能分支为准，整理和本文在 #53。新接手者可从当前基线另开短分支，保留所有未合并分支。
2. 先跑不触碰真实账号的 Windows 验证。在源码目录用 PowerShell 7 和 `global.json` 锁定的 SDK。默认构建和 M5.2 打包使用不同入口。

   ```powershell
   $env:Path = 'E:\auto agent game\.dotnet;' + $env:Path
   dotnet --version
   dotnet test tests/VIvian.Tests/VIvian.Tests.csproj -c Release -p:M5DailyTestBuild=true -p:RestoreLockedMode=true
   ./scripts/Build.ps1 -SmokeTest
   ./scripts/Build-M5_2Test.ps1 -Target 'artifacts/handoff-check/package'
   ```

   每次使用新的输出目录。[`Test-M5_2Local.ps1`](../scripts/Test-M5_2Local.ps1) 也能顺序执行默认、M4 手动和 M5 测试再打包。CI 定义见 [`windows.yml`](../.github/workflows/windows.yml)，远端包会过期。Linux 适合代码和部分测试，WPF、UAC、Windows 进程及实机状态需要 Windows 证据。
3. 遇到游戏启动、切换或卡住的问题，先取当前安装版本、批次状态、RunId、时间、助手运行编号、窗口画面和进程清理结论。`截图反馈\待处理截图` 是用户原始输入；`截图反馈\对应日志`、测试版数据目录的 `login-evidence` 和助手 `logs/runs` 是本机排查入口。分享前保留脱敏结果。可用 `VIvian.LoginProbe --check-game-path`、`--reference-flow` 做诊断，`--click`、`--switch`、`--restart` 会操作真实桌面。
4. 需要真机时才按 [M5.2 使用步骤](M5_2_DELIVERY.md) 测。先取得用户授权、核对账号与任务设置，UAC 时将输入留给用户。进度和验收记录逐账号保存，区分新领奖与已满的本次证据，完成后检查游戏、助手与宿主退出。
5. 仅在验证通过、确认程序退出后部署。校验 ZIP、暂存解包、比对 `BUILD-INFO.txt`，将旧程序目录移到 `项目归档\回滚版本`，再让新包占用同一个 `VIvian` 目录。校验安装和快捷方式。回滚时先退出程序，备份当前程序和 SQLite，换回兼容 schema v6 的完整程序目录；旧 schema 构建使用对应的独立备份数据。安装包和快捷方式的说明见本机 `使用说明.md`。

星铁助手目录 `E:\auto agent game\March7thAssistant-official-v2026.10.31\March7thAssistant_full` 与根目录官方 ZIP 都是验证输入。`M5-upstream-source` 目前只有 Git 数据，可通过 `git show 1e77d59:<path>` 读固定源码。`M4D-downloads` 保存下一阶段的上游包。项目不依赖个人会话中的旧交接脚本。旧版 `Build-M5DailyTest.ps1`、`Build-M5_1Test.ps1`、`Deploy-M5Local.ps1` 在各自旧提交可找回；当前只用上述 M5.2 路径。

本机 11 月 9 日清理移除了 8,742 个生成副本和旧入口（约 3.83 GiB，进入回收站），`Vivian-source\artifacts` 下的历史实测材料移到 `开发资料\测试证据\M5.2-2026-11-09`。清单在 `项目归档\历次整理报告\整理报告-2026-11-09\清理清单.csv`。历史发布、用户截图、运行数据和依赖包继续用于回滚或复验。`bin`、`obj` 和新的 `artifacts` 由构建命令再生成。

交付记录至少包含修改范围、源码和安装版本、验证命令、通过/失败/跳过数、真实操作与证据、账号和配置隔离情况、异常收尾及回滚位置。下一个工作项从第 1、2 阶段尚未验证的场景开始，先查现有实现和证据再决定是否需要改代码。
