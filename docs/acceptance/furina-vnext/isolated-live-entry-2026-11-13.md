# 隔离实机入口

只接受 UnifiedInstallBuild + M5DailyTestBuild 候选。`--prepare-vnext-acceptance <目录>` 在持有正式档案单实例锁和公共桌面锁时复制已准备数据库；正式源只读，隔离副本升级为当前版本。`--vnext-acceptance <目录>` 仅打开专用 `%LOCALAPPDATA%/VIvian-vNext-Acceptance/<32位非空GUID>`，保留原有单实例、物理路径及 prepared 数据库检查。错位/缺失/多余参数明确拒绝；不自动运行游戏，不替换安装，不在启动时自动准备上游账号。

局部验证：M5+Unified 严格参数/持久计划及既有快照迁移测试23/23；实际源副本生成和游戏仍待候选构建后验证。

DeepSeekHelper 本入口任务实际两轮，artifacts/deepseek/vnext-profile/round{1,2}-response.json。首审的缺少单实例锁和祖先链接检查不成立，原代码分别在启动共用路径和 FindReparsePoint 已执行。补齐 Prepare 与 Open 同等严格解析（包括错位 Prepare 不回落正式入口）、创建前已存在目录的明确拒绝。复审无已核实的阻断；其缺 implicit using 的疑问与已通过编译矛盾；父目录竞态由既有 PublishAsync 物理句柄及再次链接检查约束。两轮上限保留。

构建说明随后修正过时 schema7 字样，从 LocalDatabase.CurrentSchemaVersion 读取真实版本，并注明验收需显式专用副本参数；仅元数据，不变更执行许可。
