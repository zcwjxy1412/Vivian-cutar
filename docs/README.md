# 文档导航

按用途分为四类。**“现行”** 是当前开发要遵守的依据；**“历史/证据”** 只用于追溯，内容可能指向旧提交，不代表当前状态；**“外部研究原文”** 是作者结论，以对应的审查记录为准。

## 1. 现行：产品、架构与规则

| 文档 | 内容 |
| --- | --- |
| [`../README.md`](../README.md) | 项目概览、当前状态、构建与 CI |
| [`DECISIONS.md`](DECISIONS.md) | M1 起的设计决策（ADR-001 起） |
| [`architecture/ADR-007-M3-PRIVILEGE-BOUNDARY.md`](architecture/ADR-007-M3-PRIVILEGE-BOUNDARY.md) | M3 权限与进程身份边界（**提案**，不能据此启用生产适配器） |
| [`M3_PREREQUISITES.md`](M3_PREREQUISITES.md) | 接入真实引擎之前必须满足的条件 |
| [`evidence/EVIDENCE_INDEX.md`](evidence/EVIDENCE_INDEX.md) | M3 证据索引：已固定的上游源码事实与仍为 Pending 的实测项 |
| [`RUN_ON_WINDOWS.md`](RUN_ON_WINDOWS.md) | 下载和运行 Windows 开发版（模拟引擎） |
| [`M5_LOCAL_TEST_WORKFLOW.md`](M5_LOCAL_TEST_WORKFLOW.md) | 本机固定 M5 测试版的自动部署、反馈与回滚 |
| [`M5_DAILY_WINDOWS_TEST.md`](M5_DAILY_WINDOWS_TEST.md) | 星铁 `daily` 的逐次授权与实测步骤 |
| [`M3_DESKTOP_TEST_HANDOFF.md`](M3_DESKTOP_TEST_HANDOFF.md) | 预桌面阶段结束后交给 Codex 的桌面测试交接 |
| [`TROUBLESHOOTING.md`](TROUBLESHOOTING.md) | 故障排查 |

## 2. 验收记录（保留，不改写）

| 文档 | 内容 |
| --- | --- |
| [`M1_ACCEPTANCE.md`](M1_ACCEPTANCE.md) | M1 自动化验收 |
| [`M2_PLAN.md`](M2_PLAN.md) | 已批准的 M2 计划（测试代码引用其中的场景编号） |
| [`M2_ACCEPTANCE.md`](M2_ACCEPTANCE.md) | M2 自动化验收 |
| [`RELEASE_NOTES/`](RELEASE_NOTES/) | 发布说明（发布工作流会自动读取） |
| [`PR_DRAFT.md`](PR_DRAFT.md) | M1 当时的 PR 说明草稿（历史，M1 验收清单引用） |

## 3. 审查与历史输入

| 文档 | 内容 |
| --- | --- |
| [`reviews/M2_REVIEW_BRIEF.md`](reviews/M2_REVIEW_BRIEF.md) | 交给外部审查者的 M2 材料 |
| [`reviews/M3_GEMINI_RESEARCH_VALIDATION.md`](reviews/M3_GEMINI_RESEARCH_VALIDATION.md) | 对 Gemini M3 研究（PR #18）的独立审查：**判断以它为准** |
| [`reference/INTEGRATED_REVIEW.md`](reference/INTEGRATED_REVIEW.md) | 项目启动时对十份原始方案的整合复核 |
| [`INPUTS.md`](INPUTS.md) | 原始输入文件的来源与 SHA-256 |
| [`reference/original/`](reference/original/) | 项目启动时的原始方案，**逐字保存**。其中以反引号写出的 `docs/...` 路径指的是原方案自己的目录结构，在本仓库中不存在，这是预期的 |

## 4. 外部研究原文（Gemini，PR #18）

这些文档顶部都标有“Gemini 原始研究”。其中的版本、示例和建议是作者结论，实测状态与冲突见 [`reviews/M3_GEMINI_RESEARCH_VALIDATION.md`](reviews/M3_GEMINI_RESEARCH_VALIDATION.md)。

- [`research/M3_GEMINI_FINAL_SUMMARY.md`](research/M3_GEMINI_FINAL_SUMMARY.md)
- [`research/M3_BETTERGI_ANALYSIS.md`](research/M3_BETTERGI_ANALYSIS.md)、[`research/M3_MARCH7TH_ANALYSIS.md`](research/M3_MARCH7TH_ANALYSIS.md)、[`research/M3_ONEDRAGON_ANALYSIS.md`](research/M3_ONEDRAGON_ANALYSIS.md)
- [`research/M3_ENGINE_INTERFACE_MATRIX.md`](research/M3_ENGINE_INTERFACE_MATRIX.md)、[`research/M3_VERSION_COMPATIBILITY.md`](research/M3_VERSION_COMPATIBILITY.md)
- [`architecture/M3_ADAPTER_PROPOSAL.md`](architecture/M3_ADAPTER_PROPOSAL.md)、[`requirements/M3_UI_SPEC.md`](requirements/M3_UI_SPEC.md)
- [`research/assets/gemini-m3/`](research/assets/gemini-m3/)：上述文档引用的图示

[PR #32](https://github.com/zcwjxy1412/Vivian-cutar/pull/32) 带来四份较新的 Gemini 报告（`research/M3_UPSTREAM_RELEASE_VERIFICATION.md`、`research/M3_ENGINE_CLI_COMPATIBILITY.md`、`research/M3_ENGINE_PROCESS_AND_PRIVILEGE_RISKS.md`、`research/M3_ADAPTER_IMPLEMENTATION_REQUIREMENTS.md`）和一份勘误；勘误（[`research/M3_GEMINI_RESEARCH_ERRATA.md`](research/M3_GEMINI_RESEARCH_ERRATA.md)）优先于这些原文。原文中“VIvian 以管理员身份运行”“按安装路径或进程名清扫进程”两条与项目安全规则冲突，不予采用。

## 5. 进行中的工作与安全门禁

以下表格保留 2026-11-04 的历史状态；当前 M5 分支依赖顺序为 PR #46 → #47 → #48，固定测试版与自动部署工作位于新的独立 PR。合并前须分别审查，不能直接把后续分支合并到 main。

| PR | 内容 | 状态 |
| --- | --- | --- |
| [#30](https://github.com/zcwjxy1412/Vivian-cutar/pull/30) | #28/#29 安全门禁（RunId 启动许可、有界调用） | 已合并 |
| [#33](https://github.com/zcwjxy1412/Vivian-cutar/pull/33) | 运行归属、有界迟到清理、有界持久化、停止/启动许可（包含 #20） | 已合并；Codex 验收通过 |
| [#20](https://github.com/zcwjxy1412/Vivian-cutar/pull/20) | 调度器取消与矛盾证据 | 已随 #33 合并 |
| [#32](https://github.com/zcwjxy1412/Vivian-cutar/pull/32) | Gemini 新报告与勘误 | 已合并；勘误优先于 Gemini 原文 |
| [#34](https://github.com/zcwjxy1412/Vivian-cutar/pull/34) | 文档导航与仓库审计 | 已合并 |
| [#35](https://github.com/zcwjxy1412/Vivian-cutar/pull/35) | 0.3.0-dev：引擎就绪检查、星铁官方发行包核对、Windows 开发版 | 已合并（Claude 自审，经用户授权） |
| [#23](https://github.com/zcwjxy1412/Vivian-cutar/pull/23)、[#24](https://github.com/zcwjxy1412/Vivian-cutar/pull/24)、[#26](https://github.com/zcwjxy1412/Vivian-cutar/pull/26) | Job 归属检查、星铁只读发现、官方发行包审计 | 已随 #35 合并 |
| [#27](https://github.com/zcwjxy1412/Vivian-cutar/pull/27) | Claude 独立审查报告（历史） | 保留为记录 |
| [#31](https://github.com/zcwjxy1412/Vivian-cutar/pull/31) | #30 + #20 组合验证 | 已关闭，未合并 |

仍然开放的阻断项：[#16](https://github.com/zcwjxy1412/Vivian-cutar/issues/16)（桌面 UAC 与提权进程归属）、[#22](https://github.com/zcwjxy1412/Vivian-cutar/issues/22)（星铁只读 CLI 查询的实测）、[#25](https://github.com/zcwjxy1412/Vivian-cutar/issues/25)（官方发行包实际文件的独立核验）。

整合后的安全基线（仅限模拟引擎范围）：有界的适配器调用与状态写入、按 RunId 发放的启动许可、停止时先撤销启动许可、会话安全门禁。CI 通过不是 UAC、提权进程或真实游戏的证明。

main 上目前没有任何真实适配器；调度器还会用 `RealAdapterUnverified` 门禁阻断所有非模拟适配器，直到适配器通过独立审查。桌面 UAC、提权子进程、真实引擎和真实游戏都仍为 Pending。
