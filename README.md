# VIvian · 三游戏日常控制中心

Windows 桌面程序，用来统一调度三款开源游戏自动化引擎的日常任务：

| 游戏 | 引擎 |
|---|---|
| 原神 | [BetterGI](https://github.com/babalae/better-genshin-impact) |
| 崩坏：星穹铁道 | [March7thAssistant](https://github.com/moesnow/March7thAssistant) |
| 绝区零 | [ZenlessZoneZero-OneDragon](https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon) |

VIvian 不重写这些引擎，而是通过可以独立维护的适配器调用它们。

> **当前版本 0.2.0（M2）：仅模拟模式。** 已完成执行内核，但**尚未接入任何真实引擎**，不会启动游戏或任何程序。“模拟队列”中的结果都带【模拟】标签，不代表真实的日常完成情况。
>
> 使用第三方游戏自动化工具存在账号处罚风险。VIvian 不绕过反作弊、不修改游戏内存、不注入进程、不收集账号密码，也无法保证不受处罚。

## 里程碑

| 里程碑 | 内容 | 状态 |
|---|---|---|
| M1 | WPF 基础工程、SQLite 配置、日志 | ✅ 自动化验收通过（`docs/M1_ACCEPTANCE.md`） |
| M2 | 执行内核：串行调度、三层状态、进程归属、崩溃对账、单实例、紧急停止、模拟队列界面 | ✅ 自动化验收通过（`docs/M2_ACCEPTANCE.md`） |
| M3 | 接入三款真实引擎 | ⏳ 等待前置条件（`docs/M3_PREREQUISITES.md`） |
| M4～M6 | 版本检测与更新回滚、托盘与主题、安装器与签名 | 未开始 |

## 运行便携版

从 [Releases](https://github.com/zcwjxy1412/Vivian-cutar/releases) 下载 `VIvian-<版本>-win-x64.zip`，解压到一个新目录，运行 `VIvian.exe`。这是自包含发布，不需要另外安装 .NET。当前版本未签名，Windows SmartScreen 可能会提示。

- **模拟队列**：为每款游戏选择一种模拟情形，开始队列，观察串行调度、紧急停止和结果判定
- **引擎设置**：保存三个引擎的安装目录（目前只保存，不会使用）
- **概览 / 自检记录**：基础自检

数据保存在 `%LOCALAPPDATA%\VIvian`，日志在其中的 `logs` 子目录。

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
| `docs` | 计划、决策、验收材料、M3 前置清单、外部评审材料 |
