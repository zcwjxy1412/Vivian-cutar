# VIvian V1.0 架构审查报告

| 项目 | 内容 |
|---|---|
| 审查对象 | Gemini 产出的六份研究与架构文档（`architecture_proposal.md`、`engine_interface_matrix.md`、`license_and_risk.md`、`ui_ux_spec.md`、`update_strategy.md`、`upstream_audit.md`） |
| 审查性质 | 独立架构审查 + QA 验收基线，不默认待审方案正确 |
| 审查基准日 | 2026-11-03（America/Chicago） |
| 审查方式 | 源码逐文件核对 + GitHub Releases API 实测 + 本地克隆比对；**未运行任何游戏客户端**，故所有"运行时行为"结论均标注为待实机验证 |
| 证据分级 | **[已核实]** 本人直接读取源码/API 得到；**[文档声明]** 仅上游文档或待审文档自述；**[待验证]** 需 Codex 用发行包实机测试；**[推定]** 基于同类实现的工程推断 |
| 结论 | **有条件阻断**：5 项 Critical 未闭合前不建议进入正式编码（详见第 6 节） |

> 本文档只做审查，不修改 Gemini 原始报告，也不包含主程序实现。

---

## 1. 结论摘要（先看这里）

待审方案的整体方向是正确的：**进程外集成、串行队列、声明式能力清单、原子更新** 都是这个项目该有的骨架；技术栈选择（C#/WPF + 进程外适配器）在工程上也站得住。**但它把"上游行为"当成了已知常量，而实测表明三个上游的"成功/失败/版本"语义都存在系统性偏差**，其中任何一条都足以让 V1.0 的核心承诺（"统一任务管理、可靠完成判定、可回滚更新"）失效。

按严重度计：**Critical 5 项、High 10 项、Medium 8 项、Low 5 项**。

最关键的三个事实性发现：

1. **ZZZ 的退出码不能用于判定任务结果**。`src/one_dragon/launcher/application_launcher.py:82-113` 在 `run_application()` 返回后只调用 `execute_after_done()`，**从不检查 `run_result.success`，也不设置进程退出码**；同时 `src/one_dragon/launcher/exe_launcher.py:50-61` 在非管理员时 `pyuac.runAsAdmin(sys.argv, wait=False); sys.exit(0)`，父进程立即以 0 退出。待审方案 `engine_interface_matrix.md:107` 写的"ExitCode==0 + 游戏关闭 + 无 ERROR"实际会**把失败判成成功**。
2. **"冻结上游版本"的设计不成立**。ZZZ 的 `auto_update_code` 默认 `true`（`src/one_dragon/envs/env_config.py:223-228`），`force_update` 默认 `true`（`:207-212`，注释明确"会直接丢弃现有的改动"），`src/one_dragon/launcher/runtime_launcher.py:24-80` 每次启动都 `git fetch/checkout`。Vivian 锁定的 v2.6.0 会在用户第一次点"开始"时被拉到 main 最新代码，兼容性门禁与回滚点当场失效。
3. **待审方案自己写下的"强制 `auto_update: false`"，会让 M7A 完全无法启动**。`main.py:350-354` 的 `first_run()` 用 `config.yaml` 的 `auto_update` 作为"是否已完成首次免责声明确认"的判据；置 false 后 CLI 直接 `sys.exit(EXIT_FIRST_RUN_REQUIRED=3)`。也就是说 `architecture_proposal.md:98`、`engine_interface_matrix.md:61`、`update_strategy.md:82` 三处相互印证的"安全加固"，实际是**功能性自毁**。

这三条不是"实现细节待定"，而是**需求与上游契约冲突**；在它们被重新设计之前开始写主程序，等于把返工写进排期。

---

## 2. 事实核查表

核查方法：GitHub API 取 tag/commit/release 元数据；按 tag 克隆并逐文件读取（BetterGI `0.66.0`，March7thAssistant `v2026.10.31`，ZZZ-OneDragon `v2.6.0`）。本地路径见附录 A。表中"判定"列：✅ 属实、⚠️ 部分属实/表述过强、❌ 与实测不符、❓ 无法核实。

### 2.1 版本与发布元数据

| # | 待审文档断言 | 实测结果 | 判定 |
|---|---|---|---|
| V-01 | BetterGI v0.66.0，2026-09-26，Commit `0af68c2` | Releases API：`0.66.0`，published `2026-09-26T13:58:48Z`，tag commit `0af68c2` | ✅ |
| V-02 | BetterGI 安装包 481 MB / 7z 444 MB | `BetterGI.Install.0.66.0.exe` 504,209,179 B；`BetterGI_v0.66.0.7z` 465,128,660 B（=443.6 MiB） | ✅（MiB 口径） |
| V-03 | M7A v2026.10.31，Commit `1e77d59`，full 452 MB / update 156 MB | `v2026.10.31` published `2026-10-31T11:47:54Z`；`full.7z` 474,333,248 B（452 MiB）、`update.7z` 163,523,697 B（156 MiB） | ✅ |
| V-04 | ZZZ v2.6.0，Commit `5f2e473`，WithRuntime-Full 269 MB、RuntimeLauncher 198 MB | `WithRuntime-Full.zip` 282,033,578 B（269 MiB）；`RuntimeLauncher.zip` 207,982,295 B（198 MiB）；published `2026-10-30T19:45:40Z` | ✅ |
| V-05 | "三项目均公开 sha256（此外 ZZZ 有 SignPath，M7A/BGI 有 GPG 签名）"（`update_strategy.md:60`） | 三个 release 的 assets 均带 `digest: sha256:…`；**没有** `.sig`/`.asc`/签名资产；SignPath 与 GPG 说法无法由元数据证实 | ❌（GPG）/❓（SignPath，需 Authenticode 实测） |
| V-06 | BetterGI 当前主线为 v0.66.0 | default_branch = `refactor/20260930`，`pushed_at` 2026-11-01（v0.66.0 之后仍有提交）；该分支已升 .NET 10 | ⚠️ 发布版正确，但"上游稳定"假设不成立 |
| V-07 | 文档自称路径为 `docs/architecture/…` 等 | 六份文件实际位于项目根目录，无 `docs/` 目录 | ⚠️ 仅路径表述问题 |

### 2.2 BetterGI 接口与行为

| # | 待审文档断言 | 实测（v0.66.0 源码） | 判定 |
|---|---|---|---|
| B-01 | CLI 解析在 `OneDragonFlowViewModel.cs:572-598` 的 `OnLoaded` | 解析已集中到 `BetterGenshinImpact/Helpers/CommandLineOptions.cs`：`:18` 惰性 `Parse(Environment.GetCommandLineArgs())`；`:128` `arg1.Contains("startOneDragon")`；`:135` `--startGroups`；`:146` `arg1.Contains("start")`。`OneDragonFlowViewModel.cs:544-569` 只负责消费结果 | ❌ 位置错误（行为大体正确） |
| B-02 | 单实例锁是 `.UseSingleInstance("BetterGI")` | 源码中不存在该 API。`Helpers/RuntimeHelper.cs:119` `CheckSingleInstance(string, Action<bool>)` 用 `EventWaitHandle.OpenExisting`，已存在则 `Environment.Exit(0xFFFF)` | ❌ |
| B-03 | 非管理员退出码 `559` 并 runas 自重启（`upstream_audit.md:26/67`、`ui_ux_spec.md:129`） | `RuntimeHelper.cs`：`Environment.Exit(exitCode ?? 'r'+'u'+'n'+'a'+'s')` = 114+117+110+97+115 = **553**（0x229） | ❌ 数字错误 |
| B-04 | 退出码只有 0 / 559 / 65535 | 0（正常关闭，**含任务内部失败但走完流程**）、553（提权自重启）、65535（单实例冲突）；另有异常崩溃的非零值 | ⚠️ 不完整 |
| B-05 | 传错配置名会被发现 | `OneDragonFlowViewModel.cs:544-569`：找不到配置只 `_logger.LogWarning("未找到，请检查。")`，随后仍 `OnOneKeyExecute()` 用当前 `SelectedConfig` 执行；`:567` 在列表为空时还可能 NRE | ❌ **静默跑错配置**，文档未提 |
| B-06 | 任务完成标志 `一条龙和配置组任务结束` | `OneDragonFlowViewModel.cs:707` 确实输出；但它在 `CompletionAction`（`:712-728`）执行**之前**；取消标志 `:688` "任务被取消，退出执行" | ✅（文档 `architecture_proposal.md:95` 的 15 s 等待是经验值） |
| B-07 | `CompletionAction` 取值 `["无","关闭游戏","关闭游戏和软件","关机"]` | switch 实际识别 `关闭游戏`、`关闭软件`、`关闭游戏和软件`、`关机` 四值，文档**漏了"关闭软件"** | ⚠️ |
| B-08 | 配置写入时机："退出时会回写覆盖" | `Service/ConfigService.cs:60` `Config.OnAnyChangedAction = Save;`：**任何属性变更即时落盘**；`App.xaml.cs:120` 启动归一化后也 `Save()` | ⚠️ 实际风险更高 |
| B-09 | 日志 `log/better-genshin-impact{yyyyMMdd}.log` | `App.xaml.cs:69-88`：`RollingInterval.Day`、`shared: true`、保留 31 文件/21 天；`outputTemplate` 为 `[HH:mm:ss.fff] [Level:u3] [BgiInstance] SourceContext\nMessage\nException` → **每条日志 3 行**，且带 `[{InstanceType}:S{session}:P{pid}:T{start}]` 前缀 | ✅ 路径对，格式复杂度被低估 |
| B-10 | 技术栈 "C# / .NET 10" 与上游同栈（`architecture_proposal.md:10,15`） | `BetterGenshinImpact.csproj:8` `<TargetFramework>net8.0-windows10.0.22621.0</TargetFramework>`；.NET 10 仅存在于 `refactor/20260930` | ❌（对 0.66.0） |
| B-11 | "未知参数会被忽略并打开主界面"（`engine_interface_matrix.md:16`） | `:146` `arg1.Contains("start")` 会匹配任何含 "start" 的首参；无参数时正常开主界面。行为方向正确，但"忽略"意味着**拼错命令不会报错**，是危险特性而非无害特性 | ⚠️ |
| B-12 | BetterGI 不支持 CLI 状态查询 | 源码中确未发现状态查询入口（`--TaskProgress` 是进度上报而非查询） | ✅ |

### 2.3 March7thAssistant 接口与行为

| # | 待审文档断言 | 实测（v2026.10.31） | 判定 |
|---|---|---|---|
| M-01 | 新增 `m7a.exe` CLI，`--status`、`--list --json`、`<task> --after-finish None` | `utils/cli_entry.py` + `assets/agent/skills/march7th-assistant/SKILL.md` 证实；实际能力更全：`--workflow-name`、`--detach`、`--follow latest`、`--control current pause|resume|stop`、`--force` | ✅（且文档低估） |
| M-02 | `--status` 返回 `first_run_completed` 等字段 | 属实；返回 version/install_dir/first_run_completed/game_path_exists/active_run/recent_runs/logs_dir/runs_dir/gui_running/config_writable | ✅ |
| M-03 | 退出码 0 成功、1 失败 | `utils/runs.py:20-26`：0 OK、1 FAILED、2 USAGE、3 FIRST_RUN_REQUIRED、4 BUSY、5 ELEVATION_FAILED、6 STOPPED | ⚠️ 不完整（漏 3/4/5/6，其中 3/4 是常态） |
| M-04 | 成功判定 = `ExitCode==0` 且输出含"已完成"（`engine_interface_matrix.md:107`） | 程序有结构化结果：`logs/runs/<run_id>.json` 含 `status`（running/success/failed/blocked/stopped/interrupted/aborted）、`exit_code`、`error`、`log_file`、`screenshot`；`--follow latest` 可取尾。文本匹配不可靠（`--json` 输出 `ensure_ascii=True`，中文为 `\uXXXX`） | ❌ 应采用 run 记录而非文本 |
| M-05 | 强制 `auto_update: false` 可禁用自更新且无副作用 | `main.py:350-354`：`first_run()` 读 `auto_update` 判断首次免责声明确认；false → `sys.exit(3)`。**禁用自更新只能改 `check_update: false`** | ❌ **致命交互** |
| M-06 | `pause_after_success`/`exit_after_failure` 默认值 | `assets/config/config.example.yaml`：`pause_after_success: true`、`exit_after_failure: false` → 默认会停在控制台等待按键 | ✅（文档已识别，需强制改写） |
| M-07 | 打包 spec 中定义 `name='m7a'`（`upstream_audit.md:97`） | 仓库内 `March7th Assistant.spec` 的 `name='March7th Assistant'`；`m7a.exe` 的来源（独立 spec 还是构建期复制改名）需用发行包验证 | ❓ |
| M-08 | 文档称"无遥测"（`license_and_risk.md` 风险章节） | `config.example.yaml:710` `telemetry_enable: true`（默认开）；`main.py:305-311` 初始化并上报 `invoker`、任务开始/完成/错误 | ❌（对 Vivian 自身可成立，但**未披露上游遥测**，隐私告知不完整） |
| M-09 | 运行互斥仅有 `active_run` | `main.py:336-347` `ensure_exclusive_run()`：lock busy → exit 4；lock **unavailable → 警告后照常执行**（互斥可能失效） | ⚠️ |
| M-10 | 可用 `full.7z` 覆盖升级 | `SKILL.md` 明确"不要用完整包覆盖已有安装，用 `app_update`"；`upstream_audit.md` 未收录该警告 | ❌ 遗漏 |

### 2.4 ZZZ-OneDragon 接口与行为

| # | 待审文档断言 | 实测（v2.6.0） | 判定 |
|---|---|---|---|
| Z-01 | 入口 `OneDragon-RuntimeLauncher.exe`，参数 `-o -c [-i 1,2]` | `src/one_dragon/launcher/launcher_base.py:15-31` 与 `exe_launcher.py` 证实；另支持 `-s/--shutdown [n]`、`--hibernate`、`--sleep`、`-h/-v` | ✅ |
| Z-02 | 成功 = ExitCode 0 + 游戏关闭 + 无 Traceback | `application_launcher.py:82-113` 从不检查 `run_result.success`、不设退出码；失败（非异常）也返回 0；未提权时 `exe_launcher.py:50-61` 父进程直接 `sys.exit(0)` | ❌ **系统性假成功** |
| Z-03 | 版本可冻结为 v2.6.0 | `runtime_launcher.py:24-80` 每次启动 `_sync_code()` 做 git fetch/checkout；`env_config.py:223-228` `auto_update_code` 默认 true | ❌ |
| Z-04 | `config/env.yml` 中 `auto_update: false` 关闭更新（`architecture_proposal.md:102`） | 键名是 `auto_update_code`；且 `force_update` 默认 true，会"直接丢弃现有改动" | ❌ 键名/语义均错 |
| Z-05 | 配置位于 `config/env.yml`、`game.yml`、`one_dragon.yml`、`zzz_one_dragon.yml` 与 `config/[0-9]*/` | `yaml_config.py::_get_yaml_file_paths()`：`sub_dir=['config']`，有实例号时追加 `%02d` → 实例配置实际在 `config/01/*.yml`；全局为 `config/env.yml`。文档把两者混列，写错位置会静默无效 | ⚠️ |
| Z-06 | "强制全英文路径"是全局硬校验 | `src/one_dragon_qt/app/directory_picker.py:257` 仅在**用户选择目录**时警告并清空（游戏/安装目录）；`cv2_utils.py` 专门做了非 ASCII 兼容 | ⚠️ 表述过强 |
| Z-07 | MCP 后端 `127.0.0.1:23001/mcp` 可用 | `src/zzz_od/backend/entry/server.py` 存在；但 `docs/develop/zzz/backend/entry.md` 要求 `uv run --env-file .env`、dev 组 `mcp>=2,<3`、`uvicorn` → **发行包内是否可用未验证** | ❓ |
| Z-08 | 日志格式 `[HH:mm:ss.fff] [filename.py line] [LEVEL]: message` | `src/one_dragon/utils/log_utils.py` 证实；默认文件 `.log/log.txt`（按时间轮转），另有 `.debug/zzz_od_mcp/main_server.log` | ✅ |
| Z-09 | SignPath 签名 | release 无签名资产；需对 exe 做 `Get-AuthenticodeSignature` 实测 | ❓ |
| Z-10 | 单实例："同一安装目录不可并发" | `backend/mcp/service_app.py` 单跑道（并发返回"已有运行在进行中"），但这是后端语义；GUI/Launcher 层的互斥未验证 | ⚠️ |

### 2.5 许可证与合规

| # | 断言 | 核查 | 判定 |
|---|---|---|---|
| L-01 | 三上游均 GPL-3.0 | GitHub API `license.spdx_id` 均为 GPL-3.0 | ✅ |
| L-02 | WPF-UI 为 MIT | 属实；但引用来源是 BetterGI 的 `AGENTS.md`，应改为 WPF-UI 仓库 LICENSE | ⚠️ |
| L-03 | PySide6 为 LGPLv3、PySide6-Fluent-Widgets 为 GPL | 属实 | ✅ |
| L-04 | 模式 A（独立进程调用）不构成衍生作品，故可不开源 Vivian | 工程上通常可接受，但"经命令行/本地 socket 亲密通信"在 FSF 解释下存在争议；这是**法律判断**，不能由架构文档定论 | ⚠️ 需法律意见 |
| L-05 | 捆绑再分发时提供 Corresponding Source 即可 | 还需：不得附加限制、保留通知、提供安装信息（GPLv3 §6）、ZZZ 的 Python 运行时与全部依赖的 SBOM | ⚠️ 不完整 |
| L-06 | M7A 含 `fps_unlocker`，硬禁用即可 | 若分发包内仍含该二进制，仍有分发与 ToS 风险；应做**构建期剔除**并记录修改 | ⚠️ |

---

## 3. 问题清单

严重度定义：
- **Critical**：直接导致核心承诺失效、用户数据损坏、或安全边界被突破；必须在编码前重设计。
- **High**：会造成高频误判/中断/支持成本，或安全上有明确攻击路径；必须在首个可发布版本前修复。
- **Medium**：影响可维护性、可观测性或边缘场景；进入 backlog 并有验证手段。
- **Low**：表述、一致性、体验细节。

### 3.1 Critical

#### C-1 完成判定链路会系统性地把失败判成成功

**证据**
- ZZZ：`application_launcher.py:82-113` 忽略 `run_result`；`exe_launcher.py:50-61` 提权路径父进程 0 退出。
- BGI：传错配置名静默回退（`OneDragonFlowViewModel.cs:544-569`）；退出码 0 不区分内部失败（文档自己也承认，`upstream_audit.md:66`）。
- M7A：文档采用"退出码 0 + 文本含'已完成'"，而程序已提供 `logs/runs/<run_id>.json` 的 `status` 字段。

**影响**：用户看到"全部成功"，实际当天日常没做；这是统一管理器最不可接受的失败模式，且会污染历史统计与后续调度决策（例如"今日已完成"被错误置位后不再重跑）。

**建议**
1. 完成判定改为**三通道加权 + "证据优先"**：优先读结构化产物（M7A run JSON、ZZZ 后端 `/game/status` 或 run 上下文、BGI 日志中的任务计数 + 进程存活），文本匹配只作兜底。
2. 引入**否定证据优先**规则：出现 `UnHandle Exception`/`Traceback`/`[ERROR]`/`任务被取消` 任一，即判失败，无论退出码。
3. 对 ZZZ 强制"提权后子进程"模型：父进程 0 退出不得作为结果，必须以子进程/后端状态为准。
4. 每个适配器输出统一 `EngineOutcome { Success, Failed, Indeterminate, Cancelled }`；`Indeterminate` 必须让 UI 显式询问用户，禁止默认成功。

**验收**：注入式测试中，构造"退出码 0 + 日志含错误标志"的样本，判定必须为 Failed；构造"父进程 0 + 子进程失败"的 ZZZ 样本，必须为 Failed。

#### C-2 版本冻结与兼容门禁在 ZZZ 上不成立

**证据**：`runtime_launcher.py:24-80` 每次启动同步代码；`env_config.py:207-228` `force_update=true`、`auto_update_code=true` 默认值；`architecture_proposal.md:102` 的键名 `auto_update` 根本不存在。

**影响**：Vivian 记录的"引擎版本 v2.6.0"与实际执行代码不一致；回滚点、Smoke Test 结论、日志正则全部可能在用户机器上漂移。更糟的是 `force_update=true` 会**丢弃本地改动**——Vivian 若为兼容性给 ZZZ 打过补丁，会被静默还原。

**建议**：V1.0 对 ZZZ 采取"**启动前强制写 `config/env.yml`：`auto_update_code: false`、`force_update: false`**，并在启动后校验 `src` 目录 git HEAD == 记录值；不一致则拒绝启动并提示修复"。同时把"上游自更新"列为兼容性门禁的一等输入。

**验收**：断网/联网两种情况下启动 ZZZ 各 3 次，`git rev-parse HEAD` 必须不变；篡改 `src` 后启动必须被拦截。

#### C-3 配置补丁存在写入竞态，且 M7A 的补丁会直接锁死 CLI

**证据**
- BGI `ConfigService.cs:60` 任意变更即 Save；`App.xaml.cs:120` 启动也 Save → 引擎运行时外部改 `User/config.json` 会被覆盖，或覆盖引擎刚写入的值。
- M7A `main.py:350-354`：`auto_update=false` ⇒ 退出码 3。
- ZZZ 实例配置在 `config/<idx>/`，写 `config/` 根无效。

**影响**：轻则补丁不生效（用户以为已关闭"完成后暂停"，实际仍阻塞 40 分钟），重则引擎反复写回导致配置半损坏；M7A 则是**直接不可用**。

**建议**
1. 所有配置写入必须满足"**引擎进程已退出 + 文件未被占用 + 版本指纹一致**"三条件；写入前对 `User/config.json`、`config.yaml`、`env.yml` 做 SHA-256 快照，写入后回读校验。
2. 写前获取**跨进程命名互斥**（`Global\Vivian.Engine.<Game>.ConfigLock`），并让适配器在引擎启动后进入"只读期"。
3. M7A 只改 `check_update: false`、`pause_after_success: false`、`exit_after_failure: true`；**禁止改 `auto_update`**；并在 `--status` 里把 `first_run_completed` 仅用于引导，不作为成功依据。
4. 提供"配置补丁清单"（键、期望值、写入时机、回滚值），启动前校验实际值，不符则拒绝启动而非静默继续。

**验收**：并发写测试（引擎写 + Vivian 写）100 次，最终配置必须为 Vivian 期望值且 JSON/YAML 可解析；M7A 补丁后 `--status` 与一次 dry-run 必须成功。

#### C-4 提权模型导致父子进程关系断裂，JobObject 与退出码均失效

**证据**：M7A `m7a.exe` 运行任务时经 UAC 拉起 `March7th Assistant.exe` 并回传退出码（文档已述，但未设计父进程丢失场景）；ZZZ 用 `pyuac.runAsAdmin(wait=False)` 后父进程退出；BGI 非管理员时 `runas` 自杀重启。三者都不是"Vivian 的直接子进程"。

**影响**：`ProcessJobGuard` 假设"杀掉进程树即停任务"不成立；应用崩溃后引擎成为孤儿继续操作游戏；用户点"停止"可能只杀掉壳进程。

**建议**
1. 放弃"进程树"作为唯一真相，改为**引擎身份登记**：启动前记录 `(exe 路径, PID, 启动时间, 命令行哈希)`，停止时按"同名进程 + 启动时间晚于本次"匹配并逐个确认。
2. 使用 **Job Object + `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`**，但必须由**提权后的 Vivian 主进程**创建作业并在启动引擎时分配；对 `runas`/`pyuac` 派生的进程，需以"轮询 + 命令行指纹"补偿。
3. 增加"**停止确认**"：发停止信号后 10 s 检查游戏进程与引擎进程，未退则弹 UI 让用户选择强杀/继续等待。
4. 明确 Vivian 自身必须以管理员运行（与三引擎一致），否则禁止启动任务，而不是"尝试提权"。

**验收**：在 Vivian 被任务管理器强杀后 30 s 内，引擎与游戏进程必须被作业对象回收；三种引擎各测一次。

#### C-5 缺少跨进程/跨实例的全局互斥

**证据**：待审方案的串行只发生在 Vivian 进程内的状态机；`architecture_proposal.md:92` 的"单实例预清理"只针对 BetterGI 残留；未定义"两个 Vivian 实例""Vivian + 用户手动开的引擎"场景。

**影响**：两套调度同时 `SendInput`、同时抢前台窗口、同时改同一份配置 → 任务错乱、配置损坏、账号风险。

**建议**：引入 `Global\Vivian.Scheduler` 命名互斥（单实例 Vivian）+ 每游戏 `Global\Vivian.Engine.<Game>` 互斥（含手动启动检测）；获取失败时 UI 明确显示"另一实例/引擎正在运行"并只读展示，不提供"强制开始"。

**验收**：双开 Vivian、Vivian + 手动开 BetterGI，均必须被拒绝并给出可操作提示。

### 3.2 High

#### H-1 三引擎的窗口/输入争用被严重低估

三个引擎都依赖"前台窗口 + 全局输入"：BGI 用 Fischless.WindowsInput/GameCapture，M7A 用 `pyautogui`/后台截图，ZZZ 用 `pynput`/`vgamepad`/ViGEmBus。串行执行只解决"同时运行"，不解决：
- 引擎启动后**自行拉起游戏并抢前台**，Vivian 的"焦点守卫"无法阻止引擎内部行为；
- 引擎残留（崩溃、用户手动关闭 UI 但后台仍在跑）时，下一个引擎的 `SendInput` 会打到最后前台窗口；
- 全局热键冲突：M7A 默认 `f10` 停止 / `f8` 暂停，BGI 有自己的快捷键，Vivian 计划 `Ctrl+Alt+F12`；
- ZZZ 的 ViGEmBus 虚拟手柄与真实手柄/其他工具冲突，且驱动安装可能要求重启。

**建议**：串行之外增加"**引擎静默屏障**"：下一个任务开始前，必须满足 `引擎进程=0` 且 `游戏进程=0` 且 `前台窗口不属于任何引擎`，并做 3 s 稳定性复查；热键在启动前做 `RegisterHotKey` 冲突检测，冲突则降级为托盘菜单。

#### H-2 日志解析器按当前版本写死，缺少版本化与降级

BGI 的 Serilog 模板是 3 行一条、带 `[BgiInstance]` 前缀、按天滚动且 `shared:true`；M7A 的 `--json` 输出中文为 `\uXXXX`；ZZZ 因为会自更新，日志格式随时可能变。待审方案的解析规则没有"版本 → 规则集"映射，也没有"解析失败时降级为进程 + 截图"的策略。

**建议**：解析器做成**版本化规则包**（`rules/bgi/0.66.json`），每条规则带样本与单测；未知版本进入"宽松模式"（只认进程退出与错误关键字），并在 UI 标注"该引擎版本未经完整验证"。

#### H-3 超时阈值缺乏依据，会误杀长任务

文档给"原神 45 min / 星铁 40 min / 绝区零 30 min / 锄大地 180 min、日志静默 8 min"。上游 `SKILL.md` 明确 `main` 常见 10~40 分钟，`universe`/`fight` 可能数小时。40 min 硬超时会杀掉合法的模拟宇宙/差分宇宙长跑。

**建议**：超时按"**任务类型 + 历史 P95**"动态计算，首次运行使用保守上限（如 180 min），并在 UI 显示"预计/已运行"；静默超时只在"无日志 + 无 CPU + 无窗口变化"三条件同时满足时触发。

#### H-4 更新链路的完整性保证不足（无签名 → 管理员权限执行）

文档承认"下载后校验 SHA-256"，但 SHA-256 来自**同一份可被篡改的元数据**（或第三方镜像），且解压后立即以管理员身份执行 `m7a.exe --status` 探针。这是典型的"下载即执行"供应链路径：镜像/CNB/代理被入侵或 DNS 劫持 → 任意代码以管理员运行。

**建议**：最低要求——① 只从 GitHub Releases 官方域下载，`sha256` 与**两个独立来源**（API digest + release notes 内发布的校验值）比对；② 引入项目自维护的**公钥签名清单**（Vivian 发布时签名 `manifest.json`，客户端内置公钥），未签名版本一律 `Unverified`；③ 解压与探针在**低权限沙箱进程**中执行，确认通过后才允许提权运行；④ 禁用"下载后自动执行"，改为用户确认。

#### H-5 解压路径穿越与符号链接未处理

7z/zip 解压如果直接调用 `7za x` 或 `ZipFile.ExtractToDirectory`，对 `..\`、绝对路径、NTFS ADS、符号链接的处理不一致；ZZZ 包内含 `.runtime` 与 `src`，条目数上万，人工审计不现实。

**建议**：解压前读取归档条目清单，拒绝任何绝对路径、`..`、盘符、符号链接/硬链接条目；解压到 `staging` 后做"根目录一致性"检查（期望顶层目录集合）；对 `.runtime` 中的可执行文件做 PE 头与大小上限校验。

#### H-6 命令行参数注入面

BGI：`BetterGI.exe startOneDragon <配置名>`，配置名来自用户输入/配置文件，若含 `..\` 或引号可能越界读写 `User\OneDragon\`；ZZZ：`-i 1,2` 只应接受数字白名单；M7A：`--workflow-name` 来自用户输入。

**建议**：所有拼进命令行的用户输入走"白名单字符 + 长度上限 + 与已知枚举比对"；配置名只允许 `^[\w\-\u4e00-\u9fa5]{1,64}$`，禁止路径分隔符；启动前解析出最终路径并断言其在引擎根目录下（`Path.GetFullPath` 前缀校验，注意大小写与尾随分隔符）。

#### H-7 Vivian 自身更新在管理员权限下是提权面

文档只设计了引擎更新，未定义 Vivian 自身更新。若 Vivian 以 `RunLevel=Highest` 运行且更新器把新版本写入用户可写目录，任何本地用户都能替换二进制 → 管理员代码执行（LPE）。

**建议**：Vivian 自身更新必须"签名校验 + 安装目录 ACL 仅 Administrators/SYSTEM 可写 + 更新器以低权限下载、提权仅用于替换"；V1.0 可先不做自动更新，改为"提示用户手动下载"。

#### H-8 GPL 义务在"捆绑/修改"模式下未闭环

模式 A 的边界需要法律意见；模式 B 需提供 Corresponding Source 与安装信息；ZZZ 的 WithRuntime 包含 Python 与大量依赖，M7A 完整包包含 `fps_unlocker`。待审文档没有 SBOM、没有源码获取路径、没有"修改过的文件"清单。

**建议**：发布前生成 SBOM（组件、版本、许可证、源码 URL）；若捆绑，提供 `SOURCE_OFFER.md` 与构建脚本；剔除 `fps_unlocker` 并在 `NOTICE` 中记录修改；不使用上游 LOGO/名称暗示官方。

#### H-9 无崩溃恢复与幂等设计

SQLite 有 `TaskBatchRuns`/`EngineTaskRuns` 表，但没有 WAL、没有事务边界、没有"启动时对账"（上次进程被杀时处于 Running 的任务如何收尾）。用户重启后可能看到"运行中"僵尸记录，或重复执行已完成任务。

**建议**：`PRAGMA journal_mode=WAL; synchronous=FULL;`；每次状态转换写"意图 + 结果"两条记录（write-ahead intent）；启动时执行 `RecoverOrphans()`：把 `Running/StartingEngine` 标为 `Interrupted`，并让用户选择"重跑/跳过"。

#### H-10 关机/注销/锁屏无处理

未处理 `WM_QUERYENDSESSION`、`SessionEnding`；`LogonType=InteractiveToken` 在锁屏/远程桌面断开时的行为未验证；关机时引擎可能被强杀，留下半完成的游戏状态与损坏的配置。

**建议**：监听 `SystemEvents.SessionEnding`，尽力 `StopGracefullyAsync` 并在 5 s 内落盘"中断"状态；恢复后提示"上次被系统中断"；任务计划程序的自启改为可选，且默认不用 `Highest`（改为启动后按需提权）。

### 3.3 Medium

| # | 问题 | 说明与建议 |
|---|---|---|
| M-1 | WPF/.NET 选型理由错误但结论可保留 | 上游 0.66.0 是 .NET 8，不是 .NET 10；"同栈"论据不成立。建议 **.NET 8 LTS**（与 BGI 一致、WPF-UI 4.x 验证充分），或若坚持 .NET 10 需自证 WPF-UI/单文件发布/裁剪兼容性。真正决定成败的是进程编排与状态持久化，不是 UI 框架。 |
| M-2 | SQLite 并发与增长 | 单写者 + UI 线程易锁库；需 `busy_timeout`、后台写入队列、日志归档保留策略（如 90 天/2 GB）与 `VACUUM` 计划。 |
| M-3 | UI 范围过大 | 六页面 + 托盘动态图标 + 拖拽排序 + 全屏错误快照 + 诊断包，V1 难以全部达标；建议按第 6.4 节删减。 |
| M-4 | 经验参数缺依据 | 10 s 冷却、8 min 静默、15 s 等待、45/40/30 min 均无实测来源；应改为可配置 + 有默认值与依据说明。 |
| M-5 | 可观测性不足 | 仅文件尾随 + SQLite；建议引擎侧 wrapper 输出 JSONL 事件（`task_start/task_end/error`），Vivian 只消费事件流。 |
| M-6 | 任务计划程序提权细节未验证 | `InteractiveToken + Highest` 在微软账号/无密码/远程桌面下的行为需实测；`schtasks` 创建需管理员。 |
| M-7 | 分辨率/DPI 冲突 | BGI 会改游戏分辨率，ZZZ/M7A 截图依赖固定分辨率；串行时也需在任务前后校验分辨率并恢复。 |
| M-8 | 磁盘空间与耗时 | 三引擎约 1.2 GB，双份备份约 2.2 GB，加上解压临时空间；444 MB 7z 解压耗时与磁盘占用需在安装前预检（建议 ≥ 15 GB 可用）。 |

### 3.4 Low

| # | 问题 |
|---|---|
| L-1 | 文档内部路径不一致（自称 `docs/...`，实际在根目录）；引用使用 `gitback.org` 镜像，长期可用性存疑。 |
| L-2 | 颜色/尺寸硬编码，建议改为资源字典 + 设计 token。 |
| L-3 | 无 i18n 与无障碍（高 DPI、键盘导航、屏幕阅读器）规划。 |
| L-4 | 无崩溃转储（WER LocalDumps）与诊断包版本号。 |
| L-5 | 版本号策略未定义（Vivian 语义化版本 + 引擎兼容矩阵版本）。 |

---

## 4. 专项分析

### 4.1 WPF/.NET 架构是否合理

**结论：方向合理，理由需修正，边界需补。**

合理的部分：
- 进程外适配器（`IGameAutomationEngine`）与"引擎进程"解耦，是正确的隔离方式；三引擎语言/运行时不同（.NET 8 / Python 3.12 / Python 3.11），进程外是唯一稳妥选择。
- WPF 对 Win32（`SetWinEventHook`、`RegisterHotKey`、Job Object、`SendInput` 检测）的亲和性确实优于 WinUI 3（MSIX/打包/提权限制）与 Tauri（WebView2 常驻内存与 GPU 争用）。
- 单文件/自包含发布 + 托盘常驻，符合"后台调度器"形态。

需修正的部分：
1. **"与 BetterGI 同栈 .NET 10"是事实错误**（0.66.0 = net8.0-windows10.0.22621.0）。这不影响"选 C#"的结论，但影响运行时前置条件（用户需装 .NET 8 Desktop Runtime，或自包含发布增加 ~150 MB）。
2. WPF-UI 4.x + .NET 10 的组合未在你方文档中给出验证；建议以 .NET 8 为基线，.NET 10 作为后续升级项。
3. 架构缺少三块关键设计：**权限模型**（谁提权、何时提权、子进程继承）、**持久化状态机**（写前意图 + 启动对账）、**引擎身份登记**（替代进程树）。这三块比 UI 框架重要一个数量级。

### 4.2 三引擎是否争用窗口、输入、权限

| 资源 | 争用点 | 严重度 | 结论 |
|---|---|---|---|
| 前台窗口 | 三引擎都会 `SetForegroundWindow`/激活游戏；BGI 启动即拉游戏 | 高 | 串行可缓解，但需"引擎完全退出 + 游戏退出"屏障；Vivian 的焦点守卫管不到引擎内部 |
| 键盘/鼠标注入 | `SendInput` 是全局的；M7A 用 pyautogui/pynput；ZZZ 用 pynput | **高** | 任一残留引擎都会让注入打到错误窗口；必须互斥 + 前台校验 + 注入前窗口标题/进程校验 |
| 虚拟手柄 | ZZZ 用 ViGEmBus/vgamepad | 中 | 与真实手柄、其他映射工具冲突；驱动安装需重启，V1 应检测而非自动安装 |
| 权限 | 三者都要求管理员；ZZZ/M7A 会二次提权 | **高** | 必须"Vivian 先提权，子进程继承"，否则 UAC 弹窗不可见导致挂起；需实测三引擎在已提权父进程下的行为 |
| 单实例 | BGI EventWaitHandle(65535)；M7A active.json（unavailable 时放行）；ZZZ 后端单跑道 | 高 | 三者语义不同，Vivian 必须自己维护全局互斥，不能依赖上游 |
| GPU/捕获 | BGI GameCapture、ZZZ mss、M7A 后台截图 | 中 | 同时运行会互相遮挡/黑屏；串行 + 独占全屏/无边框设置需统一 |
| 分辨率 | BGI 自动改分辨率 | 中 | 任务后必须恢复，否则后续引擎截图错位 |

### 4.3 日志解析与完成状态可靠性

三通道可靠性评分（1~5，5 最可靠）：

| 通道 | BGI | M7A | ZZZ | 说明 |
|---|---|---|---|---|
| 进程退出码 | 2 | 4 | **1** | BGI 0 不区分内部失败；ZZZ 恒 0/父进程 0；M7A 有 0~6 语义 |
| 结构化产物 | 2 | **5** | 3 | M7A `logs/runs/<run_id>.json` 最可靠；ZZZ 有后端状态但发行包可用性未知；BGI 无 |
| 日志文本 | 4 | 2 | 3 | BGI 标志明确但多行/多实例；M7A 中文匹配脆弱；ZZZ 格式稳定但会随自更新变化 |
| 进程/窗口存活 | 3 | 3 | 3 | 只能判"是否还在跑"，不能判"是否成功" |
| 游戏内状态 | — | — | — | 无可靠 API，禁止依赖 |

**建议判定顺序**：结构化产物 → 否定证据（错误关键字）→ 退出码 → 日志成功标志 → 存活探测。任一环节给出"失败"即失败；只有全部通过才 `Success`；证据缺失为 `Indeterminate`。

### 4.4 更新与回滚

| 风险 | 现状 | 建议 |
|---|---|---|
| 版本漂移 | ZZZ 自更新、BGI 主线为 refactor 分支 | 启动前后校验 `git HEAD`/文件哈希；漂移即标 `Incompatible` |
| 配置丢失 | 6 步升级中"配置迁移合并"无事务 | 升级前整目录备份 + 配置单独快照；失败自动回滚 |
| 磁盘/时间 | 三包 ~1.2 GB，双份备份 ~2.2 GB | 预检 ≥ 15 GB；解压在 staging，成功后原子重命名 |
| 回滚 | "10 秒一键回滚"未定义边界 | 保留**上一代**目录即可；回滚 = 停引擎 → 校验旧目录完整性 → 重命名交换 → 校验版本 |
| 完整性 | 仅 sha256，无签名 | 引入 Vivian 签名清单；未签名 = Unverified，需用户确认 |
| 自更新互斥 | 未处理"升级时引擎在跑" | 升级前强制停止 + 全局互斥 + 文件占用检测 |

### 4.5 安全威胁模型（摘要）

| 威胁 | 路径 | 影响 | 缓解 |
|---|---|---|---|
| 供应链投毒 | 镜像/CNB 被入侵 → 恶意 7z → 管理员执行 | 完全控制 | 双源哈希 + 签名清单 + 低权限解压/探针 |
| 压缩包路径穿越 | `..\`、绝对路径、符号链接 | 任意文件写 | 条目白名单 + 根目录一致性检查 |
| 命令行注入 | 配置名/实例名拼进 CLI | 越界读写、执行错误配置 | 字符白名单 + `Path.GetFullPath` 前缀断言 |
| 提权（LPE） | 管理员 Vivian + 用户可写安装/更新目录 | 任意代码以管理员运行 | 安装目录 ACL、签名校验、更新器分离权限 |
| 诊断包泄露 | 全屏截图/日志含 UID、账号、Cookie | 隐私泄露 | 白名单脱敏 + 本地生成 + 不自动上传 |
| 热键劫持/冲突 | 全局热键被占用 | 停止失效 | 注册失败检测 + 托盘兜底 |
| 凭据 | Mirror酱 CDK 用 DPAPI | 换机/换用户失效 | 明确提示；V1 可先不支持 CDK |
| 磁盘耗尽 | 恶意/损坏包解压炸弹 | 拒绝服务 | 解压前检查条目数与解压后大小上限 |

### 4.6 许可证结论

- 三上游 GPL-3.0，**模式 A（独立进程、不捆绑、不分发修改版）风险最低**，与待审文档推荐一致；但"命令行调用不构成衍生"是法律判断，建议在公开发布前取得书面法律意见，并在 README 声明"本软件不包含上游代码，仅通过命令行调用"。
- 若采用模式 B/C：必须提供 Corresponding Source、构建脚本、修改清单、安装信息（GPLv3 §6），且不得对用户施加额外限制；ZZZ 的 Python 运行时与依赖需完整 SBOM。
- M7A：`fps_unlocker` 必须从分发包中剔除（不是仅配置禁用），并在 NOTICE 记录；不得伪造 `first_run_completed`（文档已正确要求）。
- 商标：不得使用 miHoYo/HoYoverse/上游 LOGO 暗示官方；应用名与图标需自持。
- 遥测：M7A 默认开启 `telemetry_enable: true`，Vivian 必须在隐私说明中披露并提供"一键关闭"（写 `telemetry_enable: false`），否则"无遥测"的表述不成立。

---

## 5. 未经验证的假设清单（按影响排序）

| # | 假设 | 若为假的影响 | 验证方式 |
|---|---|---|---|
| A-1 | ZZZ 发行包内 `git`/`src` 可被冻结，且 `auto_update_code:false` 生效 | 版本门禁失效 | Codex 实机 |
| A-2 | 三引擎在"已提权父进程"下不会再次弹 UAC | 任务卡死/不可见弹窗 | Codex 实机 |
| A-3 | BGI 传错配置名可被外部检测 | 静默跑错任务 | Codex 实机 + 日志 |
| A-4 | ZZZ 失败时后端/日志有可识别信号 | 假成功 | Codex 实机（人为中断） |
| A-5 | M7A `m7a.exe` 在发行包中存在且 `--status` 可用 | 适配器无法探测 | Codex 解包验证 |
| A-6 | ViGEmBus 已安装/可安装且不与真实手柄冲突 | ZZZ 输入失败 | 人工 |
| A-7 | 任务计划程序 `InteractiveToken + Highest` 在目标账户可用 | 开机自启失败 | Codex 实机 |
| A-8 | 7z/zip 包内无路径穿越条目 | 任意文件写 | 自动化扫描 |
| A-9 | 三引擎可共用同一套"全屏/无边框/分辨率"设置 | 截图错乱 | 人工 |
| A-10 | WPF-UI 4.x 在 .NET 8/10 单文件发布下正常 | UI 崩溃 | CI 冒烟 |

---

## 6. 正式审查结论

### 6.1 是否建议进入正式编码阶段

**不建议全面进入正式编码。建议"有条件启动、限定范围"。**

- **阻断条件**：C-1~C-5 未闭合前，禁止编写"完成判定""版本管理""配置补丁""进程编排"四类核心代码——这些正是返工成本最高的部分。
- **可并行开展**（不依赖未验证假设）：
  1. 状态机骨架与持久化（含 `RecoverOrphans`、WAL、幂等转换）；
  2. 日志/事件采集与脱敏管道；
  3. UI 壳与设计系统（不接引擎）；
  4. 测试夹具与故障注入框架（见 TEST_PLAN）；
  5. 打包/签名/更新器的**低权限**部分。
- **建议节奏**：2 周 Spike（Codex 完成 6.3 节 V1~V6）→ 架构修订评审（本报告 C/H 项闭环）→ 再进入 8~10 周 MVP 编码。

判定依据：核心功能依赖的 5 个上游行为假设中，3 个已被证伪（ZZZ 退出码、ZZZ 版本冻结、M7A `auto_update`），1 个高危存疑（BGI 配置名回退）。这已构成"影响核心功能的未验证问题"，按用户要求给出**阻断结论**。

### 6.2 必须先修复的架构缺陷

1. **完成判定模型重写**（C-1）：统一 `EngineOutcome` 四态；"否定证据优先"；ZZZ 以子进程/后端为准。
2. **版本治理重写**（C-2）：ZZZ 强制关闭 `auto_update_code`/`force_update` 并校验 HEAD；建立"引擎指纹（版本+哈希）→ 规则包"映射。
3. **配置补丁协议**（C-3）：三条件写入 + 快照/回读 + 全局配置锁；删除所有 `auto_update=false` 补丁，M7A 只改 `check_update`。
4. **权限与进程身份模型**（C-4）：Vivian 统一提权；Job Object + 引擎身份登记；停止确认流程。
5. **全局互斥**（C-5）：`Global\Vivian.Scheduler` + 每引擎互斥 + 手动启动检测。
6. **更新安全基线**（H-4/H-5/H-7）：签名清单、低权限解压/探针、条目白名单、自身更新 ACL。
7. **崩溃恢复与幂等**（H-9）：写前意图 + 启动对账 + 用户选择重跑/跳过。

### 6.3 应交给 Codex 验证的技术问题

> 每条都给出"可判定通过/失败"的标准，避免"跑一下看看"。

| ID | 验证项 | 通过标准 |
|---|---|---|
| V1 | 发行包中 `m7a.exe` 是否存在、`--status`/`--list --json` 是否可用、退出码 3/4 的触发条件 | 三项命令均有稳定输出；人为制造首次未确认 → 退出码 3 且可识别 |
| V2 | BGI 0.66.0：`startOneDragon <错误配置名>` 的实际行为；日志三行格式与 `BgiInstance`；退出码 553/65535 | 能稳定复现"静默回退"或证明不会；解析器能正确切分 |
| V3 | ZZZ WithRuntime：未提权时父进程退出码；`run_application` 失败时的退出码；`auto_update_code` 实际值；`config/<idx>` 结构 | 构造失败任务，证明退出码为 0 且需靠其他信号识别 |
| V4 | ZZZ 23001 后端在发行包内是否可用（`uv`、`.env`、依赖） | 能启动并返回 `/health`、`/game/status` |
| V5 | 三引擎残留时的窗口/输入争用；ViGEmBus 与真实手柄共存 | 能复现"注入打错窗口"，并验证屏障可消除 |
| V6 | 提权继承链：Vivian（管理员）→ 三引擎，是否二次弹 UAC；M7A `runas` 行为 | 全程无不可见 UAC；子进程可被作业对象回收 |
| V7 | 7z/zip 路径穿越与符号链接样本 | 扫描器能拒绝全部恶意条目 |
| V8 | ZZZ exe 的 Authenticode 签名；BGI/M7A 是否存在 GPG 签名资产 | 给出签名主体与校验结果 |
| V9 | M7A 遥测默认值与关闭方式；BGI 是否有上报 | `telemetry_enable:false` 后无出站请求（抓包） |
| V10 | 任务计划程序 `InteractiveToken+Highest`：微软账号/无密码/远程桌面 | 三种账户形态均可登录自启 |
| V11 | WPF-UI 4.x 在 .NET 8/10 下单文件发布与高 DPI | 启动/托盘/多屏正常 |
| V12 | 444 MB 7z 解压耗时与峰值磁盘占用 | 在目标机 ≤ 5 min、峰值 ≤ 3 GB |

### 6.4 V1.0 应删减或推迟的功能

**删除（不做）**
- 跨引擎并行执行（与三引擎的全局输入模型根本冲突）；
- 任何内存读写/Hook/注入/封包相关能力（合规红线）；
- 诊断包自动上传/遥测；
- 自动安装 ViGEmBus 等驱动；
- 自动修复游戏客户端/资源。

**推迟到 V1.1+**
- 多账号/多实例（`-i 1,2`）——配置隔离与互斥复杂度高；
- Mirror酱 CDK / CNB 镜像源——先只支持 GitHub 官方源；
- Vivian 自身自动更新——先"检查 + 提示 + 手动下载"；
- 桌面分身/ChildSession、WebView/MCP 集成；
- 托盘动态图标/动画、拖拽排序、历史趋势图；
- `RunLevel=Highest` 开机静默提权——先提供"手动启动 + 可选计划任务"；
- "10 秒一键回滚"降级为"保留上一代 + 手动回滚"，V1.1 再做自动。

**V1.0 必须保留的最小闭环**
串行队列 + 三适配器 + 四态判定 + 配置补丁（受控）+ 失败截图/日志 + 手动更新与回滚 + 单实例互斥 + 崩溃恢复。

### 6.5 发布前必须关闭的阻断项

| 阻断项 | 对应问题 | 关闭标准 |
|---|---|---|
| 假成功 | C-1 | 注入测试 0 例误判；ZZZ 失败样本 100% 识别 |
| 版本漂移 | C-2 | 连续 3 次启动 HEAD 不变；篡改被拦截 |
| 配置损坏/锁死 | C-3 | 并发写 100 次无损坏；M7A 补丁后可运行 |
| 孤儿进程/无法停止 | C-4 | 强杀 Vivian 后 30 s 内引擎与游戏退出 |
| 双实例/手动引擎冲突 | C-5 | 双开被拒绝并提示 |
| 供应链 RCE | H-4/H-5 | 恶意包 100% 拒绝；未签名包需确认 |
| 回滚失败 | 4.4 | 回滚后引擎可启动、配置完整 |
| 许可不合规 | H-8 | SBOM + NOTICE + 源码获取路径齐全 |

---

## 附录 A：证据索引

| 引擎 | 版本 | 本地路径 | 关键文件 |
|---|---|---|---|
| BetterGI | 0.66.0 | `C:\Users\胡桃\AppData\Local\Temp\vivian-audit\bgi\` | `BetterGenshinImpact/Helpers/CommandLineOptions.cs`、`Helpers/RuntimeHelper.cs`、`Service/ConfigService.cs`、`App.xaml.cs`、`ViewModel/Pages/OneDragonFlowViewModel.cs`、`BetterGenshinImpact.csproj` |
| March7thAssistant | v2026.10.31 | `…\vivian-audit\m7a\` | `main.py`、`utils/cli_entry.py`、`utils/runs.py`、`assets/config/config.example.yaml`、`assets/agent/skills/march7th-assistant/SKILL.md`、`March7th Assistant.spec` |
| ZZZ-OneDragon | v2.6.0 | `…\vivian-audit\zzz\` | `src/one_dragon/launcher/exe_launcher.py`、`launcher/application_launcher.py`、`launcher/runtime_launcher.py`、`envs/env_config.py`、`base/config/yaml_config.py`、`utils/log_utils.py`、`zzz_od/backend/entry/server.py` |

## 附录 B：可复现命令

```bash
# 版本元数据（任选其一）
curl -s https://api.github.com/repos/babalae/better-genshin-impact/releases/latest
curl -s https://api.github.com/repos/moesnow/March7thAssistant/releases/latest
curl -s https://api.github.com/repos/OneDragon-Anything/ZenlessZoneZero-OneDragon/releases/latest

# 关键代码位置
grep -n "startOneDragon\|Contains(\"start\")" BetterGenshinImpact/Helpers/CommandLineOptions.cs
grep -n "0xFFFF\|runas" BetterGenshinImpact/Helpers/RuntimeHelper.cs
grep -n "OnAnyChangedAction" BetterGenshinImpact/Service/ConfigService.cs
grep -n "EXIT_FIRST_RUN_REQUIRED\|def first_run" main.py
grep -n "auto_update_code\|force_update" src/one_dragon/envs/env_config.py
grep -n "run_result" src/one_dragon/launcher/application_launcher.py
grep -n "runAsAdmin" src/one_dragon/launcher/exe_launcher.py
```

## 附录 C：审查限制

1. 未运行任何游戏客户端与三款引擎的发行包，所有"运行时"结论均为源码推断，需 Codex 实机复核（6.3 节）。
2. `refactor/20260930` 分支仍在演进，.NET 10/ExternalAccess/Pulonia 等结论仅对该分支快照成立。
3. 许可证合规不构成法律意见。
4. 本报告不评估 UI 视觉细节，仅评估可实施性与风险。
