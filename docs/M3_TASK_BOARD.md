# M3 任务与依赖（2026-11-04）

当前 main：`5c49461`；Windows CI 基线：PR #18 的 166 项通过（模拟 / 工程测试）。PR #20 为 170/170 通过，PR #23 为 168/168 通过，PR #24 为 169/169 通过；分别是独立分支，不能相加为一个 main 测试数。DeepSeek 的 `integration/deepseek-onboarding` 远端分支已存在，尚无 PR；该文档自述的本地无写权限是提交时的记录，远端推送已发生，但其后续 GitHub Issue/PR 写入权限未由本人验证。

| 任务 | 负责人 | 模块 / 验收 | 分支与 PR | 依赖 / 状态 |
|---|---|---|---|---|
| [#19](https://github.com/zcwjxy1412/Vivian-cutar/issues/19) 调度器风险 | Codex 开发；DeepSeek 独立复核 | 取消状态复位、停止时序、证据冲突和阻断显示；Windows 全量测试 | `fix/m3-scheduler-hardening`；[PR #20](https://github.com/zcwjxy1412/Vivian-cutar/pull/20) | CI 与复核待完成 |
| [#21](https://github.com/zcwjxy1412/Vivian-cutar/issues/21) 对抗测试 | DeepSeek | 新增 `tests/VIvian.Adversarial/**`，Job、SQLite、CLI、适配器异常；可复现证据和独立审查 | `tests/m3-deepseek-adversarial`；PR 待创建 | DeepSeek onboarding PR / #19；不修改 Codex 生产文件 |
| [#16](https://github.com/zcwjxy1412/Vivian-cutar/issues/16) 权限与 Job | Codex 无害原型；DeepSeek 审核 | SID、Job、同名隔离、崩溃恢复；桌面 UAC 拒绝/同意、跨权限和执行器杀死单独验收 | `spike/m3-privilege-validation`；[PR #23](https://github.com/zcwjxy1412/Vivian-cutar/pull/23) | CI 可做部分；桌面 UAC 授权待提供，ADR-007 保持提案 |
| [#22](https://github.com/zcwjxy1412/Vivian-cutar/issues/22) 星铁只读发现 | Codex；DeepSeek 审核 | 安装路径、CLI、版本、完整哈希、预检；发行包上只读参数验证后才开放状态/任务查询 | `feature/m3-m7a-readonly`；[PR #24](https://github.com/zcwjxy1412/Vivian-cutar/pull/24) | #16 / #19 / 精确发行包证据；发现代码已提交，CLI 参数 Pending |
| BetterGI 适配器 | Codex；DeepSeek 审核 | 独立版本与生命周期证据、测试、实机验收 | `feature/m3-bettergi-adapter`；PR 待创建 | 星铁安全门槛和独立上游实测 |
| OneDragon 适配器 | Codex；DeepSeek 审核 | 独立版本与生命周期证据、测试、实机验收 | `feature/m3-onedragon-adapter`；PR 待创建 | BetterGI 后，不能复制其权限/进程假设 |

DeepSeek onboarding 分支的原文件独立保留，不覆盖或代其合并。发布之前关闭全部 Critical / High 缺陷。没有完成确认的进程清理时，不进入下一任务。
