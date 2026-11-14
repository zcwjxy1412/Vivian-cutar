# C：实际模型多游提案验证与应用连接交付

核实日期：2026-11-14。总控审阅并亲自执行固定安全入口；C 仅只读校验已生成证据，没有重复请求供应商。探针引用 C 的 b935524（已由总控合入），不等同于整个新宿主或正式安装的实机验收。

## 实际模型证据

证据：`artifacts/multigame-probe/live-evidence.json`，SHA256 `FFAC47326E833DFBC4ACB5245DF4E2E0F919ED1B97D52FDC12841E69359CBD50`。该文件不纳入版本控制；只保留封闭字段，不保存原始模型正文、请求头或密钥。

- 模型请求为 `deepseek-v4-pro`；6 次请求全部 HTTP 200，响应模型名匹配均为 true。
- `ordered_multigame_plan_card`：通过，4 次请求。实际工具顺序为 list_games → 三次 list_accounts（genshin、starrail、zzz）→ propose_run。账号发现顺序与最终计划顺序独立；确认卡项目严格为 zzz → genshin → starrail，每游戏使用合成 slot 2。
- `unknown_plan_id_not_found`：通过，2 次请求。只调用 get_batch_progress 查询固定未知计划；模拟服务返回 null，工具返回未找到；模型最终回复包含“未找到”。没有最近批次回退或重发提案。
- prepare=1、submit=0、query=1。实际 AssistantSession/AssistantTools 处理往返并构造 ProposalCard；没有执行确认命令，没有打开窗口、启动游戏或使用真实账号数据。
- 计划任务均为 routine，资源策略均为 saved_account_settings。没有新增第九工具，也不要求新 items 计划先读取配置。

上述结论已通过同一封闭输出校验器复核。未覆盖用户在正式应用中的连接设置、真实账号准入、宿主持久化/执行、奖励、窗口显示以及多次模型调用的稳定性；模型正文没有保留，不声称做过完整自然语言逐句审阅。

## 安全入口与审查

入口：`artifacts/multigame-probe/run-fixed-multigame-probe.mjs`，SHA256 `5E539EF005DC80C1CB3F3C20BFB7D0B226AD72F9AF45130D9BA358B001BBD0B6`。

固定 manifest SHA256 `F68BCBA2C1C979A7468335CE2801ABA2D8D3763890C317015C2B22B099DAEA67`，核验 53 个源码/项目/程序集/运行依赖文件；只检查模式不读取凭据。密钥由既有安全读取器经私有管道捕获，再通过标准输入传入探针，不经过参数或环境变量。输出严格限字段和枚举，异常正文不输出；证据文件禁止覆盖。

DeepSeek 审查共 2 轮：首轮核实后补强失败路径 stderr 缓冲区清理、失败请求计数一致性和非完整计划结构拒绝；第二轮没有剩余阻塞。53 文件检查、15 类非法输出拒绝测试通过。审查结果在同目录 `review-launcher-round1.json` 和 `review-launcher-round2.json`。JS/.NET 不可变字符串不能保证即时清零；没有把缓冲区清理描述为所有内存副本清零。

## 应用设置实际位置与格式

依据 AssistantSettingsStore、App.xaml.cs、UnifiedProfilePreparation 及 AssistantViewModel 源码核实，集成 89b375d 的设置存储实现相同：

- 文件始终是当前运行数据目录下的 `assistant.json`，不是程序安装目录，也不是 Helper 的凭据文件。
- 正式 unified 安装默认位置为 `%LOCALAPPDATA%\VIvian-UserData\assistant.json`；当前用户解析为 `C:\Users\胡桃\AppData\Local\VIvian-UserData\assistant.json`。实机隔离入口指定数据目录时以该目录为准，不能拿正式目录的状态替代。
- JSON 字段为 BaseUrl、Model、ProtectedKey。前两项明文；ProtectedKey 是 UTF-8 密钥经 Windows DPAPI CurrentUser 加密后转 Base64，没有额外 entropy。保存先写 assistant.json.tmp 再替换正式文件。
- Load 会在应用进程内解密；文件缺失、格式/权限/解密错误等返回空设置。密文存在不代表当前 Windows 用户能解密，更不代表接口连通。不要通过调用 Load 做“只检查文件”的检查。
- 本轮只使用文件存在检查：上述正式位置 assistant.json **不存在**；没有读取内容、密文或解密。此结论仅表示该位置当时没有保存文件，不推断其他配置目录或正在运行进程的未保存草稿。

## 安全判断与用户连接步骤

安全判断分三层：文件存在只能说明有设置文件；重新打开同一数据目录的应用后观察 AI 状态，“未配置”表示应用没有完整可用参数，“未测试”表示参数完整但本次没有成功测试；用户主动点“测试连接”，看到“聊天和工具往返均已通过”，才证明应用自身连接成功。状态会受到未保存草稿影响，不能把修改中的页面状态当作已保存设置。

正式安装后，用户在 AI 助手右上角齿轮进入“AI 接口”，填写：

1. 接口地址：`https://api.deepseek.com`。
2. 模型名：`deepseek-v4-pro`。
3. 在密码框输入自己的密钥；不发到聊天中。已有同一地址且可解密的保存密钥可留空沿用，更换地址需要重新填写。
4. 点击“测试连接”，等待聊天和工具往返均通过；再点击“保存”。单独测试不会保存，单独保存也不会证明连通。两者都完成后再开始聊天。

Helper 已连接和此次合成探针已通过，均不能表述为应用已配置；探针没有写入应用 assistant.json。这里不替用户导入 Helper 密钥，也不要求用户向协作聊天提供密钥。运行计划仍须本机允许生成提案、宿主准入和用户确认，接口连接成功不等于允许启动游戏。
