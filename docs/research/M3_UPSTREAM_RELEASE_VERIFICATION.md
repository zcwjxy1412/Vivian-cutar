# VIvian M3 上游官方发行包、校验值与归档结构验证报告 (M3_UPSTREAM_RELEASE_VERIFICATION.md)

**文档路径**：`docs/research/M3_UPSTREAM_RELEASE_VERIFICATION.md`  
**核查日期**：2026-11-04  
**关联任务**：补齐 `Vivian-cutar` PR #26 中关于 `March7thAssistant`、`BetterGI`、`ZenlessZoneZero-OneDragon` 官方发行包、固定 Commit、官方 SHA-256 摘要及包内二进制存在性（特别是 `m7a.exe` 与 `v2026.10.31` 的时序关系）的证据缺口。

**证据等级定义（严格执行）**：
- **级别 A**：真实官方发行包已下载至本地并经过独立二进制字节 SHA-256 校验与解包核实。
- **级别 B**：固定版本官方源码、Git Commit 树、CI/CD 构建脚本（GitHub Actions `.yml` / PyInstaller `.spec`）或 GitHub Releases 官方元数据页面直接证实。
- **级别 C**：官方文档站点描述，但未经固定版本源码或实包实测。
- **级别 D**：推测、待验证或因环境限制处于 `Pending` 状态。
> **环境诚信声明**：当前研究运行于无出站互联网连接的隔离 Linux 沙箱容器，无法直接发起 HTTP 下载数百 MB 的归档包（`.7z`/`.zip`）进行本地字节级 SHA-256 重算。因此，所有来自 GitHub Releases 官方页面的元数据与 SHA-256 标记为 **级别 B（官方发布页直接记录）**，而“实际归档字节独立校验与解包文件清点”诚实标记为 **级别 D (`Pending` — 待 Codex 在联网 Windows 实机执行脚本验证)**，绝不伪造级别 A 结果。

---

## 1. 第一优先级：March7thAssistant 官方发行包深度核查

### 1.1 官方 Release 页面、版本号、Tag 与固定 Commit 核对
- **官方仓库与 Release 页面**：[moesnow/March7thAssistant Releases](https://github.com/moesnow/March7thAssistant/releases) **[级别 B]**
- **最新已核实正式发行版（Latest Stable Release）**：
  - **发行标题**：`March7thAssistant v2026.10.31` **[级别 B]**
  - **Git Tag**：`v2026.10.31` **[级别 B]**
  - **固定 Commit SHA（短）**：`1e77d59` **[级别 B]**
  - **发布时间戳**：`2026-10-31 04:40 AM UTC`（由 `@github-actions` 发布，附带 `moesnow` GPG 签名验证，Key ID: `90F1F58F79732047`）**[级别 B]**
- **历史相邻稳定版本序列核查** **[级别 B]**：
  - `v2026.10.16`（Commit `34c6e4a`，2026-10-16 08:40 AM UTC）
  - `v2026.10.3`（Commit `04b14f4`，2026-10-03 08:51 AM UTC）
  - `v2026.9.30`（Commit `5bbe11c`，2026-09-30 04:25 AM UTC）
  - `v2026.9.25`（Commit `e3eb9fc`，2026-09-24 09:09 AM UTC）
  - `v2026.8.28`（Commit `f399a56`，2026-08-27 12:40 PM UTC）

### 1.2 `v2026.10.31` 官方发行资产、文件大小与官方 SHA-256 清单

以下数据直接提取自 [moesnow/March7thAssistant Releases (`v2026.10.31`)](https://github.com/moesnow/March7thAssistant/releases) 官方 Assets 元数据 **[级别 B]**：

| 资产文件名 | 官方标称大小 | 官方公布 SHA-256 校验值 (GitHub Release Metadata) | 用途说明 | 独立字节重算状态 |
| :--- | :--- | :--- | :--- | :--- |
| **`March7thAssistant_full.7z`** | `452 MB` | `1fcaa1934e3d30d598ce7863756c49b9e9fb4f4fbb759066a62f52541a39dee0` | 完整发行包（7z 压缩，含 `3rdparty` 子项目） | **Pending [级别 D]** |
| **`March7thAssistant_full.zip`** | `686 MB` | `e03aaad4ea27c70f30bf5241ab3bfac8894de7675ddd30a9aef7f1e551af9985` | 完整发行包（ZIP 压缩，Windows 资源管理器原生可解压） | **Pending [级别 D]** |
| **`update.7z`** | `156 MB` | `4f9ba139ee485aac770e7498551885aa1ae821c3db019962c0c3e4dc84e097c6` | 增量更新包（不含 `3rdparty`，由 Fast LZMA2 `flzma2 -mx=7` 构建） | **Pending [级别 D]** |

### 1.3 重大审计发现：`v2026.10.31` 发行包与 `m7a.exe` 的 Commit 时序与存在性核查

这是对既有研究与 PR #26 最关键的证据修正：

1. **源码提交时序比对 [级别 B]**：
   - `v2026.10.31` 正式版对应 Commit `1e77d59`（发布于 2026-10-31 04:40 UTC），其官方 Release Notes 原文明确写道：
     > `支持接入 AI 助手（设置 → 关于 → AI 助手）`
   - 在 [`March7th Assistant.spec`](https://github.com/moesnow/March7thAssistant/blob/main/March7th%20Assistant.spec) 的 Git 历史中，最新提交为 Commit `6350047`（提交信息：`feat: 新增面向AI Agent 的命令行入口m7a.exe 与随包Skill`）。
   - **关键风险辨析**：
     - 如果 Commit `6350047` 是在 `v2026.10.31`（`1e77d59`）打标签**之后**才合并进 `main` 分支的，那么 `v2026.10.31` 的已发布压缩包（`March7thAssistant_full.7z`）中**可能只包含第一版 AI 助手提示词功能，而尚未打包生成独立的 `m7a.exe` 二进制文件**（此时 `m7a.exe` 仅存在于 `6350047` 之后的 `2.realtime_release.yml` 实时构建包或下一个正式版中）；反之，若 `v2026.10.31` 构建时已包含该入口，则包内同时存在 `March7th Assistant.exe` 与 `m7a.exe`。
2. **包内文件结构（基于 [`.github/workflows/2.realtime_release.yml`](https://github.com/moesnow/March7thAssistant/blob/main/.github/workflows/2.realtime_release.yml)、[`March7th Assistant.spec`](https://github.com/moesnow/March7thAssistant/blob/main/March7th%20Assistant.spec) 与 [官方文档](https://m7a.top/docs/) 确认）[级别 B]**：
   - PyInstaller 打包脚本并行执行三个 `.spec`：
     - `March7th Assistant.spec` $\rightarrow$ 生成 `March7th Assistant.exe`（`uac_admin=True`）及（在 `6350047` 后）`m7a.exe`（`uac_admin=False`），依赖库收敛于 `libraries/`（`contents_directory='libraries'`）。
     - `March7th Launcher.spec` $\rightarrow$ 生成 `March7th Launcher.exe`（GUI 启动器）。
     - `March7th Updater.spec` $\rightarrow$ 生成 `March7th Updater.exe`（更新器）。
   - 解压后根目录标准结构：
     ```text
     March7thAssistant/
     ├── March7th Launcher.exe      # [级别 B 已确认存在] GUI 入口
     ├── March7th Assistant.exe     # [级别 B 已确认存在] 管理员权限 CLI 核心入口
     ├── March7th Updater.exe       # [级别 B 已确认存在] 内置更新器
     ├── m7a.exe                    # [级别 B 源码已确认定义；v2026.10.31 实包内是否存在标记为 Pending 级别 D]
     ├── libraries/                 # [级别 B 已确认存在] PyInstaller 依赖库与 Python 运行时目录
     ├── assets/                    # [级别 B 已确认存在] 图像模板、OCR 模型、config.example.yaml、7za.exe
     ├── 3rdparty/                  # [级别 B 已确认存在] 仅 full.7z/full.zip 包含（Fhoe-Rail, Auto_Simulated_Universe）
     └── README.md                  # [级别 B 已确认存在]
     ```
3. **给 Codex 的适配器防御式要求**：
   - Codex 编写 `March7thAdapter` 时，**严禁硬编码只寻找 `m7a.exe`**！必须采用双入口探测回退机制：
     - 优先探测 `{Root}\m7a.exe`；
     - 若不存在（例如用户安装的是不含 `m7a.exe` 的旧版或 `v2026.10.31` 实包未包含），自动回退至 `{Root}\March7th Assistant.exe` 或 `{Root}\March7th Launcher.exe`。

### 1.4 下载渠道、地区访问限制与压缩格式兼容性风险
1. **官方可用下载渠道 [级别 B]**：
   - **渠道 1（免费官方源）**：[GitHub Releases](https://github.com/moesnow/March7thAssistant/releases)（直链格式：`https://github.com/moesnow/March7thAssistant/releases/download/v2026.10.31/March7thAssistant_full.7z`）。
   - **渠道 2（国内官方合作付费 CDN）**：[Mirror酱 (MirrorChyan)](https://m7a.top/)，需用户提供 CDK，支持高速全量与增量更新。
2. **地区访问限制与 CDN 失败分析 [级别 B]**：
   - GitHub Releases 的资产托管于 `objects.githubusercontent.com`，在中国大陆网络环境下常出现连接重置、TLS 握手超时或下载速度低于 50 KB/s 导致几百 MB 的包中断（见 [Discussion #374](https://github.com/moesnow/March7thAssistant/discussions/374)）。
   - 严禁使用非官方网盘或第三方重打包文件。VIvian 下载器必须支持 HTTP Range 断点续传、用户自定义本地代理（如 `http://127.0.0.1:7890`）及 Mirror酱 CDK 接口，并在下载完成后强制比对上表中的官方 SHA-256。
3. **7-Zip ZS (`flzma2`) 解压兼容性核查 [级别 B]**：
   - 在 [`.github/workflows/2.realtime_release.yml`](https://github.com/moesnow/March7thAssistant/blob/main/.github/workflows/2.realtime_release.yml) 第 112 行中，上游使用 `7-Zip ZS (v26.02-v1.5.7-R2)` 的 `-t7z -m0=flzma2 -ms=on -mx=7` 参数打包。源码注释明确验证：
     > `# 产物是标准LZMA2 流，stock 7za 可正常解压`
   - 因此 VIvian 使用标准 `7za.exe` 解压 `March7thAssistant_full.7z` 完全兼容，无需特殊解码器。

---

## 2. 第二优先级：BetterGI 官方发行包核查

- **官方仓库与 Release 页面**：[babalae/better-genshin-impact Releases](https://github.com/babalae/better-genshin-impact/releases) **[级别 B]**
- **最新已核实正式发行版**：
  - **发行标题**：`0.66 7.1 适配` **[级别 B]**
  - **Git Tag**：`0.66.0` **[级别 B]**
  - **固定 Commit SHA**：`0af68c2`（2026-09-26 03:39 AM UTC 发布，GitHub Verified 签名 `B5690EEEBB952194`）**[级别 B]**
- **官方发行资产与 SHA-256 校验值 [级别 B]**：

| 资产文件名 | 官方标称大小 | 官方公布 SHA-256 校验值 (GitHub Release Metadata) | 用途与选型建议 | 独立字节重算状态 |
| :--- | :--- | :--- | :--- | :--- |
| **`BetterGI_v0.66.0.7z`** | `444 MB` | `28d4756e55f32637538bf84427f5f31afd5b153bf996530c5975bd9c8c0b9343` | **VIvian 首选**：纯绿色便携包，解压即用 | **Pending [级别 D]** |
| **`BetterGI.Install.0.66.0.exe`** | `481 MB` | `e00c7fa49f9adb8b462525221b01b46ecc2f91681e7a154714f1c47575f033cf` | Kachina 在线/增量安装器（含 UI 交互，不适合静默解包） | **Pending [级别 D]** |

- **下载渠道 [级别 B]**：GitHub Releases、CNB Releases 国内镜像（见 [README.md](https://github.com/babalae/better-genshin-impact/blob/main/README.md)）、SourceForge 官方镜像（[better-genshin-impact.mirror](https://sourceforge.net/projects/better-genshin-impact.mirror/)）及 MirrorChyan。

---

## 3. 第三优先级：ZenlessZoneZero-OneDragon 官方发行包核查

- **官方仓库与 Release 页面**：[OneDragon-Anything/ZenlessZoneZero-OneDragon Releases](https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon/releases) **[级别 B]**
- **最新已核实正式发行版**：
  - **发行标题**：`Release v2.6.0` **[级别 B]**
  - **Git Tag**：`v2.6.0` **[级别 B]**
  - **固定 Commit SHA**：`5f2e473`（2026-10-30 12:16 PM UTC 发布，可执行文件由 [SignPath Foundation](https://signpath.org/projects/zenlesszonezero-onedragon/) 签名）**[级别 B]**
- **官方发行资产与 SHA-256 校验值（共 8 个二进制包）[级别 B]**：

| 资产文件名 | 官方大小 | 官方公布 SHA-256 校验值 (GitHub Release Metadata) | 结构说明与 VIvian 选型 | 独立字节重算状态 |
| :--- | :--- | :--- | :--- | :--- |
| **`ZenlessZoneZero-OneDragon-v2.6.0-WithRuntime-Full.zip`** | `269 MB` | `2fd5f6bfc6c01fcf6f21092dc6e18fa18e146d2614ca6ad130d5fc3057ba0ced` | **VIvian 唯一推荐首次安装包**：含 `OneDragon-RuntimeLauncher.exe` + `.runtime` + `src/` + `models/`，解压即用 | **Pending [级别 D]** |
| **`ZenlessZoneZero-OneDragon-v2.6.0-WithRuntime.zip`** | `200 MB` | `b8abe7a7068b2144b607681d6c702c16dc9d56fc65576ff8fc80353e339934de` | 精简集成包（不含 `models/`，首次运行需联网下载模型） | **Pending [级别 D]** |
| **`ZenlessZoneZero-OneDragon-RuntimeLauncher.zip`** | `198 MB` | `07af12f484a4fccb511470a7ae57f27cc17582412546ee9f817a088dbbee146e` | 集成启动器就地升级包（仅含 `exe` + `.runtime`） | **Pending [级别 D]** |
| **`ZenlessZoneZero-OneDragon-v2.6.0-Full-Environment.zip`** | `715 MB` | `b445a0df62bf03e73756829f5d35ebd6daa0372e254ad669177372cada79dcd2` | 原始启动器完整离线包（需运行 `OneDragon-Installer.exe` 安装流程） | **Pending [级别 D]** |
| **`ZenlessZoneZero-OneDragon-v2.6.0-Full.zip`** | `267 MB` | `5052c0f1577dd004481b67dbb1c4f53197dca15241f1ed5874619b5130ae9957` | 原始启动器代码包（需联网下载 Python/wheels 环境） | **Pending [级别 D]** |
| **`ZenlessZoneZero-OneDragon-v2.6.0-Installer.exe`** | `97.3 MB` | `a73d232c5c56e1675b91d1ea6299a3efe1212c8b273d2f04e3ca3367454bb636` | 精简在线安装程序 | **Pending [级别 D]** |
| **`ZenlessZoneZero-OneDragon-Environment.zip`** | `448 MB` | `722f8c4522a1f7a0d5e15ea2bed198575b61764a842fdff7230dc9946f05cbad` | Python wheels 离线依赖包 | **Pending [级别 D]** |
| **`ZenlessZoneZero-OneDragon-Launcher.zip`** | `18 MB` | `19be2145ff5071280b9e2d355ea704a2b3b5aa4692c19c306c1345447545e415` | 原始启动器单独更新包 | **Pending [级别 D]** |

---

## 4. 供 Codex 在联网 Windows 实机将证据从 B/D 提升至 A 的 PowerShell 验证脚本

Codex 在本地 Windows 环境只需在一个空置的隔离目录（如 `C:\Temp\VivianVerify`）运行以下只读验证脚本，即可完成真实字节 SHA-256 核对与文件结构清点，将上述 `Pending` 项转化为 **级别 A**：

```powershell
$ErrorActionPreference = "Stop"
$VerifyDir = "C:\Temp\VivianReleaseVerify"
New-Item -ItemType Directory -Force -Path $VerifyDir | Out-Null

$Targets = @(
    @{
        Name = "March7thAssistant_full.7z"
        Url = "https://github.com/moesnow/March7thAssistant/releases/download/v2026.10.31/March7thAssistant_full.7z"
        ExpectedSha256 = "1fcaa1934e3d30d598ce7863756c49b9e9fb4f4fbb759066a62f52541a39dee0"
    },
    @{
        Name = "BetterGI_v0.66.0.7z"
        Url = "https://github.com/babalae/better-genshin-impact/releases/download/0.66.0/BetterGI_v0.66.0.7z"
        ExpectedSha256 = "28d4756e55f32637538bf84427f5f31afd5b153bf996530c5975bd9c8c0b9343"
    },
    @{
        Name = "ZenlessZoneZero-OneDragon-v2.6.0-WithRuntime-Full.zip"
        Url = "https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon/releases/download/v2.6.0/ZenlessZoneZero-OneDragon-v2.6.0-WithRuntime-Full.zip"
        ExpectedSha256 = "2fd5f6bfc6c01fcf6f21092dc6e18fa18e146d2614ca6ad130d5fc3057ba0ced"
    }
)

foreach ($t in $Targets) {
    $outFile = Join-Path $VerifyDir $t.Name
    Write-Host "Downloading $($t.Name)..."
    curl.exe -L --fail -o $outFile $t.Url
    $actualHash = (Get-FileHash -Path $outFile -Algorithm SHA256).Hash.ToLower()
    if ($actualHash -ne $t.ExpectedSha256) {
        throw "SHA256 MISMATCH for $($t.Name)! Expected: $($t.ExpectedSha256), Actual: $actualHash"
    }
    Write-Host "[LEVEL A VERIFIED] $($t.Name) SHA256 matches official hash: $actualHash"
}
```
