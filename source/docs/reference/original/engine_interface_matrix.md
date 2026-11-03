# Vivian V1.0 三引擎外部调度与配置接口矩阵 (ENGINE_INTERFACE_MATRIX.md)

**文档路径**：`docs/research/ENGINE_INTERFACE_MATRIX.md`  
**设计目标**：为 Vivian V1.0 的三个独立引擎适配器（`BetterGiAdapter`、`March7thAdapter`、`ZzzOneDragonAdapter`）建立统一、无歧义的底层进程调用、配置读写、日志监听与退出状态判定规范。严禁使用任何未经验证的虚构命令行参数。

---

## 1. 核心进程调用与 CLI 接口对比矩阵

| 接口维度 | 原神：BetterGI (`v0.66.0`) | 崩坏：星穹铁道：March7thAssistant (`v2026.10.31`) | 绝区零：ZenlessZoneZero-OneDragon (`v2.6.0`) |
| :--- | :--- | :--- | :--- |
| **推荐可执行入口** | `{EngineRoot}\BetterGI.exe` [已从源码验证] | `{EngineRoot}\m7a.exe`（或 `March7th Assistant.exe`）[已从源码验证] | `{EngineRoot}\OneDragon-RuntimeLauncher.exe` [仅文档声明] |
| **备用/GUI 配置入口** | `{EngineRoot}\BetterGI.exe`（无参启动 GUI）[已从源码验证] | `{EngineRoot}\March7th Launcher.exe` [已从源码验证] | `{EngineRoot}\OneDragon-RuntimeLauncher.exe`（无参启动 GUI）[仅文档声明] |
| **一键日常（一条龙）命令** | `BetterGI.exe startOneDragon` [已从源码验证] | `m7a.exe main --after-finish None`（或 `March7th Launcher.exe main -e`）[仅文档声明] | `OneDragon-RuntimeLauncher.exe -o -c` [仅文档声明] |
| **指定配置组/子任务命令** | `BetterGI.exe startOneDragon <配置名>`（如 `默认配置`，不带尖括号）[已从源码验证] | `m7a.exe <task_id> --after-finish None`（支持 `daily`, `power`, `routine`, `fight`, `universe`, `currencywars`）[仅文档声明] | `OneDragon-RuntimeLauncher.exe -o -c -i <实例ID>`（如 `-i 1,2`）[仅文档声明] |
| **CLI 状态/列表查询** | 不支持（传入未知参数会被忽略并打开主界面）[已从源码验证] | `m7a.exe --status`（JSON 状态）<br>`m7a.exe --list --json`（JSON 任务列表）<br>`March7th Launcher.exe -l` [仅文档声明] | `OneDragon-RuntimeLauncher.exe -v`（版本号）<br>`OneDragon-RuntimeLauncher.exe -h`（帮助）[仅文档声明] |
| **工作目录 (`WorkingDirectory`)** | **必须**设为 `{EngineRoot}` [已从源码验证] | **必须**设为 `{EngineRoot}` [已从源码验证] | **必须**设为 `{EngineRoot}`（且路径必须纯英文无空格）[已从源码验证] |
| **关联游戏进程名** | 国服：`YuanShen.exe`<br>国际服：`GenshinImpact.exe` [已从源码验证] | `StarRail.exe` [已从源码验证] | `ZenlessZoneZero.exe` [仅文档声明] |
| **单实例/并发限制** | `.UseSingleInstance("BetterGI")` 强互斥锁；启动前必须先关闭已有 `BetterGI.exe` [已从源码验证] | `active_run` 互斥；不可同时运行两个任务实例 [仅文档声明] | 同一安装目录不可并发运行多个一条龙实例 [仅文档声明] |
| **优雅停止机制** | 1. 完成后配置自动退出（`CompletionAction = "关闭游戏和软件"`）<br>2. 外部终止：`CloseMainWindow()` -> 超时 5s 后 `Kill(entireProcessTree: true)` [已从源码验证] | 1. 发送全局停止热键（默认 `F10`，配置项 `hotkey_stop_task`）<br>2. 外部终止：`Kill(entireProcessTree: true)` [已从源码验证] | 1. `-c` 参数完成后自动关闭游戏并退出<br>2. 外部终止：`Kill(entireProcessTree: true)` [仅文档声明] |

---

## 2. 配置文件体系与关键字段映射矩阵

Vivian V1.0 采用 **“非侵入式配置补丁（Non-Intrusive Config Patching）”** 策略：不重写上游全量配置 Schema，仅读取与修改与“一键日常开关、游戏路径、完成后退出动作、禁用内置自动更新”直接相关的核心键值，其余高阶参数（如配队细节、战斗策略脚本）保留原文件结构并提供“打开原生引擎设置”入口。

### 2.1 原神：BetterGI 配置接口明细
- **验证状态**：**[已从源码验证]**（源码路径：[OneDragonFlowViewModel.cs](https://gitback.org/babalae/better-genshin-impact/src/commit/436a8d207ee302763edb207a545093b4cf22340d/BetterGenshinImpact/ViewModel/Pages/OneDragonFlowViewModel.cs)、[MainWindowViewModel.cs](https://gitback.org/babalae/better-genshin-impact/src/commit/900b6aec84911b8af00042e60984beb7ffbc4a0a/BetterGenshinImpact/ViewModel/MainWindowViewModel.cs)）
- **文件格式与编码**：JSON（`Newtonsoft.Json` 序列化，UTF-8 无 BOM）
- **核心配置文件路径**：
  1. 全局主配置：`{EngineRoot}\User\config.json`
  2. 一条龙工作流配置：`{EngineRoot}\User\OneDragon\<配置名>.json`（默认：`{EngineRoot}\User\OneDragon\默认配置.json`）
  3. 调度器配置组目录：`{EngineRoot}\User\ScriptGroup\*.json`
- **关键字段映射表**：

| 配置文件 | JSON Path / 属性名 | 类型 | Vivian 托管用途与推荐值 | 验证状态 |
| :--- | :--- | :--- | :--- | :--- |
| `User\config.json` | `SelectedOneDragonFlowConfigName` | `string` | 当前选中的一条龙配置名（如 `"默认配置"`） | [已从源码验证] |
| `User\config.json` | `CommonConfig.ExitToTray` | `bool` | 建议设为 `false`，防止主窗口关闭时驻留托盘不退出 | [已从源码验证] |
| `User\config.json` | `MaskWindowConfig.MaskEnabled` | `bool` | 是否启用游戏内遮罩日志窗口（默认 `true`） | [已从源码验证] |
| `User\OneDragon\<Name>.json` | `Name` | `string` | 一条龙配置名称（与文件名对应，如 `"默认配置"`） | [已从源码验证] |
| `User\OneDragon\<Name>.json` | `TaskEnabledList` | `Dictionary<string, bool>` | 子任务启用字典：<br>- `"领取邮件"`: `bool`<br>- `"合成树脂"`: `bool`<br>- `"每日委托"`: `bool`<br>- `"自动秘境"`: `bool`<br>- `"自动幽境危战"`: `bool`<br>- `"自动刷地脉花"`: `bool`<br>- `"自动首领讨伐"`: `bool`<br>- `"领取每日奖励"`: `bool`<br>- `"领取尘歌壶奖励"`: `bool`<br>- `"自动七圣召唤"`: `bool` | [已从源码验证] |
| `User\OneDragon\<Name>.json` | `CompletionAction` | `string` | 任务完成后动作，可选值：`["无", "关闭游戏", "关闭游戏和软件", "关机"]`。**Vivian 强制设为 `"关闭游戏和软件"`** | [已从源码验证] |
| `User\OneDragon\<Name>.json` | `CraftingBenchCountry` | `string` | 合成台国家：`["枫丹", "稻妻", "璃月", "蒙德"]`（v0.64+ 新增纳塔/至冬需实机确认） | [已从源码验证] |
| `User\OneDragon\<Name>.json` | `AdventurersGuildCountry` | `string` | 冒险家协会国家：`["枫丹", "稻妻", "璃月", "蒙德"]` | [已从源码验证] |
| `User\OneDragon\<Name>.json` | `DomainName` | `string` | 自动秘境名称（来自 `MapLazyAssets.Instance.DomainNameList`） | [已从源码验证] |

---

### 2.2 崩坏：星穹铁道：March7thAssistant 配置接口明细
- **验证状态**：**[已从源码验证]**（源码路径：[assets/config/config.example.yaml](https://github.com/moesnow/March7thAssistant/blob/main/assets/config/config.example.yaml)）
- **文件格式与编码**：YAML（扁平键值对结构，UTF-8 编码）
- **核心配置文件路径**：`{EngineRoot}\config.yaml`（首次运行前可由 Vivian 从 `{EngineRoot}\assets\config\config.example.yaml` 拷贝生成）
- **关键字段映射表**：

| YAML 键名 | 类型 | 默认值 | Vivian 托管用途与强制策略 | 验证状态 |
| :--- | :--- | :--- | :--- | :--- |
| `game_path` | `string` | `"C:\\Program Files\\Star Rail\\Game\\StarRail.exe"` | 本地游戏客户端路径（由 Vivian 全局游戏路径中心同步注入） | [已从源码验证] |
| `check_update` | `bool` | `true` | **Vivian 强制设为 `false`**（禁止启动时弹出更新提示阻塞自动化） | [已从源码验证] |
| `auto_update` | `bool` | `false` | **Vivian 强制设为 `false`**（由 Vivian 版本管理器统一接管更新） | [已从源码验证] |
| `pause_after_success` | `bool` | `true` | **Vivian 强制设为 `false`**（防止成功后控制台停留在 `input()` 阻塞不退出） | [已从源码验证] |
| `exit_after_failure` | `bool` | `false` | **Vivian 强制设为 `true`**（失败后立即返回非零退出码退出进程） | [已从源码验证] |
| `after_finish` | `string` | `"None"` | 任务完成后操作，Vivian 设为 `"Exit"`（关闭游戏）并在自身队列最后统一决定是否关机 | [已从源码验证] |
| `auto_set_resolution_enable` | `bool` | `true` | 启动游戏时自动设为 `1920x1080` 并关闭自动 HDR | [已从源码验证] |
| `use_background_screenshot` | `bool` | `true` | 优先使用后台截图，减少悬浮窗干扰 | [已从源码验证] |
| `daily_enable` | `bool` | `true` | 是否启用每日实训 | [已从源码验证] |
| `power_enable` | `bool` | `true` | 是否启用清体力总开关 | [已从源码验证] |
| `instance_type` | `string` | `"拟造花萼（金）"` | 清体力副本类型：`拟造花萼（金）` / `拟造花萼（赤）` / `凝滞虚影` / `侵蚀隧洞` / `饰品提取` | [已从源码验证] |
| `instance_names` | `Map<string,string>` | 见模板 | 各副本类型对应的具体副本名称字典 | [已从源码验证] |
| `build_target_enable` | `bool` | `false` | 是否启用游戏内“培养目标”自动匹配刷本 | [已从源码验证] |
| `echo_of_war_enable` | `bool` | `false` | 是否优先完成每周 3 次「历战余响」 | [已从源码验证] |
| `reward_enable` | `bool` | `true` | 是否领取奖励总开关（含委托 `reward_dispatch_enable`、邮件 `reward_mail_enable`、无名勋礼 `reward_srpass_enable`） | [已从源码验证] |
| `fight_enable` | `bool` | `false` | 完整运行中是否包含「锄大地」 | [已从源码验证] |
| `weekly_divergent_enable` | `bool` | `false` | 是否启用每周「差分宇宙」积分奖励 | [已从源码验证] |
| `currencywars_enable` | `bool` | `false` | 是否启用每周「货币战争」积分奖励 | [已从源码验证] |
| `forgottenhall_enable` | `bool` | `false` | 是否启用「混沌回忆」 | [已从源码验证] |
| `purefiction_enable` | `bool` | `false` | 是否启用「虚构叙事」 | [已从源码验证] |
| `apocalyptic_enable` | `bool` | `false` | 是否启用「末日幻影」 | [已从源码验证] |
| `hotkey_stop_task` | `string` | `"f10"` | 停止任务全局热键 | [已从源码验证] |
| `hotkey_pause_task` | `string` | `"f8"` | 暂停/继续任务全局热键 | [已从源码验证] |

---

### 2.3 绝区零：ZenlessZoneZero-OneDragon 配置接口明细
- **验证状态**：配置文件路径 **[已从源码验证]**（见 [.gitignore](https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon/blob/main/.gitignore)）；内部 YAML 具体字段名 **[尚待验证]**（因 `.gitignore` 忽略了用户运行期生成的 `config/*.yml`，仓库仅保留 `*.sample.yml` 模板，需 Codex 在原型阶段读取解压包的 `config/` 目录提取精确键名）。
- **文件格式与编码**：YAML（`pyyaml==6.0.3` 读写，UTF-8 编码）
- **核心配置文件路径**：
  - `{EngineRoot}\config\env.yml`（环境与自动更新配置）
  - `{EngineRoot}\config\game.yml`（游戏路径、区服、分辨率与按键启动参数）
  - `{EngineRoot}\config\one_dragon.yml` / `{EngineRoot}\config\zzz_one_dragon.yml`（一条龙全局及实例配置）
  - `{EngineRoot}\config\01\`（默认账号实例 01 的子任务配置目录，支持多实例 `01`, `02`...）
- **已知一条龙子任务模块（[仅文档声明]，见 [一条龙功能说明](https://one-dragon.com/zzz/zh/feat/feat_one_dragon/onedragon.html)）**：
  - `兑换码`、`邮件`、`每日签到`、`录像店营业`、`咖啡店`、`体力刷本`、`活跃度奖励`、`丽都城募`、`丽都周纪`、`恶名狩猎`、`迷失之地`、`枯萎之都`、`式舆防卫战`、`情报板`、`随便观`、`锄大地`、`真·拿命验收`、`驱动盘拆解`、`通知`。

---

## 3. 运行状态监控与结果判定状态机矩阵

由于三款引擎在设计上并非全部遵循 POSIX CLI 标准（尤其是 BetterGI 属于 GUI 宿主挂载命令行触发，ZZZ-OneDragon 属于 PySide6 启动器封装），**单靠进程 `ExitCode == 0` 无法 100% 区分“全部日常完成”、“中途识别卡死超时”与“用户手动关闭窗口”**。Vivian V1.0 必须采用 **“进程生命周期 + 标准流/文件日志双轨解析 + 游戏进程存活校验”** 的三维判定机制：

| 判定维度 | 原神：BetterGI | 崩坏：星穹铁道：March7thAssistant | 绝区零：ZZZ-OneDragon |
| :--- | :--- | :--- | :--- |
| **日志采集源** | 监听文件：`{EngineRoot}\log\better-genshin-impact{yyyyMMdd}.log`（FileSystemWatcher + FileStream `FileShare.ReadWrite` 增量读取）[已从源码验证] | 1. 直接重定向 `m7a.exe` / `March7th Assistant.exe` 的 `StandardOutput` / `StandardError`（UTF-8）<br>2. 监听 `{EngineRoot}\logs\*.log` [已从源码验证] | 1. 监听 `{EngineRoot}\.log\*.log`（FileStream `FileShare.ReadWrite`）<br>2. 捕获 `OneDragon-RuntimeLauncher.exe` 标准输出 [已从源码验证] |
| **启动成功标志** | 日志出现 `启用一条龙配置：` 或 `一条龙启动` [已从源码验证] | 进程存活且输出 `开始执行：` 或日志初始化完成 [仅文档声明] | 启动器拉起且 `.log/` 产生当日新日志行 [已从源码验证] |
| **子任务进度提取** | 正则匹配：`一条龙任务执行:\s*(\d+)/(\d+)` 与 `配置组任务执行:\s*(\d+)/(\d+)` [已从源码验证] | 匹配日志中各模块标题（如 `每日实训`、`清体力`、`领取奖励`）[仅文档声明] | 匹配 `[one_dragon_app.py ...]` 应用切换日志行 [已从源码验证] |
| **任务成功判定条件** | 1. 日志出现 `一条龙和配置组任务结束`<br>2. 日志未出现未处理的致命崩溃（`UnHandle Exception`）<br>3. `BetterGI.exe` 退出（或由 Vivian 在检测到结束日志 10s 后主动回收）[已从源码验证] | 1. 进程退出且 `ExitCode == 0`<br>2. 输出包含 `已完成` 且无致命报错 [已从源码验证] | 1. `OneDragon-RuntimeLauncher.exe` 正常退出（`ExitCode == 0`）<br>2. `ZenlessZoneZero.exe` 已关闭（带 `-c` 参数时）<br>3. `.log/` 尾部无未捕获 `Traceback` / `[ERROR]` [仅文档声明] |
| **任务失败/中断判定** | - **中断**：日志出现 `任务被取消，退出执行` 或进程被外部提前杀死<br>- **失败**：日志出现 `PaddleOcr预热异常`、`没有配置,退出执行!`、连续 `[ERR]` 或超过单任务最大超时阈值（默认 60 分钟） | - **失败**：`ExitCode != 0` 或日志出现 `发生错误` / `未完成`<br>- **中断**：进程在未输出完成标志前被杀死 | - **失败**：进程非零退出、日志出现 `Traceback` / `未检测到` 或运行超时<br>- **中断**：进程被提前终止 |
