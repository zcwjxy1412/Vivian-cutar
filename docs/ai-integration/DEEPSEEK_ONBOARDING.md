# DeepSeek 接入说明（VIvian V1.0 独立审查与测试通道）

> **状态：本地提交，尚未推送。** 撰写环境没有任何 GitHub 写入凭据，本文件所在分支 `integration/deepseek-onboarding` 只存在于审查机的本地克隆中：**没有远程分支，也没有 Pull Request**。推送命令见 §4。
>
> 基线：`main` @ `864e8fc619562a527fa49a2ccbba7012a513a6b9`（2026-11-03T18:13:36Z，提交信息 `docs(m3): propose fail-closed privilege and Job validation matrix (#17)`）
> 撰写：DeepSeek，角色为独立代码审查 / 自动化测试 / 安全与可靠性验证（非主开发）
> 日期：2026-11-03

## 0. 结论先行

| 问题 | 结论 |
|---|---|
| 这个环境能不能读仓库、跑构建和测试？ | **能**，已复现：构建 0 警告 0 错误，166 个测试全部通过（27.3 秒）。 |
| 能不能 push、开 PR？ | **不能**，已实测失败。本机无 PAT、无 SSH 私钥、无 `gh` CLI、Git Credential Manager 未登录任何账号。 |
| 现在属于哪个方案？ | 方案 A 的**只读 + 本地执行**形态（A-readonly）。方案 A 的写入部分、方案 B、方案 C 均未启用。 |
| 需要用户做什么？ | 二选一授权写入：本机交互式登录（推荐），或创建仅限本仓库的细粒度 PAT 并存入凭据管理器。**不要把 token 发到聊天里。** |

本说明的目标不是宣称"已经接入完成"，而是把**已证实的部分**、**已证实做不到的部分**、以及**补齐条件**写清楚，避免任何一方基于错误假设排期。

## 1. 实际采用的接入方式

| 维度 | 实际情况 |
|---|---|
| 代码获取 | `git clone https://github.com/zcwjxy1412/Vivian-cutar.git`（匿名 HTTPS），以及匿名 GitHub REST API |
| 执行环境 | 用户本人的 Windows 主机，DeepSeek Harness 提供的本地 shell（bash 语义）与文件读写 |
| 写入方式 | **无**。仅本地工作树与本地分支 |
| 推送方式 | **无**。见 §3 的实测证据 |
| 与 Codex 的关系 | Codex 主开发、持有写权限；DeepSeek 只读审查、提测试、发 Issue / PR 评论 |
| 与 Gemini 的关系 | Gemini 负责上游引擎研究与界面；DeepSeek 不重复其工作，但会独立核对上游事实 |

## 2. 已验证能力（每条都有证据）

| # | 能力 | 结果 | 证据 / 复现方式 |
|---|---|---|---|
| 2.1 | 克隆仓库 | 通过 | `HEAD = 864e8fc`，工作树 89 个受版本控制文件，默认分支 `main`，公开仓库 |
| 2.2 | 读 GitHub API（匿名） | 通过 | `GET /repos/zcwjxy1412/Vivian-cutar` 返回 200；`GET .../actions/runs?branch=main` 返回 200 |
| 2.3 | 创建本地分支 | 通过 | 本分支 `integration/deepseek-onboarding` |
| 2.4 | 修改源码 / 新增文件 | 通过（本地） | 本文件 |
| 2.5 | 构建 | 通过 | `dotnet build VIvian.slnx -c Release` → **0 警告 0 错误**（仓库开启 TreatWarningsAsErrors） |
| 2.6 | 运行测试 | 通过 | `dotnet test tests/VIvian.Tests/VIvian.Tests.csproj -c Release` → **总数 166，通过 166，失败 0**，27.3 秒 |
| 2.7 | 读取 CI 结果 | 通过 | run 41691750413（main，success）；run 41691390509（`spike/m3-privilege-boundary` 的 PR 运行，success） |
| 2.8 | git commit | 通过（本地） | 见 §10 的提交记录 |
| 2.9 | git push | **失败** | 详见 §3 |
| 2.10 | 创建 PR | **失败** | 依赖 2.9；且本机无 `gh`，未登录网页会话 |

### 2.1 构建复现命令（含本机沙箱绕行）

本审查机是真实的 Windows 主机，但 DSH 沙箱拒绝写入 `C:\Users\胡桃\.dotnet`、`C:\Temp` 与 `%LOCALAPPDATA%\NuGet`，因此构建必须重定向这些目录。**这是审查机的限制，不是仓库缺陷**；仓库自带的 `scripts/Build.ps1` 在 CI 上正常工作。

```bash
export DOTNET_ROOT="<workspace>/.tools/dotnet10"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_HOME="<workspace>/.tools/dotnet-home"
export NUGET_PACKAGES="<workspace>/.tools/nuget"
export NUGET_HTTP_CACHE_PATH="<workspace>/.tools/nuget-http"
export TMP="<workspace>/.tools/tmp" TEMP="$TMP"
dotnet build VIvian.slnx -c Release -p:NuGetAudit=false
dotnet test tests/VIvian.Tests/VIvian.Tests.csproj -c Release --no-build
```

说明：

- 系统只装了 .NET **8.0.14 运行时宿主**，没有 SDK；`global.json` 固定 `10.0.403` 且 `rollForward: disable`，所以审查机另装了 SDK 10.0.403 到工作区。
- `-p:NuGetAudit=false` 只是为了绕开被拒绝写入的 NuGet 漏洞数据库缓存（NU1900）；CI 不需要这个参数。
- 未使用 `scripts/Build.ps1`：本机没有 `pwsh`。**这不代表验证过该脚本本身**，脚本由 CI 验证。

## 3. 写入能力：实测失败证据

| 检查项 | 结果 |
|---|---|
| `git config --get-all credential.helper` | `helper-selector`（GitHub 的 helper 选择器，未配置具体凭据） |
| `git credential fill`（host=github.com） | 无输出（无凭据） |
| `%USERPROFILE%\.git-credentials` | 不存在 |
| `gh` CLI | 未安装 |
| `~/.ssh` 私钥 | 不存在（只有 `known_hosts` / `known_hosts.old`） |
| `GH_TOKEN` / `GITHUB_TOKEN` 环境变量 | 未设置 |
| `git push --dry-run` | `fatal: could not read Username for 'https://github.com': terminal prompts disabled` |
| `ssh -T git@github.com` | `Connection closed by 140.82.114.3 port 22` |
| Git Credential Manager | 已安装（2.9.1），但 `cmdkey /list` 中没有 GitHub 账户 |

**因此：当前不具备任何写入能力。** 任何"已推送分支 / 已创建 PR"的说法都是假的。

## 4. 需要用户授权的项（二选一，都不需要在聊天里发送密钥）

### 路径 W1：交互式登录（推荐，凭据不经过聊天与仓库）

在本机任意终端执行其一：

```bash
# 方式 A：让 GCM 弹出浏览器完成 GitHub 登录
git credential-manager github login

# 方式 B：直接尝试推送，由 GCM 弹窗引导登录
git -C <repo> push -u origin integration/deepseek-onboarding
```

完成后凭据保存在 Windows 凭据管理器，DeepSeek 的后续调用直接复用，**不需要看到 token**。
局限：需要用户本人在机器前完成一次浏览器授权；本会话的 shell 关闭了交互式提示（terminal prompts disabled），所以这一步必须由用户自己触发，或明确同意由我启动一次会弹窗的 `git push`。

### 路径 W2：细粒度 PAT（适合长期自动化，也适合方案 C）

1. GitHub → Settings → Developer settings → **Fine-grained personal access tokens** → Generate new token。
2. **Repository access**：Only select repositories → `zcwjxy1412/Vivian-cutar`。
3. **Permissions**（最小集）：

| 权限 | 级别 | 用途 |
|---|---|---|
| Contents | Read and write | 推分支、提交 |
| Pull requests | Read and write | 开 PR、发评论 |
| Issues | Read and write | 提缺陷、回复 |
| Actions | Read | 读 CI 运行与日志 |
| Metadata | Read（自动附带） | 仓库元数据 |

4. **有效期不超过 30 天**，用完即弃。
5. 存储（不要粘贴到聊天、不要写进仓库）：

```bash
# 存入 Windows 凭据管理器（交互输入，shell 历史中不留明文）
git credential approve
# 然后逐行输入：
# protocol=https
# host=github.com
# username=zcwjxy1412
# password=<在此粘贴 PAT>
# （空行结束）
```

或者直接交给 Actions Secrets（方案 C 用），不放进 git。

### 明确**不要**授予的权限

`admin:org`、`delete_repo`、`workflows` 写权限、Secrets 读写、组织级或跨仓库权限、无过期的 token。这些都不需要。

## 5. 授权后的行为边界（自我约束）

获得写入能力后，DeepSeek 只做以下事情：

1. 推送 `integration/*`、`review/*`、`tests/*` 前缀的分支；**不推送 main**。
2. 不 force push 已有分支，不删除分支，不改 branch protection，不改 Secrets，不改 `.github/workflows/**`（除非单独 PR 并说明理由）。
3. 开 PR 但**不合并**；合并权保留给用户。
4. 审查时**不执行 PR 作者的代码**：只读 diff 与源码；测试代码在自己的分支上写。
5. 修复建议优先以 Issue / PR 评论呈现；确需改代码时，改动限制在 `tests/**` 与新文档，生产代码的修改交由 Codex 或用户确认。

## 6. 数据共享范围与安全

| 通道 | 代码是否离开本机 | 说明 |
|---|---|---|
| 方案 A（当前只读） | 否 | 只有 `git clone` 与 GitHub 匿名 API 出站 |
| 方案 B（DeepSeek API 编程代理） | **是** | 代码片段 / diff 会发送到 `https://api.deepseek.com` |
| 方案 C（Actions + DeepSeek API） | **是** | 同上，且经 GitHub Actions 中转 |

- 若仓库保持**公开**，方案 B/C 的额外暴露有限；若转为**私有**，等于把私有代码交给第三方 API 处理，必须由用户书面同意后再启用。
- 发送前应脱敏：绝对路径中的用户名、PAT、本机 temp 路径、日志中的账号信息。
- 我**不能**代替用户判断 DeepSeek 的数据保留与训练政策，请在启用前自行确认服务条款。
- 方案 C 中 `DEEPSEEK_API_KEY` 只放在 GitHub Secrets；**fork 来的 PR 默认拿不到 secret**，因此自动审查只覆盖同仓库分支，这是有意的取舍。

## 7. 自动审查通道（方案 C）：可行性与成本

### 7.1 可行性

| 条件 | 状态 |
|---|---|
| GitHub Actions 已启用 | 是（`windows.yml`、`release.yml` 均有成功运行） |
| 仓库公开时的 Actions 分钟数 | 不计费（公开仓库免费不限量） |
| 若转私有 | Free 计划 2,000 分钟/月，Windows runner 按 **2 倍** 系数计费 |
| 可用的触发方式 | `pull_request`（opened / synchronize / reopened） |
| 需要的 Secret | `DEEPSEEK_API_KEY`（Repository secret） |
| 安全设计要点 | 审查脚本从 **base 分支**检出，PR 内容只当作文本 diff 传给模型，**绝不执行 PR 里的脚本或构建产物** |

### 7.2 成本估算（按 2026-11 官方价目）

假设单 PR：diff 约 2,000 行改动 ≈ **60K 输入 token**，审查报告 **4K 输出 token**，每月 20 个 PR。

| 模型 | 单 PR 输入成本 | 单 PR 输出成本 | 单 PR 合计 | 20 PR/月 |
|---|---|---|---|---|
| `deepseek-flash` 闲时（输入 $0.15 / 输出 $0.6 每 1M） | $0.0090 | $0.0024 | **约 $0.011** | **约 $0.23** |
| `deepseek-flash` 峰时（$0.3 / $1.2） | $0.0180 | $0.0048 | 约 $0.023 | 约 $0.46 |
| `deepseek-v4-pro` 闲时（$0.66 / $1.98） | $0.0396 | $0.0079 | 约 $0.048 | 约 $0.95 |
| `deepseek-v4-pro` 峰时（$1.32 / $3.96） | $0.0792 | $0.0158 | 约 $0.095 | 约 $1.90 |

结论：**成本可以忽略**，瓶颈不是钱而是审查质量与误报率。建议先用 `deepseek-flash` 跑一周，比较其报告与人工复核的一致率，再决定是否升级到 `deepseek-v4-pro`。

### 7.3 建议的落地顺序

1. 先只做"PR 变更摘要 + 风险清单"，不做自动评论（人工看一眼再决定是否贴出）。
2. 稳定后开启自动评论，但**只评论、不阻断**（不设 required check）。
3. 最后才考虑把"Critical 级发现"做成阻断项，前提是连续 20 个 PR 零误报。

草案见 `docs/ai-integration/deepseek-review-workflow.draft.yml`（**文件名故意不在 `.github/workflows/` 下，不会被 GitHub 执行**）。

## 8. 与 Codex / Gemini 的协作边界

| 角色 | 职责 | 与 DeepSeek 的接口 |
|---|---|---|
| Codex | 主开发、持有写权限、合并 PR | DeepSeek 在 PR 上留审查意见与测试代码，不抢先改生产文件 |
| Gemini | 上游引擎研究、界面 | DeepSeek 独立核对上游事实，冲突时以源码与可复现命令为准 |
| DeepSeek | 独立审查、对抗性测试、安全与可靠性验证 | 输出 Issue / PR 评论 / `tests/**` 下的测试代码 |

## 9. 后续独立测试的开发顺序

按"先能证明现状、再证明缺陷"的顺序推进，**接入完成前不扩展 M3 业务代码**：

1. **复现 M2 评审的三个高危问题**（只读代码 + 新增测试，不改源码）：
   - `QueueScheduler` 是否存在"上一款清理未确认就启动下一款"的路径；
   - `EmergencyStop` 的时序：停止请求与写记录的先后；
   - `Judge` / `VerdictPolicy` 是否可能把无证据的运行判为成功。
2. **新建独立测试项目** `tests/VIvian.Adversarial`（与 Codex 的 `tests/VIvian.Tests` 并存，互不冲突）：
   - 进程监管：句柄继承白名单之外的泄漏、Job 逃逸、孤儿进程、管道读取线程与退出回调的竞态。
3. **崩溃恢复对账**：`RunStore` × `VIvian.CrashHost`，覆盖"进程被杀 / 断电 / 数据库写入中断"。
4. **SQLite 迁移失败回滚**：构造 v2 数据库 → 破坏 → 验证"拒绝启动而非静默重建"。
5. **覆盖率基线**：先记录，不设门禁；设门禁前先排除不可测的 WPF/平台代码。
6. **M3 适配器边界条件**：等 Codex 落地适配器、用户提供测试机后再做。
7. **上游事实回归测试**：把 `docs/evidence/EVIDENCE_INDEX.md` 固定的上游 HEAD 与 CLI 解析写成可对比的快照测试，上游改格式时能第一时间失败。

## 10. 本次提交内容

- 新增：`docs/ai-integration/DEEPSEEK_ONBOARDING.md`（本文件）
- 新增：`docs/ai-integration/deepseek-review-workflow.draft.yml`（方案 C 草案，未启用）
- **未修改任何生产代码，未修改任何既有文档。**

## 附录 A：本机环境记录

| 项 | 值 |
|---|---|
| OS | Windows（用户主机） |
| git | 2.51（Git for Windows，bash 语义 shell） |
| Git Credential Manager | 2.9.1+6760f0ef069c994aa2bb1d703fb374986ee82a3e |
| .NET SDK | 10.0.403（另装到工作区；系统仅有 8.0.14 宿主） |
| `gh` CLI | 未安装 |
| 仓库可见性 | public（`license` 字段为 `null`，尚无 LICENSE 文件） |
| 默认分支 | `main` |
| 分支保护 | **无法判定**：匿名请求 `GET /branches/main/protection` 返回 401，需管理员权限才能查询 |

## 附录 B：本次发现的一处文档不一致（低严重度）

`.github/workflows/release.yml` 顶部注释写"Windows 上构建 → **14 项测试** → 便携版发布"，但当前 `tests/VIvian.Tests` 实际有 **166** 个测试。注释已过期，建议改为"全部测试"或由脚本动态计数，避免以后有人误以为测试被裁剪。

## 11. 只读分析能力演示（3 个具体发现）

为证明"能读代码并给出可定位的结论"，而不是只会 clone 和跑测试，这里给出三个本轮实际发现的点（均为只读分析，未改任何源码）。

### 11.1 文档与测试数量不一致（低）

`.github/workflows/release.yml` 顶部注释仍写"**14 项测试**"，而 `tests/VIvian.Tests` 当前有 **166** 个测试。属于注释漂移，不影响行为，但会误导后续对"测试是否被裁剪"的判断。建议改为"全部测试"或用脚本动态计数。

### 11.2 `_running` 可能被永久置位（中低，值得上游确认）

`src/VIvian.Core/Execution/QueueScheduler.cs:66-77`：

- `:66` 先执行 `Interlocked.Exchange(ref _running, 1)`；
- 之后到 `:77` 的 `try {` 之间还有若干可能抛异常的操作，其中 `:71` 的 `ct.Register(...)` 在调用方传入**已释放的** `CancellationToken` 时会抛 `ObjectDisposedException`；
- 异常发生在 `try` 之外，`finally`（`:127-130`）不会执行，`_running` 永久为 1，后续所有 `RunAsync` 都抛"已有队列在运行"，只能重启进程恢复。

这不是安全漏洞（它是 fail-closed），但违背"可恢复"的期望。建议把 `try` 提前到 `:66` 之后，或在 `catch` 中复位标志。

### 11.3 启动前的紧急停止会被丢弃（已知限制，建议加测试固定）

`QueueScheduler.cs:59` 的 `EmergencyStop()` 只完成**当前**的 `_emergency`；`:68-69` 在 `RunAsync` 开始时用 `NewSignal()` 替换它。因此"用户点了开始、队列尚未进入 `RunAsync` 时又点停止"这一停会静默丢失。`:53-58` 的注释承认了这一点，并把责任推给调用方（用 `ct` 取消）。

这在 WPF 里对应"开始"按钮点击后、调度器尚未接管的窗口期。建议：UI 层禁用按钮，**并**加一条测试把当前行为固定下来，避免以后有人误以为已处理。

> 这三个点只是抽样演示，不是 M2 复审结论。完整的 M2 复核（评审简报里的 7 个问题）在获得 Issue/PR 权限后按 §9 的顺序逐条给出可复现证据。
