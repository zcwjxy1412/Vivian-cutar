# C：三游戏账号发现说明修正

2026-11-13；独立小任务，基于 C 的 v3 消费实现。公共注册与保存勾选准入由 I 负责。

- 复用现有工具：list_games → list_accounts(game) → 用游戏和 slot 辨认，目标或顺序有歧义时询问 → propose_run(items)。新 items 计划不要求先 read_configuration；旧单星铁五字段计划仍须读取配置与 revision。
- list_accounts 新增 saved_auto_run，保留 AutoRun，两者都来自已保存的数据库值。工具说明和系统提示明确它们不是界面未保存的选择，也不是执行许可。
- 继续使用白名单字段；不增加 GameAccount.Name、手机号、UID、登录资料。slot 用作安全账号称呼。不新增工具、公共接口或当前选择回调。

验证：三游戏参数化测试先因缺少 saved_auto_run 失败，添加字段后通过；测试覆盖真假保存状态、旧键兼容、游戏隔离、slot、完整字段白名单，以及脚本模型 HTTP 消息中不出现合成名称、脱敏/完整手机号、UID、区服哨兵。未注册任何配置 provider 仍能发现三游戏账号。

- Assistant 回归：86/86，零失败、零跳过。
- 全量 Windows 测试：1215/1215，零失败、零跳过。
- 日志：artifacts/tests-c-account-discovery/{assistant,full}.trx。
- git diff --check 通过。

DeepSeek 独立审查：1 轮，deepseek-v4-pro，thinking disabled，finish reason stop；无发布阻塞，无需实现修正或复审。请求/结果保存在 artifacts/review-c-account-discovery/{request1,response1}.json。核实其非阻塞提醒：双字段重复是有意兼容且有一致性测试；脚本模型验证传输数据，不能证明真实模型必定遵循歧义询问或执行能力判断。审查中“字段顺序”应准确理解为字段集合断言，并不承诺 JSON 属性顺序。

未进行真实模型或游戏验收，未访问正式账号数据或修改宿主准入。
