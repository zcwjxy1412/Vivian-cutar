# Vivian V1.0 引擎安装与独立版本更新策略 (UPDATE_STRATEGY.md)

**文档路径**：`docs/architecture/UPDATE_STRATEGY.md`  
**核心铁律**：
1. **三引擎完全独立安装、独立更新、独立回滚**，互不耦合。
2. **严禁自动执行未经兼容性验证的新版本**：上游发布新版本后，若未通过 Vivian 兼容性清单验证或未经用户显式授权，绝不在后台静默升级并直接投入无人值守运行。
3. **全面接管并禁用上游内置自更新**：防止上游程序在凌晨挂机时弹出更新确认框或自重启导致调度链路断裂。

---

## 1. 发行包选型与目录隔离规范

### 1.1 三款上游引擎推荐下载包形态（已从 Release 与构建流验证）

| 引擎名称 | 推荐下载资产包名称 | 体积量级 | 为什么选择该包形态（对比其它安装包） | 验证来源 |
| :--- | :--- | :--- | :--- | :--- |
| **BetterGI (原神)** | `BetterGI_v{Version}.7z`<br>（如 `BetterGI_v0.66.0.7z`） | ~444 MB | 纯绿色便携压缩包，解压即用，不含安装器 UI 交互，保留 `User/` 目录即可无损升级。 | [BetterGI Releases](https://github.com/babalae/better-genshin-impact/releases) |
| **March7thAssistant (星铁)** | 首次安装：`March7thAssistant_full.7z`<br>后续更新：`update.7z` | 首次 ~452 MB<br>增量 ~156 MB | 均采用标准 LZMA2 7z 格式（见 [`2.realtime_release.yml`](https://github.com/moesnow/March7thAssistant/blob/main/.github/workflows/2.realtime_release.yml)），`update.7z` 剥离了 `3rdparty` 子目录，体积缩小 65%，适合快速更新。 | [March7th Releases](https://github.com/moesnow/March7thAssistant/releases) |
| **ZZZ-OneDragon (绝区零)** | 首次安装：`ZenlessZoneZero-OneDragon-v{Version}-WithRuntime-Full.zip`<br>后续更新：`ZenlessZoneZero-OneDragon-RuntimeLauncher.zip` | 首次 ~269 MB<br>启动器 ~198 MB | 内嵌 `.runtime`（Python 3.11.12 离线环境）+ `src/` + `models/`，解压即用，无需本机安装 Python 或 `uv`，且进程名固定为 `OneDragon-RuntimeLauncher.exe`。 | [ZZZ-OneDragon Releases](https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon/releases) |

### 1.2 强制目录结构规范
由于 `ZenlessZoneZero-OneDragon` 官方强制要求安装路径**全英文、无空格、无中文字符且路径不宜过长**（见 [快速开始文档](https://one-dragon.com/zzz/zh/quickstart.html)），Vivian 默认建议在非系统盘根目录创建统一的纯英文工作区（例如 `D:\VivianWorkspace\`）：

```text
D:\VivianWorkspace\
├── app\                        # Vivian V1.0 主程序目录
├── data\                       # Vivian SQLite 数据库、全局配置、运行归档日志
│   ├── vivian.db
│   ├── manifests\              # 兼容性验证清单缓存
│   ├── downloads\              # 离线更新包下载缓存与 SHA-256 校验区
│   └── backups\                # 三引擎配置文件与旧版本回滚快照
└── engines\                    # 三大引擎独立沙箱目录（严格纯英文无空格）
    ├── BetterGI\               # 原神引擎根目录
    │   ├── BetterGI.exe
    │   ├── User\               # [受保护] 用户配置与一条龙脚本目录
    │   └── log\                # [受保护] 运行日志目录
    ├── March7thAssistant\      # 星穹铁道引擎根目录
    │   ├── m7a.exe
    │   ├── March7th Assistant.exe
    │   ├── config.yaml         # [受保护] 用户主配置文件
    │   ├── 3rdparty\           # [受保护] Fhoe-Rail / Auto_Simulated_Universe
    │   └── logs\               # [受保护] 运行日志目录
    └── ZZZ-OneDragon\          # 绝区零引擎根目录
        ├── OneDragon-RuntimeLauncher.exe
        ├── .runtime\           # 内嵌 Python 运行时
        ├── src\                # 业务代码
        ├── models\             # [受保护] ONNX 视觉识别模型
        ├── config\             # [受保护] 全局及 01/02 多账号实例配置
        └── .log\               # [受保护] 运行日志目录
```

---

## 2. 多源下载与 SHA-256 强校验机制

1. **多渠道下载源支持**：
   - **官方免费源（默认）**：GitHub Releases API（`https://api.github.com/repos/{owner}/{repo}/releases`）及 CNB 国内镜像源（BetterGI 与 ZZZ-OneDragon 均已同步发布至 CNB Releases，见 [BetterGI README](https://github.com/babalae/better-genshin-impact/blob/main/README.md) 与 [ZZZ-OneDragon build-release.yml](https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon/blob/main/.github/workflows/build-release.yml)）。
   - **Mirror酱（MirrorChyan）高速源（可选）**：三款上游项目均已官方接入 Mirror酱（见各自 Release 页面说明）。Vivian 在设置中心提供统一的 Mirror酱 CDK 输入框（本地 DPAPI 加密存储），用户填入后可为三款引擎共享高速 CDN 增量/全量下载。
2. **强制哈希与签名校验**：
   - 三款项目在 GitHub Release Assets 中均公开了官方 `sha256` 摘要（此外 ZZZ-OneDragon 具备 SignPath Foundation 代码签名，March7thAssistant 与 BetterGI 具备 GitHub Actions GPG 签名）。
   - 下载完成后，Vivian 必须在内存中计算文件 `SHA-256` 并与 Release 元数据比对，不一致则立即丢弃并报错，严禁解压损坏或被篡改的归档包。

---

## 3. 版本兼容性验证门禁（Compatibility Gatekeeper）

为彻底解决 **“上游升级导致 CLI 接口变化或新版本引入致命 Bug（如 BetterGI v0.63.0 / v0.65.0 官方均曾发布 Caution 警告底层重构不稳定）”** 的问题，Vivian 引入三态版本兼容门禁：

### 3.1 版本兼容状态分级
- **🟢 `Verified`（已验证兼容）**：该上游版本已通过 Vivian 兼容性清单（`compatibility_matrix.json`）及本地接口自检（Smoke Test），允许一键更新并投入定时无人值守运行。
- **🟡 `Unverified`（新发布尚未验证）**：GitHub Release 刚发布的新版本，尚未命中 Vivian 兼容清单，或本地 Smoke Test 存在警告。
  - **策略**：**禁止自动更新，禁止在无人值守计划任务中自动切换至此版本**。UI 显示“发现新版本（待兼容性验证）”，若用户因游戏大版本更新（如原神 7.1、星铁新版本）急需手动尝鲜，需在弹窗中勾选“我已知晓该版本尚未经过 Vivian 兼容性验证，更新前将自动创建完整回滚快照”。
- **🔴 `Incompatible / Broken`（已知不兼容或上游标记严重 Bug）**：已知破坏 CLI 参数（或正在进行不兼容架构迁移，如 BetterGI `Pulonia` 预览分支改变配置文件结构）或上游 Release 明确标注高风险重构的版本，Vivian 拦截更新并给出红色警告说明。

### 3.2 本地契约自检（Post-Extract Smoke Test）
在解压新版本至临时暂存目录 `{EngineRoot}_staging\` 后、正式替换原目录前，Vivian 自动执行 4 项静态与轻量契约探针：
1. **入口可执行文件探针**：检查 `BetterGI.exe` / `m7a.exe` & `March7th Assistant.exe` / `OneDragon-RuntimeLauncher.exe` 是否存在且 PE 头合法。
2. **CLI 帮助/状态探针（免提权快速探针）**：
   - 对 March7thAssistant 执行 `m7a.exe --status` 与 `m7a.exe --list --json`，校验返回 JSON 是否包含 `first_run_completed` 等必需字段。
   - 对 ZZZ-OneDragon 执行 `OneDragon-RuntimeLauncher.exe -v` 与 `-h`，校验输出是否包含 `-o, --onedragon` 与 `-c, --close-game`。
3. **配置 Schema 兼容性探针**：对比新版本附带的配置模板（如 `assets/config/config.example.yaml` 或 `config/*.sample.yml`）与 Vivian 适配器所依赖的核心字段（如 `pause_after_success`、`CompletionAction`）是否依然存在。
4. **上游自更新器熔断补丁**：在新版本配置中自动将 `check_update` / `auto_update` 设为 `false`。

---

## 4. 事务性就地升级与一键回滚流程（Transactional Update & Rollback）

为杜绝解压中断或文件占用导致引擎损坏，Vivian 更新管理器严格执行 **6 步原子升级协议**：

```
[Step 1: 进程与文件锁检查]
  检测目标引擎及对应游戏是否正在运行 -> 若运行中则拒绝更新或提示终止进程
       |
[Step 2: 保护区快照备份 (Backup)]
  将当前引擎的受保护配置目录 + 当前版本核心二进制归档至:
  data\backups\{GameType}\{OldVersion}_{Timestamp}.7z
       |
[Step 3: 离线解压至暂存目录 (Staging)]
  调用内置 7za.exe 将校验通过的新版压缩包解压至 engines\{GameType}_staging\
       |
[Step 4: 受保护用户配置迁移与合并 (Config Merge)]
  - BetterGI: 将原 User\ 目录完整复制到 staging\User\
  - March7th: 将原 config.yaml 与新 config.example.yaml 进行键值合并（保留用户设置，补齐新版新增默认键），并强制写入 pause_after_success=false, check_update=false
  - ZZZ-OneDragon: 将原 config\ 和 models\ 复制到 staging\
       |
[Step 5: 契约探针自检 (Smoke Test)]
  在 staging 目录运行 CLI/Schema 探针 -> 若失败则清理 staging 并中止升级
       |
[Step 6: 目录原子切换 (Atomic Swap)]
  将 engines\{GameType} 重命名为 engines\{GameType}_old
  将 engines\{GameType}_staging 重命名为 engines\{GameType}
  验证启动正常后清理 _old（保留 Step 2 的压缩快照供随时一键回滚）
```
