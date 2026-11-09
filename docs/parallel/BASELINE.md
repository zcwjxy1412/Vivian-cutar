# M6 并行开发基线

**BASE_SHA** `c44dde18c8fbb21caba770f8799d33b880f8f843`（2026-11-09，America/New_York）。

| 分支 | 角色 | 核对结果 |
| --- | --- | --- |
| `main` | 正式发布线 | `aa0db832736d827d2ce335bffcd3e138ba9b81b4` |
| `feature/m5-2-auto-account-switch` | [PR #52](https://github.com/zcwjxy1412/Vivian-cutar/pull/52) | 草稿开放，基于 `feature/m5-1-multi-account-safety`，head `3512ca4d44cb551b61bc9c3666d9e150cd450bbe` |
| `chore/m5-2-workspace-cleanup` | [PR #53](https://github.com/zcwjxy1412/Vivian-cutar/pull/53) | 草稿开放，基于 #52 分支，head 为 BASE_SHA |
| `feature/m6-ai-integration` | AI 与公共改动 | 从 BASE_SHA 分出 |
| `feature/m6-genshin-bettergi` | 原神适配器 | 从 BASE_SHA 分出 |
| `feature/m6-zzz-onedragon` | 绝区零适配器 | 从 BASE_SHA 分出 |
| `integration/m6-dual-engine` | M6 合流目标 | 从 BASE_SHA 吸收已验收的增量 |

远端 PR 元数据与 Git 分支头一致，`git merge-base --is-ancestor 3512ca4 c44dde1` 通过，核对时原源码工作区干净。正式安装的 BUILD-INFO 仍指向已实机验收的 `aff9a76d7fc0884c35ff3656f0433cb3adfcefd4`，星铁助手 v2026.10.31，数据库 schema v6。

游戏窗口沿用现有适配器、任务、执行准入契约继续开发。公共变更由 AI 窗口独立提交并公布 SHA，各窗口按需吸收。正式启动器、固定游戏依赖和原库保留，测试使用隔离目录。真机轮次须串行，确认进程清理与配置恢复后交接。M6 草稿基于 #53 分支，保留既有堆叠关系。
