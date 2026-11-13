# B：DeepSeek 超时恢复与补审记录

日期：2026-11-13。用户明确要求排查超时、恢复后对最新提交补审，通过后交总控安排实机。

## 结论

恢复后第一次有效独立审查通过：deepseek-v4-pro 返回 `No blocking findings.`，finish_reason=stop，reasoning_tokens=0。审查目标为 edf6fdd86e9a0d099f72a9934887be629c9efbb8（产品代码040408c），包含累计B实际差异、最后快捷区修复及C/I必要上下文，也包含本地桥接修复实际差异。补审后没有改动产品代码，因此无需修复后第二轮。此前两次超时仍记录为无有效结果，不能追记通过。

## 诊断与修复

- 初始鉴权检查成功：/models及chat端点均可用；不能据此前推历史网络一定正常。
- 已确认的请求构造缺陷：Node OpenAI SDK原样序列化extra_body，服务端收到的是嵌套extra_body.thinking而不是根级thinking。日志记录disabled只是客户端意图，不证明服务端关闭了思考。
- 官方协议：根级thinking控制模式，未提供时默认启用；未指定max_tokens时思考模式默认64K，非思考模式8K。来源：https://api-docs.deepseek.com/api/create-chat-completion/
- 同一pro模型、同一极短提示、max_tokens=128的实测：原嵌套请求耗时3844ms、20个reasoning tokens；根级请求1404ms、0个reasoning tokens。只保存长度/用量，未输出密钥或推理正文。
- 修复本机 `.codex/local-mcp/deepseekhelper/scripts/server.mjs`：chat和auth两处thinking移至根级；结果增加finishReason和reasoningTokens，便于区分完整返回、截断与参数未生效。未扩大600秒超时、未换模型、未修改凭据。
- 修复后auth成功（completion=1）；实际请求源码经SDK mock序列化的enabled/disabled双模式测试通过。复核完整B改动的请求14771 tokens，在约11秒返回303 tokens，完整结束。综合证据表明错误开启思考是此次长耗时的重要原因；历史超时请求未返回服务端耗时，不能排除当时负载等并发因素。
- 本地工具备份：本工作树artifacts/tests-b/deepseek-server-before-thinking-fix.mjs。该修复属于本机工具，不随VIvian PR发布；上游更新可能覆盖，需要复测。可重复回归脚本：本工作树artifacts/tests-b/verify-deepseek-wire.mjs（无网络、无凭据），同时保存在launcher目录。

## 有效独立审查与核实

审查返回确认输入会话幂等、先禁用再释放、构造/被动激活不获取许可、SetCurrentValue保留绑定、快捷区限高及桥接参数修复；源码和已有测试与这些结论一致。

需限定审查措辞：离屏测试只检查消息视口、发送按钮边界与绑定/许可状态，没有实际鼠标点击或桌面激活，不据此宣称“实机可点”。绑定释放后重入已在离屏测试覆盖，真实HWND恢复仍待实机。桥接本次有可重复mock验证，但未接入上游自动CI。

剩余实机项：游戏前台/键盘输入法、实际Show/Activate/失活、Alt+F4及主窗关闭时序、DPI>100%、多屏/负坐标。状态组合未在窗口级全路径覆盖。当前定位仅主屏、锁定历史不可滚动、其他页面完整紧凑适配均仍是已披露限制。

产品验证沿用同一产品代码040408c的默认/M5各1157/1157，0失败/跳过；此次只修本机工具和审查记录，没有重跑无变更的产品全套测试。PR仍为草稿，未部署、未打开真实应用或启动游戏。交总控安排串行实机验收，不是发布授权。

## 本地证据

- deepseek-diagnosis.json：线缆层和同模型A/B探针。
- auth-diagnostic.json、auth-after-fix.json：修复前后鉴权。
- deepseek-recovered-review-request.json / response.json：完整实际送审包和原始返回。
- 上述均位于本工作树artifacts/tests-b；原始审查文本附下，哈希记录随后自动写入。
DeepSeekHelper review
Model: deepseek-v4-pro
Selection: explicit tool argument
Model profile: stronger reasoning, suitable for review, verification, and higher-risk decisions
Thinking: disabled
Thinking selection: explicit tool argument
Finish reason: stop
Reasoning tokens: 0
Tokens: prompt 14771, completion 303, total 15074
Cache: hit 0, miss 14771
Estimated cost: $0.006689

No blocking findings.

主要确认：
- 输入门生命周期正确：TryEnter 幂等；Dispose 先置 CanEdit=false 再释放租约，测试断言释放回调时 CanEdit 已为 false。
- AssistantWindow 构造不触发任何获取；无 Loaded/Activated focus handler，Alt-Tab/被动激活保持只读。
- LockInput 先用 SetCurrentValue 同步禁用 EditingSurface，再 Dispose，避免租约释放与 UI 启用的竞态。
- 快捷指令修复合理：Expander 内嵌 ScrollViewer 限高 36，测试覆盖展开态下聊天区与 Send 按钮可见/可点；360x460 最小尺寸验证通过。
- DeepSeek 修复正确：thinking 移至请求根级别，health check 与 chat 均一致；输出增加 finishReason/reasoningTokens 无破坏性。

剩余验证缺口（非阻塞）：
- 真实桌面键盘焦点/输入法切换、WPF 实际 HWND 激活、SetCurrentValue 后绑定恢复均未经 live acceptance。
- MainWindow 紧凑布局仅经离屏渲染验证，未验证 DPI>100%/多显示器/负坐标。
- AssistantInputBlocked 依赖 CanOperateWorkspace/CanStopAll/Queue/HasPendingConfigurationRestore，其组合状态未在窗口级集成测试全路径覆盖。
- 本地 bridge 修复未纳入自动回归测试。
deepseek-recovered-review-request.json: 6BC5F103D9748720D14871A45F3F32E0C91DFBF3AD79D43DD7F38D66BCD28219
deepseek-recovered-review-response.json: 88771D5FB4CDED1EEC2CB291877A4F89426FE8F61DE6B05F42055F7DB08E74A4
