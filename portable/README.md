# VIvian · M1 基础工程

Windows 三游戏日常控制中心的第一里程碑。**本包仅为基础工程预览，不执行任何游戏任务。**

## 已提供

- .NET 10 / WPF / MVVM，中文深色基础界面，四个页面。
- Generic Host 依赖注入、Serilog 本地滚动日志。
- SQLite 配置与基础自检记录；事务保存、乐观并发控制和数据库版本保护。
- 真实 xUnit 持久化测试与 Windows 构建/冒烟脚本。
- 普通用户权限启动，不读取密码、不调用三引擎、不修改上游配置。

## 运行便携预览

Windows x64：解压便携包到新目录，启动 `VIvian.exe`。这是自包含发布，无需额外安装 .NET Runtime。Windows SmartScreen 可能提示未签名应用；当前预览没有代码签名，不自动调整系统安全设置。

1. “引擎设置”中可保存三个安装目录；也可保持空白。
2. “概览”点击“运行基础自检”，执行配置读取、数据库写入和回读。
3. 在“自检记录”查看结果。此记录不表示游戏日常完成。

普通运行数据位于 `%LOCALAPPDATA%\VIvian`，日志位于其 `logs` 子目录。设置只写入 VIvian 自身数据库。重新加载会放弃尚未保存的输入。

## 构建

安装 global.json 指定的 .NET SDK（10.0.403），在 Windows PowerShell 7 中进入源码目录：

```powershell
./scripts/Build.ps1 -SmokeTest
```

脚本依次执行锁定依赖还原、Release 构建、xUnit、Windows x64 自包含发布、WPF 启动冒烟和ZIP打包。输出位于 `artifacts/`。不需要管理员权限；脚本不安装引擎或游戏，不执行系统关机。

GitHub Actions 工作流在 push / PR / 手动触发时运行同一流程；只有上传到实际仓库并完成运行后才有CI结论。本交付不假称已创建远程PR。

## 工程结构

| 项目 | 职责 |
|---|---|
| src/VIvian.Core | 配置与自检记录模型、存储契约、输入约束 |
| src/VIvian.Infrastructure | SQLite 模式初始化、事务保存、冲突检测、历史查询 |
| src/VIvian.App | WPF 页面、MVVM、依赖注入、日志与错误反馈 |
| tests/VIvian.Tests | 临时数据库上的隔离持久化与异常测试 |
| scripts | 构建、发布、Windows WPF 冒烟 |
| docs | 决策、M1验收、已知限制及PR草稿 |

## 当前边界

尚无调度器、真实适配器、引擎更新回滚、托盘和安装程序。M2～M6 未实施。路径保存不代表已安装或兼容；界面始终显示“尚未接入”。没有伪造CLI或游戏成功记录。

本地构建成功也不能替代Windows视觉、高DPI、UAC、真实引擎及游戏验收。验收状态以 `docs/M1_ACCEPTANCE.md` 为准。
