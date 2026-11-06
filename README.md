# VIvian · 三游戏日常控制中心

Windows 桌面程序，用来统一调度三款开源游戏自动化引擎的日常任务：

| 游戏 | 引擎 |
|---|---|
| 原神 | [BetterGI](https://github.com/babalae/better-genshin-impact) |
| 崩坏：星穹铁道 | [March7thAssistant](https://github.com/moesnow/March7thAssistant) |
| 绝区零 | [ZenlessZoneZero-OneDragon](https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon) |

VIvian 不重写这些引擎，而是通过可以独立维护的适配器调用它们。

> **当前 M5 开发分支：**专用 Windows 测试版开放星铁固定版 `daily`，每次真实执行都须用户主动确认。三引擎任务目录和星铁部分配置读写已接入；BetterGI 与 OneDragon 的真实执行仍关闭。UAC 后 Worker 归属、真实任务完成及配置双向同步尚待用户桌面验收。`main` 的正式发布状态应以 [Releases](https://github.com/zcwjxy1412/Vivian-cutar/releases) 为准。
>
> 使用第三方游戏自动化工具存在账号处罚风险。VIvian 不绕过反作弊、不修改游戏内存、不注入进程、不收集账号密码，也无法保证不受处罚。

## 里程碑

| 里程碑 | 内容 | 状态 |
|---|---|---|
| M1 | WPF 基础工程、SQLite 配置、日志 | ✅ 自动化验收通过（`docs/M1_ACCEPTANCE.md`） |
| M2 | 执行内核：串行调度、三层状态、进程归属、崩溃对账、单实例、紧急停止、模拟队列界面 | ✅ 自动化验收通过（`docs/M2_ACCEPTANCE.md`） |
| M3 | 三引擎能力模型与安全门禁 | 已在 M4/M5 分支继续扩展；历史验收见 `docs/M3_PREREQUISITES.md` |
| M4 | 三引擎适配器与手动实测 | PR #46、#47 尚未合并，需按依赖顺序审查 |
| M5 | 版本化任务目录、星铁配置、`daily` 受控测试 | PR #48 基于 #47；本分支继续建设本地固定测试版与诊断闭环 |

## 本机固定测试版

开发机使用 `E:\auto agent game\VIvian\VIvian.exe`，或桌面快捷方式“VIvian（开发测试版）”。Codex 在源码测试和 WPF 冒烟通过后运行 `scripts/Deploy-M5Local.ps1` 更新固定目录；旧版移至 `项目归档` 可回滚。程序启动时自动发现已知安装，生成 `截图反馈\对应日志` 中的诊断会话。真实游戏只在用户主动确认后执行。GitHub CI Artifact 仍作备份。

## 运行便携版

从 [Releases](https://github.com/zcwjxy1412/Vivian-cutar/releases) 下载 `VIvian-<版本>-win-x64.zip`，解压到一个新目录，运行 `VIvian.exe`。这是自包含发布，不需要另外安装 .NET。当前版本未签名，Windows SmartScreen 可能会提示。

- **模拟队列**：为每款游戏选择一种模拟情形，开始队列，观察串行调度、紧急停止和结果判定
- **引擎设置**：保存三个引擎的安装目录（目前只保存，不会使用）
- **概览 / 自检记录**：基础自检

数据保存在 `%LOCALAPPDATA%\VIvian`，日志在其中的 `logs` 子目录。

## 当前状态与安全门禁（M5 分支）

- 默认构建仍关闭真实执行；只有带独立标记和数据目录的 M5 手动测试版允许星铁 `daily`。
- M1–M4 的 RunId、进程监管、清理阻断和紧急停止机制继续生效。
- 以下各项仍为 Pending，需要桌面实机或用户授权：桌面 UAC、提权子进程的归属、真实引擎行为、真实游戏。
- CI 通过只说明模拟程序和工程测试通过，**不等于**安全批准，也不是任何游戏的运行证明。

## 文档

文档导航见 [`docs/README.md`](docs/README.md)。它区分现行规则、验收记录、历史审查与外部研究原文。

## 测试引擎与模拟

- `tests/VIvian.FakeEngine`：一个无害的测试程序，按 `--scenario` 参数表现出成功、失败、卡死、拒绝停止、遗留子进程等行为，用来在 Windows 上验证 Job Object 监管、停止升级和崩溃对账。它只用于测试，不随产品发布。
- `tests/VIvian.CrashHost`：模拟 VIvian 自身崩溃的宿主，用来验证 kill-on-close 和重启对账。
- 调度器测试大多使用内存中的脚本引擎和虚拟时钟（`FakeTimeProvider`），结果不依赖机器速度。
- 需要真实 Windows 进程的测试在其他系统上显示为**跳过**，不算作通过。

## 构建

安装 `global.json` 指定的 .NET SDK（10.0.403），然后在 PowerShell 7 中运行：

```powershell
./scripts/Build.ps1 -SmokeTest
```

依次执行：锁定模式依赖还原、Release 构建、xUnit 测试、win-x64 自包含发布、WPF 冒烟测试、打包。输出位于 `artifacts/`。不需要管理员权限。

非 Windows 系统也可以构建和运行大部分测试；需要真实 Windows 进程的测试会显示为**跳过**，不会算作通过。

### CI 与发布

- 每次推送和 PR 都会在 GitHub `windows-latest` 上运行完整流程（`Windows validation` 工作流）。冒烟测试会检查：第二个实例被拒绝、模拟队列跑通、紧急停止耗时、界面线程响应、文字对比度（WCAG AA），并为每个标签页截图
- 发布：在 Actions → **Release** → Run workflow 中填写版本号。由 Windows CI 构建并验证通过后才会创建 Release；同名版本已存在时会直接停止，不会覆盖。如果存在 `docs/RELEASE_NOTES/<版本>.md`，会把它放在说明的最前面

## 工程结构

| 项目 | 职责 |
|---|---|
| `src/VIvian.Core` | 模型与契约；`Execution/` 下是三层状态、证据判定和串行调度器 |
| `src/VIvian.Infrastructure` | SQLite（模式 v2）、Job Object 进程监管、单实例锁、模拟引擎 |
| `src/VIvian.App` | WPF 界面、MVVM、依赖注入、日志 |
| `tests/VIvian.Tests` | 单元测试、持久化测试、真实进程场景测试、源码安全检查 |
| `tests/VIvian.FakeEngine` | 可编排行为的测试引擎（只用于测试，不随产品发布） |
| `tests/VIvian.CrashHost` | 崩溃场景测试用的宿主程序 |
| `docs` | 计划、决策、验收材料、M3 前置清单、外部评审材料（导航：`docs/README.md`） |
