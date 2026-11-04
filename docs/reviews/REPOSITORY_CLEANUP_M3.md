# 仓库清理审计（M3，2026-11-04）

审计对象：main @ `5c49461`（共 100 个跟踪文件），以及全部远程分支和开放 PR 的 Git 树。
方法：没有只看文件名。逐个文件检查了文档之间的引用、源码与测试的引用（例如测试代码引用 `M2_PLAN.md` 的场景编号）、解决方案与项目引用、两个 CI 工作流和构建脚本，以及各分支上是否跟踪了生成文件。

## 1. 结论

**main 上没有可以证明安全删除的文件。** 每个文档至少被另一个文档、测试代码或工作流引用；生成文件（`bin/`、`obj/`、`TestResults/`、TRX、日志、压缩包、二进制）一个也没有被跟踪。因此本 PR **不删除任何文件**，只做有依据的结构改进。

## 2. 分类（main）

| 类别 | 内容 | 处理 |
| --- | --- | --- |
| A 生产代码 | `src/VIvian.App`、`src/VIvian.Core`、`src/VIvian.Infrastructure`、`VIvian.slnx`、`Directory.Build.props`、`global.json`、`NuGet.Config`、各 `packages.lock.json` | 保留，不改动 |
| B 测试与夹具 | `tests/VIvian.Tests`、`tests/VIvian.FakeEngine`、`tests/VIvian.CrashHost` | 保留，不改动 |
| C 现行文档 | `README.md`、`docs/DECISIONS.md`、`docs/M3_PREREQUISITES.md`、`docs/evidence/EVIDENCE_INDEX.md`、`docs/architecture/ADR-007-*`、`docs/TROUBLESHOOTING.md` | 保留；新增导航 `docs/README.md` |
| D 必须保留的历史证据 | `docs/M1_ACCEPTANCE.md`、`docs/M2_PLAN.md`、`docs/M2_ACCEPTANCE.md`、`docs/RELEASE_NOTES/`、`docs/PR_DRAFT.md`、`docs/INPUTS.md`、`docs/reference/`、`docs/reviews/*` | 保留；`PR_DRAFT.md` 加“历史文档”说明 |
| E 重复或被取代的文档 | Gemini 原文（PR #18）与 PR #32 的新报告、勘误在内容上有重叠 | 不删除：原文需要完整保留，且都已标注“Gemini 原始研究”并指向审查记录。由导航第 4 节说明以哪份为准 |
| F 临时整合产物 | 不在 main 上（见第 5 节分支） | — |
| G 生成文件 | 无 | `.gitignore` 增加 TRX、binlog、dump、coverage 和编辑器文件 |
| H 确认无用的文件 | 无 | — |

`THIRD-PARTY-NOTICES.md` 没有被其他文件引用，但它是第三方许可声明，必须保留。

## 3. 任务中点名的候选项

| 路径 | 结论 | 依据 |
| --- | --- | --- |
| `docs/PR_DRAFT.md` | 保留，标为历史 | M1 验收清单把它列为 M1 交付物；内容只对当时成立 |
| `docs/reference/original/` | 保留，逐字不改 | 原始输入；`INPUTS.md` 记录了它们的 SHA-256，修改会破坏溯源。其中反引号写的 `docs/...` 路径指的是原方案自己的目录，在本仓库不存在（共 17 处，属预期，已在导航中说明） |
| `docs/research/` | 保留 | Gemini 原文，均被 `EVIDENCE_INDEX.md` 和审查记录引用；两张图被 3 份文档引用 |
| `docs/reviews/` | 保留 | 审查记录 |
| `docs/evidence/` | 保留 | 现行证据索引 |
| `docs/M3_PREREQUISITES.md` | 保留，加状态更新 | 表中 C2、C3 已完成但没有标记；只在开头补充状态说明，不改动原表 |

## 4. 链接检查

用脚本检查了 main 上所有 Markdown 的相对链接 `[...](...)`，没有失效链接。反引号中的路径只在 `docs/reference/original/` 中失效（原因见上），另有 README 中的占位写法 `docs/RELEASE_NOTES/<版本>.md`，属于正常写法。

## 5. 分支（只提出建议，**未删除任何分支**，需要用户批准）

| 分支 | 情况 | 建议 |
| --- | --- | --- |
| `docs/m2-plan`、`docs/m1-verification`、`feature/m2.1-execution-core`、`feature/m2.2-run-persistence`、`feature/m2.3-process-supervision`、`feature/m2.4-single-instance`、`feature/m2.5-monitor-ui`、`fix/m2-review-hardening`、`docs/m2-acceptance`、`feature/m3-log-file-tailer`、`fix/m3-process-handle-inheritance`、`docs/m3-evidence-baseline`、`spike/m3-privilege-boundary`、`docs/gemini-m3-import` | 对应 PR #1–#18 都已合并（squash），内容已在 main | 可以删除；GitHub 上合并过的 PR 仍保留完整提交记录 |
| `tmp/contrast-mutant`、`tmp/screenshot-peek` | 一次性的实验分支（提交信息写明 throwaway），没有 PR | 可以删除 |
| `test/m3-run-ownership-with-20` | #33 + #20 的组合验证分支，CI 证据已记录在 PR #33 评论中；现在已被 #33 取代 | 可以删除 |
| `test/m3-safety-stop-integration`（PR #31） | Codex 的组合验证，禁止合并 | 等 #30/#20/#33 处理完后由用户决定关闭 |
| `feature/m1-foundation` | M1 原始分支，与 main 的历史不同 | 保留（M1 溯源） |
| `archive/codex-delivery` | 最初交付的归档，包含构建日志 | 保留（归档；其中的日志是当时的证据） |
| `integration/deepseek-onboarding` | DeepSeek 只读接入记录，没有 PR | 保留，或由用户决定是否合并到文档 |

开放 PR 的分支一律保留。

## 6. 本 PR 的改动

- 新增 `docs/README.md`：文档导航（现行规则、验收记录、历史审查、外部研究原文）以及进行中的 PR 和安全门禁。
- `README.md`：准确描述 M3 状态（尚无真实适配器、安全门禁在审查中、Pending 项），说明 FakeEngine、CrashHost 和模拟测试的作用，并链接导航。
- `docs/PR_DRAFT.md`：在开头加“历史文档”说明。
- `docs/M3_PREREQUISITES.md`：在开头加状态更新（不改动原表）。
- `.gitignore`：增加测试输出、诊断文件和编辑器文件。
- 没有改动任何生产代码、测试、CI 工作流或构建脚本。
