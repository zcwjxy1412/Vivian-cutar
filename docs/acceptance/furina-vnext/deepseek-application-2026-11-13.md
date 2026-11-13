# 应用客户端真实 DeepSeek 验证

2026-11-13 08:32:33–08:32:44（America/New_York）。复用现有 ChatCompletionsClient 与 AssistantSession，在 C 独立工作树的临时宿主进行；不是 DeepSeekHelper 鉴权，也不是另一套模型客户端。

## 实际结果

固定官方端点 `https://api.deepseek.com`，请求模型 deepseek-v4-pro；5次响应 HTTP200，原始响应 model 字段均为 deepseek-v4-pro（供应商报告的标识，不是权重身份鉴定）。应用代码基线为 e1a0067，后已合入 e34c104；本次不改变应用请求参数，没有额外注入 thinking 或 tool_choice。

| 场景 | 结果 | 证据 |
|---|---|---|
| 应用聊天连接 | passed，约3.85秒 | finish_reason=stop |
| TestToolRoundTripAsync | passed，约3.93秒 | vivian_probe，tool_calls → 同 call_id 的随机合成值返回 → stop；客户端验证读回一致 |
| AssistantSession.AskAsync | passed，约3.07秒 | list_games，tool_calls → 合成游戏状态标记 → stop；回复含本次随机标记、没有配置/运行提案 |

证据文件：C 工作树 `artifacts/supplier-acceptance/evidence.json`，SHA256 `BDEFFC902F051A822881F3833F81D845FA0E5879A569E531D662AC722982FF12`。输出只含模型标识、结束原因、调用 ID 和合成结果；没有请求头、密钥或真实账号配置。所有调用保持现有20秒连接/往返及90秒 Session 超时限制。不能据此推断其他模型、网络条件、图像输入、真实配置写回或游戏执行通过。

## 安全启动与核实

原安全启动入口只服务 DeepSeekHelper。总控新增本机固定目标 `run-fixed-application-probe.mjs`，位于 `.codex/tools/deepseekhelper-launcher`；它不是可传任意命令的通用启动器。先校验下述源码与 DLL 哈希，再通过既有 DPAPI 私管道把密钥仅放进固定 dotnet 子进程环境；不通过参数、聊天或文件传明文，子进程读取后清除环境变量，不写正式应用配置。父进程结束后清零缓冲并移除环境副本；托管不可变字符串的内存残留是已知平台边界。

- Probe.dll SHA256：0B645059519AEF3BFB9C1930159AB6925FDD50DABA33F4B2D0D6F1519AD7E92D
- Program.cs SHA256：3043EDF80F94547C44A42F941D11D3C3F3297D0543A7C4F6F7ACF275F940E8B5
- 解密子进程限制15秒/16KiB；验证子进程限制180秒/1MiB。输出必须是固定三阶段的JSON且状态只允许passed/failed，状态captured只表示采集成功，是否通过还需逐项核对。
- 实际密钥只供本次合成验证使用；无生产设置保存、账户读取或游戏动作。原应用客户端的 Authorization 头是密钥的发送位置，消息体与工具 fixture 不包含密钥。禁止重定向。

此新固定启动器完成 DeepSeek 有效首审+复审2轮。首审确认需要限制解密等待、拒绝多余参数并增强证据结构检查；已修复。Node语法、无凭据check-only、非法参数退出1均已验证。复审未发现安全关键回归；以下建议经核实不构成剩余阻断：

- 1MiB 输出上限会拒绝过大证据并返回失败，不截断后冒称成功；本文件明确该边界。
- 不能把精确字符串检查说成任意混淆密钥检测；根本边界是已核对的固定 Probe/应用源代码不把密钥放入模型上下文、工具结果或输出。密钥不向模型提供，不添加没有依据的密钥重建逻辑。
- check-only 仅验证哈希，不宣称运行时或鉴权已验证；实际运行另有上述三阶段证据。
- 错误只输出固定消息，刻意不回显原始异常/子进程输出。若后续失败，另用无敏感信息的检查定位，不放宽日志策略。

审查请求/原文：总控 `artifacts/deepseek/app-probe-round{1,2}-{request,response}.json`。本机启动器不随产品 PR/验证包发布。

## Helper 超时修复的准确范围

B 发现原 Helper 将 Node SDK 的 thinking 参数嵌入 extra_body，已改为请求根级，真实对照和无网络序列化检查通过。原日志中的 disabled 是客户端意图，不能解释为当时服务端没有思考。修复后实际审查回复可记录 finish_reason=stop 与 reasoning_tokens=0。用户不需再提供密钥；新桥接进程使用修复，已运行的旧进程须在下一次重启后才生效。完整 B 首审及对比度修复复审记录见 `docs/parallel/furina-b-deepseek-recovery.md`。
