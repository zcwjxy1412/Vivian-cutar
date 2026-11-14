# 原安装更新执行记录

用户授权修复阻塞后更新 `E:/auto agent game/VIvian`，保留备份和账号数据，原快捷方式不变，真实游戏由用户自行测试。

1. 修复原神原生日常接线：旧 CLI 的两个 Verified 标志继续 false，统一 M5 使用已有 NativeDaily/恢复/持久计划入口，不以 UI 接线声称实机成功。
2. 隔离入口允许独立发行包核验，不触发自动发现、账号准备或上游配置恢复；正式入口保留原准备逻辑。
3. 三聊天补齐小窗口输入可用性、真实合成模型往返和执行残余核实。每个实现任务最多两轮 DeepSeek；历史两轮后最终补丁的审查限制不抹去。
4. 构建同一提交，完整统一测试、只读启动检查。原数据 schema7 不可直接交给 schema8 严格打开；先在副本升级、逐表核对原数据，部署时保留旧库/完整档案备份。
5. 持有原档案单实例和共同桌面锁，确认无活动任务，备份后切换安装及新数据库；任何失败恢复原程序和原库。保留快捷方式目标与参数。部署后核对产物哈希、数据完整性，提供回滚位置。不开启真实游戏。

初始基线 a1e05ed；接线测试在原条件下 1失败/1通过，确认统一M5缺少原生日常委托。实际部署与最终验证见文末。

接线修复定向36/36，补充reward仍保留恢复委托断言后注册用例2/2。DeepSeek首审1轮（v4-pro，thinking disabled，4117/1292 tokens；artifacts/deepseek/deploy-wiring/review-1.json）。逐项核实：其High混淆星铁与原神，LoadPreferences早于verify且星铁必须IdentityVerified，缺发行包时保持禁止，并非绕过；原神NativeDaily单独固定安装校验，无需星铁ZIP。Low archivePath是既有兼容参数，不用于NativeDaily认证。Medium仅建议测试补断言，已补且通过；实现未因审查变动，不需第二轮。未以测试/模型审查代替游戏验收。

部署脚本 `scripts/Update-UnifiedInstall.ps1` 已在合成目录演练正常切换、程序切换后故障、数据库切换后故障（恢复精确旧库SHA）、快照后原数据变化拒绝，均符合预期。先持有档案mutex和公共桌面锁，拒绝活动进程/恢复/伴随文件，核对原库所有表与升级副本相同，逐文件校验完整备份，再移动程序并Replace数据库。保留安装目录和账号档案的恢复副本，不递归删除旧文件。

部署DeepSeek两轮，v4-pro/thinking disabled，证据 artifacts/deepseek/deployment/review-{1,2}.json。首轮Critical段落自身推翻其论据，建议“提前取消回滚标志”会破坏事务，拒绝；确认异常回滚本身失败时须明确汇总并保留原失败，已修正，并增加阶段记录及数据库后故障演练。复审无确认的数据丢失/常规回滚缺陷。其末尾外部进程绕过mutex直接写数据库的假设未复现，应用自身被同名mutex约束；突然断电和回滚期间外部占用不保证自动恢复，备份/日志供人工恢复。没有将有限静态审查称为整机安全认证。

AI真实合成业务验证：6次HTTP200、模型匹配，两阶段（有序三游确认卡、精确未知计划）通过，prepare1/submit0/query1，无真实账号/游戏；详见 C 交付记录。用户另行明确同意将现有本机密钥配置到正式AI助手，此配置将在完整数据备份之后进行，密钥不经聊天。

只读预检（artifacts/readonly-preflight/result.log）：复制档案的三游各2个勾选账号，6个SavedExecutionPlanReader请求均prepared；星铁ZIP Verified/安装IdentityVerified、ZZZ IdentityVerified、原神PendingValidation且无安装错误（仍待实机）。未调用提交/启动，未输出账号名称、标识或配置内容。正式保存的发行包路径存在。

最终包093aac默认/统一各1268/1268、升级副本启动通过；WPF烟测发现新GI接线下未核验时的Execution文案缺“未开放”。先以新断言复现失败，再修正提示；烟测也直接检查CanRunForReal，保留Report门槛，不以关键词替代准入。注册/描述3/3通过。接线任务由此使用最后第2轮DeepSeek（review-2.json），核实无实现阻断：RealExecutionPolicy由编译宏决定且只有getter，不是可变静态环境；rewardAcceptance指专用奖励验收模式，命名不是逻辑倒置；ConfigureHomeGenshinReward明确以_genshinDailyBatch is null设HomeRewardWired，两路径互斥；注册测试独立验证DI存在性，烟测验证未核验禁止，两者职责不同。此后仅重新测试/构建，不追加第三轮。093aac候选保留为烟测失败证据，不部署。

## 已安装结果（2026-11-14）

- 产品提交：`d40c0022907a5214368841e52941aa61d64112f8`。统一构建测试1269/1269，失败/跳过均0；最终WPF烟测通过，包括模拟队列3/3、紧急停止187ms、停滞AI关闭与401隔离。正式游戏未启动。
- 原目录 `E:/auto agent game/VIvian` 已更新。候选包460个文件逐个SHA256匹配；原快捷方式目标、参数、文件SHA256均未变，继续使用 `E:/auto agent game/启动 furina.lnk`。
- 完整切换前备份：`E:/auto agent game/项目归档/回滚版本/furina-vnext-20261114-d40c002`。`program` 为旧程序，`data` 为旧档案，`deployment.json` 状态为 `installed`。原数据库SHA256为 `12CDF3060751C435E9EFDCE6CC880FEB7FFD2A9E3D5D645D70AFE1499116C69B`。
- 正式库升至schema8；在AI配置后再次核对17张原表，行数及双向集合均一致，完整性检查ok。账号、勾选顺序及原任务配置未改动。检查证据：`artifacts/deploy-installed-data.json`。
- 部署曾因只读诊断留下的空WAL/SHM而在修改前拒绝；确认无应用进程、持锁后由SQLite正常checkpoint清理，原库SHA不变。升级副本同样处理后成功切换，没有直接删除数据库伴随文件。
- 从正式安装目录运行其已校验程序集，对正式库的新隔离副本执行启动检查，退出0，标记提交 `d40c0022907a`。初次外部核对误要求完整40位，而源码AppPaths.Commit明确只显示12位；纠正核对长度后匹配，无产品修改。证据：`artifacts/deploy-installed-startup.json`。没有自动打开正式窗口或启动游戏。
- 用户授权的内置AI配置已完成：官方DeepSeek地址、`deepseek-v4-pro`，现有密钥通过本机私有管道输入，使用应用自己的设置存储加密保存。15文件绑定校验通过，结果 `saved=true/readback_matches=true`；密钥未进入聊天、命令参数或报告。证据位于AI工作树 `artifacts/app-ai-config/configuration-result.json`。配置器两轮审查完成，最终仅重绑d40c002依赖哈希，配置与锁定代码未变。
- 安装ZIP SHA256：`9d504a70632583d5abf7919bc575bbc678bbac951cef3be45f9e56cc38330181`。文档收尾提交不改变产品构建身份。

更新说明放在安装目录 `本次更新说明.md`。真实星铁回归、绝区零双账号、原神单/多账号和混合队列交由用户自行测试；系统DPI、多屏、输入法、游戏焦点及视觉85/70评分仍未验收，不把自动化和模型审查当作这些项目通过。不得直接用旧程序打开新版库；回滚须先保留测试后的新档案，再成套恢复备份中的program与data。
