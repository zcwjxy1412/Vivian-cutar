# VIvian M3 上游版本演进、兼容矩阵与长效更新策略 (M3_VERSION_COMPATIBILITY.md)

> Gemini 原始研究。文中的版本状态、示例和建议保留原貌，实测状态与冲突见[审查记录](../reviews/M3_GEMINI_RESEARCH_VALIDATION.md)。

文档路径：docs/research/M3_VERSION_COMPATIBILITY.md

核查基准日期：2026-11-03

核心目标：针对三款高频更新的自动化引擎，建立严谨的版本溯源、演进追踪与长效兼容性门禁机制，确保 VIvian 在上游更新或破坏性重构时不发生灾难性故障。

## 1. 三引擎最新稳定版与发行包溯源表

<table style="width:100%;">
<colgroup>
<col style="width: 16%" />
<col style="width: 16%" />
<col style="width: 16%" />
<col style="width: 16%" />
<col style="width: 16%" />
<col style="width: 16%" />
</colgroup>
<tbody>
<tr class="odd">
<td><p>引擎名称</p></td>
<td><p>最新核实稳定版</p></td>
<td><p>发布日期</p></td>
<td><p>对应 Git Commit</p></td>
<td><p>官方推荐发行包名称及哈希依据</p></td>
<td><p>版本号规则 (Versioning)</p></td>
</tr>
<tr class="even">
<td><p>BetterGI (原神)</p></td>
<td><p>v0.66.0</p></td>
<td><p>2026-09-26</p></td>
<td><p>0af68c2</p></td>
<td><p>BetterGI_v0.66.0.7z (SHA256: 28d4756...)</p></td>
<td><p>SemVer 变体 (v0.MAJOR.PATCH)，大版本随原神主版本（如 7.0/7.1）演进</p></td>
</tr>
<tr class="odd">
<td><p>March7thAssistant (星铁)</p></td>
<td><p>v2026.10.31</p></td>
<td><p>2026-10-31</p></td>
<td><p>1e77d59</p></td>
<td><p>March7thAssistant_full.7z (SHA256: 1fcaa19...)</p>
<p>update.7z (SHA256: 4f9ba13...)</p></td>
<td><p>CalVer (vYYYY.M.D 或 vYYYY.M.D-beta)，高频周更或双周更</p></td>
</tr>
<tr class="even">
<td><p>ZZZ-OneDragon (绝区零)</p></td>
<td><p>v2.6.0</p></td>
<td><p>2026-10-30</p></td>
<td><p>5f2e473</p></td>
<td><p>ZenlessZoneZero-OneDragon-v2.6.0-WithRuntime-Full.zip (SHA256: 2fd5f6b...)</p></td>
<td><p>SemVer (vMAJOR.MINOR.PATCH)，每周均有正式或小版本迭代</p></td>
</tr>
</tbody>
</table>

## 2. 深入版本机制与历史演进分析

### 2.1 BetterGI（原神）

- 发布频率与重构现状：平均每月发布 1~2 个正式版本，中间穿插 Alpha 构建。
- 重大历史架构重构（破坏性风险极高）：

1.  v0.63.0（2026-08）：底层识图重构，引入多实例标识 \[{BgiInstance}\]，导致旧版日志解析正则失效。
2.  v0.65.0（2026-09）：官方发布警告“底层能力正在进行重构，版本 BUG 较多，请谨慎选择更新”。
3.  refactor/20260930 分支（2026-10 ~ 2026-11）：全面升级至 .NET 10，全面重构任务调度系统为 Pulonia，废弃旧版系统任务计划程序，统一普通任务与一条龙。配置文件正在从 User/OneDragon/\*.json 迁移为 User/Pulonia/accounts.json。

- 自动更新机制：内置 UpdateService 调用 BetterGI.update.exe，支持 Kachina 二进制增量更新。
- 固定版本能力：极高。解压 .7z 绿色包至固定文件夹即可永久运行，只要游戏本体界面未发生破坏性改动（如大版本 UI 换新），可长期固定运行。
- 对 VIvian 的潜在破坏点：未来 BetterGI 0.67+ 若将 refactor/20260930 的 Pulonia 正式合并为主分支，startOneDragon 命令行参数和 User/OneDragon/ 配置目录可能会被弃用，必须通过 VIvian 适配器防腐层平滑切换。

### 2.2 March7thAssistant（崩坏：星穹铁道）

- 发布频率与重构现状：极高频，基本保持 3~7 天一次发布。
- 近期关键变更：

1.  v2026.8.28（PR \#1176）：修复定时任务非零退出码返回逻辑，确立了以进程 ExitCode 作为完成判定的可靠基础。
2.  v2026.9.30：新增全局任务暂停/继续（F8）与命令行自定义流程调用。
3.  v2026.10.31：新增免提权 CLI 入口 m7a.exe 及随包 Skill，大幅降低外部工具调用复杂度。

- 自动更新机制：自带 March7th Updater.exe，启动时若 check_update: true 会联网检测并在控制台提示或自重启（[Issue \#529](https://github.com/moesnow/March7thAssistant/issues/529)）。
- 固定版本能力：极高。在 config.yaml 中将 check_update: false、auto_update: false 关闭后，程序完全脱机运行，绝不主动联网升级。
- 对 VIvian 的潜在破坏点：星穹铁道每 42 天大版本更新时副本名称与界面微调，旧版可能在刷副本时识别超时。VIvian 需捕获超时事件并引导用户更新至已验证的新版。

### 2.3 ZenlessZoneZero-OneDragon（绝区零）

- 发布频率与重构现状：高频周更，支持 GitHub Releases 与 CNB 镜像。
- 近期关键变更：

1.  推出集成启动器 OneDragon-RuntimeLauncher.exe，将 Python 3.11.12 与源码模型彻底内嵌，摆脱外部 Python 环境依赖。
2.  源码引入 MCP Server（:23001）提供外部控制接口。

- 自动更新机制：启动器默认会在启动时检查 GitHub/CNB 代码并尝试 git pull / 下载补丁。
- 固定版本能力：中等。必须在 config/env.yml 中设置 auto_update: false 才能切断启动器的自动代码拉取，确保版本固定。
- 对 VIvian 的潜在破坏点：config/ 目录下的 YAML 字段在重构中可能调整键名；必须建立配置快照保护。

## 3. VIvian 可执行版本兼容性矩阵设计

为了让 VIvian 能够智能化判断本地引擎是否受支持，VIvian 定义一套由云端/本地清单文件驱动的 五态兼容性决策状态机：

    {
      "SchemaVersion": "1.0",
      "Engines": {
        "Genshin_BetterGI": {
          "VerifiedRange": ">=0.60.0, <=0.66.0",
          "LatestRecommended": "0.66.0",
          "KnownBrokenVersions": ["0.63.0", "0.65.0"],
          "BreakingChangeBranches": ["refactor/20260930"],
          "MinimumGameVersion": "7.0",
          "AdapterContractVersion": "1.0"
        },
        "StarRail_March7th": {
          "VerifiedRange": ">=2026.8.28, <=2026.10.31",
          "LatestRecommended": "2026.10.31",
          "KnownBrokenVersions": ["2026.8.20"],
          "AdapterContractVersion": "1.0"
        },
        "ZZZ_OneDragon": {
          "VerifiedRange": ">=2.4.4, <=2.6.0",
          "LatestRecommended": "2.6.0",
          "KnownBrokenVersions": [],
          "RequiresLauncher": "RuntimeLauncher",
          "AdapterContractVersion": "1.0"
        }
      }
    }

### 五态判定与应对策略

<table>
<colgroup>
<col style="width: 25%" />
<col style="width: 25%" />
<col style="width: 25%" />
<col style="width: 25%" />
</colgroup>
<tbody>
<tr class="odd">
<td><p>兼容状态码</p></td>
<td><p>判定依据</p></td>
<td><p>VIvian 运行策略</p></td>
<td><p>用户界面展示</p></td>
</tr>
<tr class="even">
<td><p>Verified</p>
<p>(已验证兼容)</p></td>
<td><p>当前本地引擎版本落在 VerifiedRange 区间内，且不属于 KnownBrokenVersions</p></td>
<td><p>允许全自动加入定时与一键执行队列</p></td>
<td><p>🟢 绿色徽章：已验证稳定版</p></td>
</tr>
<tr class="odd">
<td><p>Unverified</p>
<p>(尚未验证的新版)</p></td>
<td><p>本地或检测到上游发布了高于 VerifiedRange 上限的新版本，但未列入已知损坏清单</p></td>
<td><p>默认禁止全自动执行；允许用户在单游戏卡片中勾选“允许尝鲜试跑”，试跑前自动强制创建完整配置与二进制快照</p></td>
<td><p>🟡 黄色徽章：新版本 (待兼容性验证)</p></td>
</tr>
<tr class="even">
<td><p>Broken</p>
<p>(已知严重不兼容)</p></td>
<td><p>属于 KnownBrokenVersions（如 BetterGI v0.65.0 底层重构不稳定版）</p></td>
<td><p>完全阻断调用，禁止运行，弹出红色警告并引导用户一键回滚到最近的稳定版本</p></td>
<td><p>🔴 红色徽章：已知不兼容 (禁止调用)</p></td>
</tr>
<tr class="odd">
<td><p>AdapterUpdateRequired</p>
<p>(需更新适配器)</p></td>
<td><p>上游发生了破坏性契约变更（如 BetterGI Pulonia 架构上线），AdapterContractVersion 大于本地适配器支持版本</p></td>
<td><p>阻止运行该引擎任务，提示“请更新 VIvian 核心或适配器插件以支持该引擎”</p></td>
<td><p>🟣 紫色徽章：需要更新 VIvian 适配器</p></td>
</tr>
<tr class="even">
<td><p>HoldCurrentVersion</p>
<p>(建议保持旧版)</p></td>
<td><p>游戏未更新且当前版本工作稳定，但上游最新版处于高风险测试期（如 Alpha/Beta）</p></td>
<td><p>屏蔽新版本更新弹窗，维持当前受支持版本</p></td>
<td><p>⚪ 灰色标签：锁定当前推荐版本</p></td>
</tr>
</tbody>
</table>
