# VIvian · 三游戏日常控制中心

Windows 桌面程序，用来统一调度三款开源游戏自动化引擎的日常任务：

| 游戏 | 引擎 |
|---|---|
| 原神 | [BetterGI](https://github.com/babalae/better-genshin-impact) |
| 崩坏：星穹铁道 | [March7thAssistant](https://github.com/moesnow/March7thAssistant) |
| 绝区零 | [ZenlessZoneZero-OneDragon](https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon) |

VIvian 不重写这些引擎，而是通过可以独立维护的适配器调用它们。

**当前集成分支正在修正 BetterGI 的正式调用链。** 原神从原安装目录运行用户保存的账号一条龙档案；Vivian 负责登录、身份核对、调度、结果记录和切号，不执行地图、传送、合成或领奖操作。当前支持合成后领奖或仅领奖，秘境执行尚未开放。两账号完整实机验收由用户操作，尚未通过。本轮审查范围与结论见 [架构审查记录](docs/reviews/2026-11-10-bettergi-native.md)。历史里程碑见 [项目计划与接手手册](docs/PROJECT_HANDOFF.md)。

>
> 使用第三方游戏自动化工具存在账号处罚风险。VIvian 不绕过反作弊、不修改游戏内存、不注入进程、不收集账号密码，也无法保证不受处罚。

## 里程碑

| 里程碑 | 内容 | 状态 |
|---|---|---|
| M1 | WPF 基础工程、SQLite 配置、日志 | ✅ 自动化验收通过（`docs/M1_ACCEPTANCE.md`） |
| M2 | 执行内核：串行调度、三层状态、进程归属、崩溃对账、单实例、紧急停止、模拟队列界面 | ✅ 自动化验收通过（`docs/M2_ACCEPTANCE.md`） |
| M3 | 三引擎能力模型与安全门禁 | 已在 M4/M5 分支继续扩展；历史验收见 `docs/M3_PREREQUISITES.md` |
| M4 | 三引擎适配器与手动实测 | PR #46、#47 尚未合并，需按依赖顺序审查 |
| M5 | 版本化任务目录、星铁配置、`daily` 受控测试 | #48→#49→#50；保留未合并依赖 |
| M5.1 | 账号档案与安全控制 | #51，旧交付说明保留用于追溯 |
| M5.2 | 保存账号自动切换、批次与配置隔离 | #52；自动化与双账号实机通过，更广的实机矩阵待验收 |

## 本机固定测试版

本机统一入口为根目录或桌面的 **「启动 furina」**，指向 `E:\auto agent game\VIvian\VIvian.exe`，已安装 `aff9a76`。启动时完成 UAC，扫描、勾选账号并保存游戏任务设置，然后在首页点一次「一键日常」。安装 ZIP 和回滚版在 `项目归档`。使用步骤见 [M5.2 操作说明](docs/M5_2_DELIVERY.md)。

## 运行便携版

从 [Releases](https://github.com/zcwjxy1412/Vivian-cutar/releases) 下载 `VIvian-<版本>-win-x64.zip`，解压到一个新目录，运行 `VIvian.exe`。这是自包含发布，不需要另外安装 .NET。当前版本未签名，Windows SmartScreen 可能会提示。

- **模拟队列**：为每款游戏选择一种模拟情形，开始队列，观察串行调度、紧急停止和结果判定
- **引擎设置**：启动时发现已知安装并保存有效路径；星铁配置可读取并编辑少量已核对的布尔参数
- **概览 / 自检记录**：基础自检

固定 M5 测试版的数据保存在 `%LOCALAPPDATA%\VIvian-M5-DailyTest`，与正式版隔离；脱敏反馈另存于 `E:\auto agent game\截图反馈\对应日志`。

## 当前状态与安全门禁（M5.2 分支）

- 默认构建使用模拟引擎。带独立标记和数据目录的 M5.2 测试版允许星铁 `routine`，在界面中称为「日常」。批次配置与权限授权在运行前完成。
- M1–M4 的 RunId、进程监管、清理阻断和紧急停止机制继续生效。
- [验收记录](docs/M5_2_ACCEPTANCE.md) 分列自动化、双账号实机证据及异常场景待测项。

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

- `main` 推送和 PR 在 GitHub `windows-latest` 上运行完整流程（`Windows validation` 工作流），避免同一功能分支的 push 与 PR 重复执行。冒烟测试检查：第二个实例被拒绝、模拟队列跑通、紧急停止耗时、界面线程响应、文字对比度（WCAG AA），并为每个标签页截图
- 发布：在 Actions → **Release** → Run workflow 中填写版本号。由 Windows CI 构建并验证通过后才会创建 Release；同名版本已存在时会直接停止，不会覆盖。如果存在 `docs/RELEASE_NOTES/<版本>.md`，会把它放在说明的最前面
- 本分支 CI 上传普通开发包（14 天）和 M5.2 包（30 天），测试与冒烟证据保留 30 天。同一个 PR 的新提交会取消旧的未完成 CI。
- 整理策略、分支保留和可再生输出见 [`docs/CLEANUP_2026_11_08.md`](docs/CLEANUP_2026_11_08.md)。构建输出留在 `.gitignore` 的 `bin/obj/artifacts` 中，不提交到仓库。

## 工程结构

| 项目 | 职责 |
|---|---|
| `src/VIvian.Core` | 模型与契约；`Execution/` 下是三层状态、证据判定和串行调度器 |
| `src/VIvian.Infrastructure` | SQLite（模式 v6）、Job Object 进程监管、单实例锁、登录识别和账号批次 |
| `src/VIvian.App` | WPF 界面、MVVM、依赖注入、日志 |
| `tests/VIvian.Tests` | 单元测试、持久化测试、真实进程场景测试、源码安全检查 |
| `tests/VIvian.FakeEngine` | 可编排行为的测试引擎（只用于测试，不随产品发布） |
| `tests/VIvian.CrashHost` | 崩溃场景测试用的宿主程序 |
| `docs` | 计划、决策、验收材料、M3 前置清单、外部评审材料（导航：`docs/README.md`） |
