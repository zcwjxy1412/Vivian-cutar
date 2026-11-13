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

## 集成冒烟发现后的修复与第2有效轮复审（最新）

总控在合流C/B后运行现有Build.ps1 -SmokeTest发现游戏管理及运行日志页10项文字对比度失败，首审未捕获此问题。因此先前“可交实机”的状态曾暂时退回待修复；不能仅凭首审或1157测试声称集成冒烟通过。

B原样重跑 scripts/Smoke.ps1 得到相同RED：C:/Temp/vivian-smoke-9302cc00070248d39990153da70c3069/contrast-failures.txt。已检查总控及本地screen-1.png、screen-3.png。根因是这两个页面直接使用透明根Grid，而其他普通页面的Page样式已有浅色背景；深色标题透到Stage/场景图，测得1.4–2.2:1。

产品增量5ac4aab9d7043621f6af0fe32ab9fe0df3feb83a：仅MainWindow.xaml两行，为游戏管理及日志页根Grid设置现有不透明Furina.PanelBrush（#F1F6FF）。不改文字颜色、对比度门槛、App注册/代码、脚本、布局尺寸、绑定或行为；背景覆盖发生在对应内容区域，不覆盖子控件独立警告条。

直接冒烟GREEN：C:/Temp/vivian-smoke-98c36f36940c43ef917d9dc5c17e68f0。随后在已提交5ac4aab执行完整 scripts/Build.ps1 -SmokeTest，退出0：

- 默认全量1157/1157，0失败/跳过，构建0警告/错误。
- 自包含发布版本0.4.0-dev+5ac4aab9d7043621f6af0fe32ab9fe0df3feb83a。
- 完整冒烟GREEN：C:/Temp/vivian-smoke-a3336e180e87431986a9933371542580；243文字对比度通过，107个非纯色背景未自动检查（不能称全界面对比度全覆盖）。模拟队列3/3、紧急停止196ms、关闭停止通过，最差UI响应32ms。
- ZIP SHA256：5de653f4c0ccd3099f5fc12988eed88044f69347d61809ad3930f201d01d50da。本工作树artifacts/VIvian-win-x64.zip只为开发验证包，没有替换正式安装。
- 此次运行现有隔离数据的模拟WPF冒烟；没有启动真实游戏。最新M5全量未重复跑，两行纯XAML不含条件构建逻辑，之前M5结果仍属于040408c，不能冒充最新提交证据。

第2有效轮/唯一复审：deepseek-v4-pro，thinking disabled，目标5ac4aab，累计B实际改动+最终两行差异+RED/GREEN和未改检查器上下文；18545 prompt/589 completion，finish_reason=stop、reasoning_tokens=0，约14秒返回。结论：两行修复有效、未发现具体回归，可在特定验收范围内放行。已核实源码、截图和脚本未修改，接受该结论；非纯色背景人工检查、真实DPI/多屏与游戏前台仍待总控，不能扩张为发布验收通过。有效首审+复审共2轮已用完，不再调用第3有效轮。

本轮原始请求/返回位于artifacts/tests-b/deepseek-second-valid-request.json与response.json；完整构建日志contrast-build-smoke.log；直接RED/GREEN日志contrast-red-smoke.log、contrast-green-smoke.log。下面附复审原文和哈希。
DeepSeekHelper review
Model: deepseek-v4-pro
Selection: explicit tool argument
Model profile: stronger reasoning, suitable for review, verification, and higher-risk decisions
Thinking: disabled
Thinking selection: explicit tool argument
Finish reason: stop
Reasoning tokens: 0
Tokens: prompt 18545, completion 589, total 19134
Cache: hit 0, miss 18545
Estimated cost: $0.008580

## 审核结论（中文）

**两行修复有效，已通过实际烟测，未发现具体回归；可在特定验收范围内放行。**

### 根因验证
- 烟测 RED 中十条失败全部为游戏管理页与运行日志页文字绘制在 `#FF101E4A`（Stage 背景）上。
- 两行 diff 分别给两个页面的根 `Grid` 增加 `Background="{StaticResource Furina.PanelBrush}"`，直接替换文本实际背景，与 RED 中测量的背景色和失败文本位置完全对应。
- GREEN 显示同一 `ContrastAudit` 对 243 个文本元素通过，未改动检查器、阈值或文本颜色——不是绕过检查。

### 具体回归风险检查
1. **无交互或绑定变更**：两行只增加 `Background` 属性，未覆盖任何依赖属性绑定；`Grid` 上无输入绑定、命中测试或模板改动。
2. **层叠遮挡**：两处修改的 `Grid` 内已有子控件（Star Rail/Genshin 配置面板、日志列表、模拟队列）都自带 `Panel`/`Surface` 背景，新背景只影响原先透出 Stage 的空隙区域。没有发现依赖透明背景的装饰元素被遮蔽。
3. **日志页 Danger 提示**：`RunLogTab` 的 `Background` 在外层 Grid，内部 `DangerBack` 条带带独立背景，未被覆盖。
4. **测试夹具**：新 `FurinaWindowLayoutTests` 覆盖宽/窄布局、输入锁定/重入、快速指令展开后发送按钮与聊天视口最小可读高度；对两行修复无冲突。
5. **DeepSeekHelper 修复**：`extra_body.thinking` 移至顶层 `thinking`，并补充 `finishReason`/`reasoningTokens`，逻辑独立于产品代码，不影响本次 UI 修复。

### 剩余不确定性（非阻塞）
- **107 个非纯色背景文本未做对比度检查**（烟测明示跳过）。其中可能包含动态图片/半透明叠加上的文字，需人工截图复核。
- **烟测是模拟渲染**，不等于真实多 DPI、多显示器、Windows 版本下的显示效果。
- 两处修复为不透明纯色背景，在**未来**主题切换为渐变或半透明背景时可能再次出现对比度问题；当前 `Furina.PanelBrush` 为不透明 SolidColorBrush，故本次成立。

**总体判断**：修复针对根因、最小化、可验证；允许放行，建议在真实桌面环境对游戏管理页与运行日志页做一次人工视觉抽查作为收官确认。
deepseek-second-valid-request.json: 8E1629B7F183C02AC20860733AB92293B5F65F18ECEB9B46288B5B53D584D7CC
deepseek-second-valid-response.json: 218078AF999CD6755B4F14F6ED9412527EF4EA198CC54D97E7A4B6A0C34E9564
