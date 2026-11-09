# M6 原神 BetterGI 接入状态

日期为 2026-11-09（America/New_York）。**BASE_SHA** 为 `c44dde18c8fbb21caba770f8799d33b880f8f843`，与 [M6 共同基线](https://github.com/zcwjxy1412/Vivian-cutar/blob/6f01c04bf804a4e34b356d17c743ae87e0cb8962/docs/parallel/BASELINE.md) 一致。PR #53 基于 PR #52 的 `3512ca4`，包含当前正式安装的 `aff9a76`。工作分支为 `feature/m6-genshin-bettergi`。

## 本机只读检查

| 项目 | 观察结果 |
| --- | --- |
| 发行包与文件 | `C:\Program Files\BetterGI\BetterGI.exe`，文件版本 0.66.0.0，产品版本 0.66.0。官方 7z 的 GitHub 发布摘要为 `28d4756e55f32637538bf84427f5f31afd5b153bf996530c5975bd9c8c0b9343`，与留档包一致。重新提取的 EXE 与安装 EXE 的 SHA-256 同为 `73732f440bc8ff3b0272f2adbbf72acadcb952d45bf7f5a62cf0e3573dbd7d73`。EXE 无 Authenticode 签名，未比对整个安装目录。 |
| 目录、权限与 IPC | `Assets`、`GameTask`、`User`、`log` 存在，HKCU 与合并视图的协议注册均指向此安装。采样时无 BetterGI、原神或星铁运行。安装根 ACL 向 Everyone 和 BUILTIN\Users 授予 FullControl，当前宿主为普通权限。直接提升并启动此目录前需解决可写载荷问题。 |
| 更新 | 读到更新器、备份及旧文件；采样时无更新器进程。官方 Release 的 latest 指向 0.66.0。订阅更新和命令行前更新均关闭，脚本库最近更新值为初始值。检查只读。 |
| 游戏入口 | 配置指向存在的国服 `YuanShen.exe`，启用了联动、命令行启动、自动进入。现有保存账号及世界就绪状态待桌面确认。 |
| 一条龙 | 唯一配置为旧格式 `默认配置`。顺序是枫丹合成树脂（保留原粹 0）、自动秘境（虹灵的净土，周配置关闭，队伍名空），每日奖励关闭。结束动作空。全局秘境的指定树脂开关关闭，优先序是浓缩、原粹，各计数为 0，当前没有固定次数，自动分解圣遗物开启。 |
| 调度组 | 七组共 48 个启用的本地路径项目，各为 Daily、执行次数 1，未指定队伍；项目引用的本地路径文件均存在。默认一条龙没有引用这七组。路线实际运行和内容许可待各组选定后核验。 |
| 日志 | 11 月 1、2、3、5、7 日五份滚动日志含实例头，未找到一条龙启用、完成或取消行。历史日志用于核对格式，原文不上库。 |

官方依据见 [BetterGI 0.66.0 发布](https://github.com/babalae/better-genshin-impact/releases/tag/0.66.0)、[固定版本 IPC 启动](https://github.com/babalae/better-genshin-impact/blob/0af68c28f6b0e714d5304625ce644736d508022e/BetterGenshinImpact/Service/Instance/InstanceBootstrap.cs) 与 [固定版本一条龙](https://github.com/babalae/better-genshin-impact/blob/0af68c28f6b0e714d5304625ce644736d508022e/BetterGenshinImpact/ViewModel/Pages/OneDragonFlowViewModel.cs)。

## 本分支实现与公共接口

- 复用 `IGameAdapter`、`IEngineRun`、`RunOutcome` 和既有进程监管器。命令行以程序绝对路径、独立参数数组和工作目录传递。按 JSON 内部名称查找唯一一条龙配置。缺失、重名、错误格式或关机动作在启动前阻断。选中配置解析为显式命名请求，执行路径再次读取配置。
- BetterGI 0.66.0 的根管道按 Windows 用户命名，与安装目录无关。探测仅检查存在性，无法确定时阻断，不发送 IPC。预检和实际启动前复查。根进程以 0 或 0xFFFF 退出且没有可归属主实例日志时，结果和清理均未确认。外部启动与配置文件变化仍可能发生在最后一次检查之后，不能用预检替代实机门禁。
- 原神专用任务目录与写入能力下一增量交付。公共宿主需为原神显示检查、任务结果、进程清理、配置恢复为独立状态。暂停、继续保持不可用，需实际确认通道才可启用。公共桌面互斥由 AI 窗口确定契约。
- 本分支未部署。验证使用独立工作树和测试夹具。原神实机成功、失败、取消、清理与恢复分别待验；正式执行门仍关闭。

## Windows 验证

- 适配器提交："c965c02f3deb04c7ee93f95d33642c0a8b9ee7a9"。
- M5 基线 733/733。首个原神增量 Windows M5 Release 740/740，0 失败、0 跳过。覆盖缺失或重名配置、启动竞态、真实 Windows 命名管道探测和 IPC 转发未确认结果。

## 第二增量 248770eb986539e7b4c59d046317c3c57382f280

- Windows M5 Release 745/745，0 失败、0 跳过。BetterGI 停止测试用真实 Windows Job 启动无害替身，根进程派生子进程后退出。紧急停止终止子进程，Job 归零，运行保持结果未确认，清理确认。
- `VIvian.BetterGiProbe inspect <安装目录> [配置名]` 在本机安装只读验证成功。它返回版本、预检、有效任务顺序和设置、能力标志及任务参数目录。指定不存在配置返回退出码 1。两份原配置的 SHA-256 前后一致。
- 一条龙同时兼容旧任务名键和新任务 ID／顺序／续跑标记。启用的外部配置组必须存在且内部名称匹配。日常奖励启用时，模拟的流程结束须伴随同一主进程在激活、进度之后产生的奖励核查。该核查只证明委托奖励处于已领取状态，不能区分新领取和此前已领。
- AI 可读取 `BetterGiTaskCatalog.Tasks`、`BetterGiTaskCatalog.Parameters` 和 `BetterGiConfiguration.InspectOneDragonAsync`。参数能力均标记为上游管理，等待写入验收。公共账号配置契约见 AI 提交 `771b7fb`，本分支当前只公开安装级只读快照。
- 共享桌面锁由 AI 提交 `fdc503e` 提供。后续实机诊断须调用 `DesktopOperationLease.Acquire`，从运行前标记待恢复直到确认进程清理与配置回读，再释放桌面。

## 第三增量 27f3f34b0bdbbf2bee8868e7f0870dfa25be8bb2

Windows M5 Release 759/759，0 失败、0 跳过。WPF 与诊断工具的 Release 构建均为 0 警告、0 错误。


- 只读快照现在包含秘境的树脂优先序、旧版与新版计次字段、自动分解状态。已核对本机默认秘境启用了分解圣遗物，树脂次数未固定。预检会阻断此类启动。
- 隔离一条龙配置通过版本摘要核对、暂存后更名、回读、恢复记录和冲突保留验证。恢复只使用能与运行前或临时写入摘要匹配的文件。BetterGI 运行中会规范化改写配置，当前真实诊断使用独立配置，现有安装的写入能力保持关闭。
- 吸收 AI 窗口的桌面锁 `fdc503e` 与加固提交 `91cf18e` 的源文件、测试和初始化脚本。锁为同一 Windows 用户所用，使用已初始化、实际文件路径一致且只有单一链接的文件。出现未确认清理或注册恢复冲突时保留锁标记。
- `run-daily` 是独立诊断入口。它要求已经提权的同一 Windows 用户、由准备脚本生成的受保护隔离安装、同一会话仅有一个路径相符的前台原神客户端。首跑只执行「领取每日奖励」，其中包含尝试领取纪行奖励。队伍留空，不配置树脂任务，结束后关闭 BetterGI，保留原神。隔离设置关闭原神联动启动、自动登录及 HDR 修改。
- 诊断报告记录 `RunId`、根 PID 与创建时间、主实例归属、委托奖励核查、流程结束、异常信号、退出码、取消或超时、进程清理、协议注册恢复和原配置摘要核对。诊断观察与正式 `RunOutcome` 分开。缺少可归属主实例证据时，即使 Job 已空也保留清理未确认。
- 本轮实际验证包括普通权限拒绝、官方包摘要、隔离方案预览、本机配置读取、Windows Job 无害替身急停和模拟日志。管理员环境、真机流程、协议恢复及账号切换留待桌面验收。原神暂停、继续与正式写入能力保持关闭。

### 复跑步骤

普通 PowerShell 7 从原神工作树执行。只读方案预览无需管理员权限。

```powershell
$dotnet = 'E:\auto agent game\.dotnet\dotnet.exe'
$probe = 'tools\VIvian.BetterGiProbe'
$archive = 'E:\auto agent game\M4D-downloads\BetterGI_v0.66.0.7z'
$game = 'E:\Genshin Impact Game\YuanShen.exe'
& $dotnet build "$probe\VIvian.BetterGiProbe.csproj" -c Release -p:RestoreLockedMode=true
$probeDll = "$probe\bin\Release\net10.0-windows10.0.19041.0\VIvian.BetterGiProbe.dll"
& $dotnet $probeDll inspect 'C:\Program Files\BetterGI'
& "$probe\Prepare-Acceptance.ps1" -Archive $archive -GameExecutable $game -PlanOnly
```

真机可用时，退出旧版 furina 及已有 BetterGI，结束其他桌面验收。先在普通终端运行 `scripts/Initialize-M6DesktopLock.ps1`。同一用户确认 Windows 管理员提示后，在管理员 PowerShell 中使用上述变量准备独立安装。本人确认进入目标账号的世界，再运行短任务并在 10 秒内切回原神。需要停止时，切回诊断终端按 Ctrl+C。

```powershell
$stage = & "$probe\Prepare-Acceptance.ps1" -Archive $archive -GameExecutable $game
& $dotnet $probeDll run-daily $stage
```

先核对报告中的 PID 归属、奖励证据、清理与恢复，再扩大任务范围。清理或恢复未确认时保留 `protocol-recovery.json` 和共同桌面锁标记，交由原档案对账。出现进程归属或协议变更冲突时目前需人工复核。

## 下一步

1. 用户桌面可用后，完成首次管理员环境准备、单任务启动归属和清理。
2. 分开核对首次真机的成功、失败、取消和未确认路径。核对真实清理与恢复后再开放正式执行能力。
3. 单账号稳定后，核对原神保存账号画面和身份依据，再实现隔离设置和 A→B 复验。
