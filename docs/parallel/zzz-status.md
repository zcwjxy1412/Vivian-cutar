# 绝区零 OneDragon 开发状态

更新于 2026-11-09（America/New_York）。

## 基线与边界

- BASE_SHA 为 `c44dde18c8fbb21caba770f8799d33b880f8f843`，与 AI 和原神分支一致。远端 [#53](https://github.com/zcwjxy1412/Vivian-cutar/pull/53) 以 `feature/m5-2-auto-account-switch@3512ca4` 为 base，head 即 BASE_SHA。最新 [#52](https://github.com/zcwjxy1412/Vivian-cutar/pull/52) head `3512ca4` 和正式安装源提交 `aff9a76` 均为 BASE_SHA 祖先。原源码工作区开工时干净。该基线没有 AGENTS.md；已核对交接手册、r7 实机验收和 AI 的共同基线。
- 工作在 `feature/m6-zzz-onedragon`。每次交付以草稿 PR 的提交列表和本页测试记录为准。
- 正式 `E:\auto agent game\VIvian\VIvian.exe` 的 BUILD-INFO 为 `aff9a76d7fc0884c35ff3656f0433cb3adfcefd4`，星铁助手 v2026.10.31，数据目录为 `%LOCALAPPDATA%\VIvian-M5-DailyTest`（schema v6）。开发与验证使用独立工作树及测试数据。

## 本机核对

| 项目 | 结果 |
| --- | --- |
| 安装与包型 | `E:\VIvianEngines\OneDragon-v2.6.0` 有集成启动器、`.runtime`、`src`、`assets/models`。本地 WithRuntime-Full ZIP 的 SHA-256 `2fd5f6bfc6c01fcf6f21092dc6e18fa18e146d2614ca6ad130d5fc3057ba0ced` 与 GitHub Release API 摘要一致。安装中对应归档的 3,118 个文件逐项比对一致。启动器无 Authenticode 签名，PE 未提供可用的 FileVersion。 |
| 身份与版本 | 启动器 SHA-256 `d4a8e2780eacc6754a88810d1d6b2fccdf4dd60a1e00725a12b5ebe8589158c1`。`--version` 以 0 退出并输出 v2.6.0。`--help` 以 0 退出并列出 `-o/-c/-i`。版本固定标签为 `5f2e473bbc5b100ae2ea482dd25be0277bbca87c`，许可证 GPL-3.0。 |
| 旧路径规则 | 共享正则只允许字母、数字、下划线、连字符和分隔符，拒绝实际根目录中的点号。实包可从该目录执行上述查询。OneDragon 专属检查只放行组件内部的点号，保留英文无空格、本地盘符、入口、重解析点和结构检查。 |
| 初始化 | 原安装没有 `.git`、`config` 或 `.log`。固定源码会在目标本地分支未建立时走首次同步，缺失 `auto_update_code` 默认启用。现有安装没有可确认的保存实例。 |
| 权限和归属 | 检查时开发进程非管理员。固定源码中 `-o` 在管理员启动器内工作，普通权限走异步提权转发后返回 0。真实任务、提权副本归属及游戏进程清理仍待观察。帮助输出的中文按本机编码输出，不能据此假设真实任务标准流是 UTF-8。 |
| 实例 | 固定版新实例从 1 开始分配；读取 `config/one_dragon.yml` 的 `instance_list` 作为清单。实例读取器仅返回编号和标志。未知编号、空列表或多选无法通过单实例检查，脚本实例编号不能证明游戏身份。 |

[官方 v2.6.0 发行页](https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon/releases/tag/v2.6.0) · [固定源码](https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon/tree/5f2e473bbc5b100ae2ea482dd25be0277bbca87c)

## 验证与公共依赖

- R1（安装诊断、实例和标准流结果）。基线 Windows M5 测试 733/733。增量以锁定 SDK 10.0.403 和锁定依赖运行 Release Windows M5，749/749 通过，跳过 0。回归测试先复现标准流出错被判成功及取消被判未确认，再验证修订。完整套件含真实 Windows Job Object 的同名外部进程保留和作业内子进程终止测试，进程为模拟引擎。
- R1 无共享代码和依赖变更。真实启动仍由公共准入负责。提交供 AI 窗口核对的最小需求为跨游戏桌面互斥和不可续跑的未清理/未恢复状态、按游戏和适配器精确放行的诊断入口，以及内部账号到上游实例的显式绑定和可单独查询的运行证据。
- 真机下一项为隔离副本中的单实例短任务。先确认共同桌面锁和提升权限的受监管宿主，再做精确进程紧急停止演练；之后核对游戏账号、实例和仅启用一项任务的配置。聚合结束行只用于对账，游戏结果与清理需各有证据。未确认清理或恢复时本轮留在阻断状态。
