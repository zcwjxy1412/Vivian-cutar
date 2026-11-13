# 原神基础配置与源项目详细设置 Implementation Plan

> For agentic workers: use superpowers:executing-plans.

Goal: furina 编辑简单参数；队伍等详细设置在 BetterGI 编辑并自动回读。
Architecture: 复用现有 ProfileRows、两秒轮询、源文件版本冲突保护和 OpenUserInterface，不新增存储。
Tech Stack: C# / .NET 10 / WPF。
Spec: 用户 2026-11-10 明确修订：简单参数在项目调整，详细队伍配置跳转源项目。

## Constraints and review focus
- 保留完整主界面、账号隔离、星铁链路、真实执行门。
- 隐藏编辑器不能删除既有队伍值；未绑定账号仍能设置简单任务。
- 源项目读回优先显示当前绑定账号字段。
- 未保存草稿不得在跳转后被外部修改冲掉。
- 入口必须打开程序，不能仅定位 EXE；不承诺自动导航到源项目内部页面。

## Task 1: 简单/详细设置分层
- [x] 修改 GenshinConfigurationViewModel：基础列表排除 DailyRewardPartyName / PartyName；详细列表保留源值只读展示。
- [x] 修改 MainWindow.xaml：详细设置区域提供源项目按钮、当前队伍只读摘要和回读说明。
- [x] 修改 App.xaml.cs 既有冒烟检查，核对两个列表完整覆盖参数且队伍不在可编辑列表。

## Task 2: 可靠跳转
- [x] 修改 GenshinSourceConfigurationViewModel：复用 OpenUserInterface；未保存改动阻断跳转，保留档案选择提示。
- [x] 运行 BetterGi 配置定向回归、WPF 构建和隔离冒烟；记录真实界面跳转未测试的限制。
- [x] 最终审查并提交本次增量。

Ruling: 本轮在已有干净合流工作树修改原神专属 UI，因为原神开发分支没有这些界面文件。无需更改公共契约和数据库。

验证：Windows M5 Release 1074/1074；定向 17/17；普通隔离自包含 WPF 冒烟通过（1024×768）。新增单元验证源队伍值回读和简单字段修改不改写队伍。最终只读审查无阻断项。
限制：未实测管理员进程打开真实 BetterGI，未部署，未运行真实游戏。
