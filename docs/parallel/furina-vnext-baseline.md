# furina vNext 基线

- 日期：2026-11-13（America/New_York）。总任务：#59；T0：#60。
- SOURCE_BASE_SHA：ce3ebeea79c507825e66a9e97097a6f80ede33cc。
- 本地 integration/m6-dual-engine、GitHub #57 及正式安装 BUILD-INFO 指向此提交。旧工作目录仍是 c44dde1，未跟踪 AGENTS.md 保留原位并复制入本基线。
- 新集成分支：integration/furina-vnext；不重置旧分支，不改正式安装/账号库/上游配置。
- .NET SDK：10.0.403，已有安装 E:\\auto agent game\\.dotnet。首次默认 PATH 找不到 SDK，恢复前失败；改为仅当前进程指定已有 SDK，未修改 global.json。

## 规范与资源

源任务书：furina-vnext/FURINA_UI_AI_BRANCH_PLAN.md。参考图：furina-vnext/references/home.png 与 assistant.png；仅为设计参考，不能作为程序验收截图。用户已批准的分工、审查轮次和边界以 furina-vnext-ownership.md 为准。

## 验证命令（在各自工作树根目录）

```powershell
$env:DOTNET_ROOT = 'E:\\auto agent game\\.dotnet'
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
.\\scripts\\Build.ps1 -SmokeTest
dotnet test tests/VIvian.Tests/VIvian.Tests.csproj -c Release -p:M5DailyTestBuild=true --results-directory artifacts/tests-m5 --logger "trx;LogFileName=m5-daily-build.trx"
```

Build.ps1 覆盖默认构建、全量测试、自包含包和模拟 WPF 冒烟。Smoke.ps1 创建临时数据目录；禁止指向正式库。M5 测试顺序运行，避免同一工作树构建标志互相覆盖。结果记录见 furina-vnext-status.md。

## 当前源码事实

已有独立 AssistantWindow，关闭隐藏并取消模型请求；Loaded 主动聚焦输入框是 B/F 的焦点检查点。已有八个工具及真实连接测试入口、端点变化失效逻辑；C 先审计并补缺口，不能把已存在能力重新开发为第二套。propose_run 目前只支持单个星铁账号。

现有账号配置契约含 Game、AccountId、Revision；RunContext/RunEvent 尚不含完整账号和配置关联字段，不得宣称原计划的全部字段已实现。必要扩展由总控依据 C/F 的消费用例单独提交。

## 审查

本次工具目录未提供 DeepSeekHelper deepseek_review。当前独立审查状态 blocked，轮次 0。继续可做的开发/本地验证，不标通过；恢复工具后以实际 diff 和脱敏证据首审。

