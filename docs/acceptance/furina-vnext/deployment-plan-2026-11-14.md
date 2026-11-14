# 原安装更新执行记录

用户授权修复阻塞后更新 `E:/auto agent game/VIvian`，保留备份和账号数据，原快捷方式不变，真实游戏由用户自行测试。

1. 修复原神原生日常接线：旧 CLI 的两个 Verified 标志继续 false，统一 M5 使用已有 NativeDaily/恢复/持久计划入口，不以 UI 接线声称实机成功。
2. 隔离入口允许独立发行包核验，不触发自动发现、账号准备或上游配置恢复；正式入口保留原准备逻辑。
3. 三聊天补齐小窗口输入可用性、真实合成模型往返和执行残余核实。每个实现任务最多两轮 DeepSeek；历史两轮后最终补丁的审查限制不抹去。
4. 构建同一提交，完整统一测试、只读启动检查。原数据 schema7 不可直接交给 schema8 严格打开；先在副本升级、逐表核对原数据，部署时保留旧库/完整档案备份。
5. 持有原档案单实例和共同桌面锁，确认无活动任务，备份后切换安装及新数据库；任何失败恢复原程序和原库。保留快捷方式目标与参数。部署后核对产物哈希、数据完整性，提供回滚位置。不开启真实游戏。

初始基线 a1e05ed；接线测试在原条件下 1失败/1通过，确认统一M5缺少原生日常委托。部署与最终验证待填写。

接线修复定向36/36，补充reward仍保留恢复委托断言后注册用例2/2。DeepSeek首审1轮（v4-pro，thinking disabled，4117/1292 tokens；artifacts/deepseek/deploy-wiring/review-1.json）。逐项核实：其High混淆星铁与原神，LoadPreferences早于verify且星铁必须IdentityVerified，缺发行包时保持禁止，并非绕过；原神NativeDaily单独固定安装校验，无需星铁ZIP。Low archivePath是既有兼容参数，不用于NativeDaily认证。Medium仅建议测试补断言，已补且通过；实现未因审查变动，不需第二轮。未以测试/模型审查代替游戏验收。

部署脚本 `scripts/Update-UnifiedInstall.ps1` 已在合成目录演练正常切换、程序切换后故障、数据库切换后故障（恢复精确旧库SHA）、快照后原数据变化拒绝，均符合预期。先持有档案mutex和公共桌面锁，拒绝活动进程/恢复/伴随文件，核对原库所有表与升级副本相同，逐文件校验完整备份，再移动程序并Replace数据库。保留安装目录和账号档案的恢复副本，不递归删除旧文件。

部署DeepSeek两轮，v4-pro/thinking disabled，证据 artifacts/deepseek/deployment/review-{1,2}.json。首轮Critical段落自身推翻其论据，建议“提前取消回滚标志”会破坏事务，拒绝；确认异常回滚本身失败时须明确汇总并保留原失败，已修正，并增加阶段记录及数据库后故障演练。复审无确认的数据丢失/常规回滚缺陷。其末尾外部进程绕过mutex直接写数据库的假设未复现，应用自身被同名mutex约束；突然断电和回滚期间外部占用不保证自动恢复，备份/日志供人工恢复。没有将有限静态审查称为整机安全认证。

AI真实合成业务验证：6次HTTP200、模型匹配，两阶段（有序三游确认卡、精确未知计划）通过，prepare1/submit0/query1，无真实账号/游戏；详见 C 交付记录。用户另行明确同意将现有本机密钥配置到正式AI助手，此配置将在完整数据备份之后进行，密钥不经聊天。

只读预检（artifacts/readonly-preflight/result.log）：复制档案的三游各2个勾选账号，6个SavedExecutionPlanReader请求均prepared；星铁ZIP Verified/安装IdentityVerified、ZZZ IdentityVerified、原神PendingValidation且无安装错误（仍待实机）。未调用提交/启动，未输出账号名称、标识或配置内容。正式保存的发行包路径存在。
