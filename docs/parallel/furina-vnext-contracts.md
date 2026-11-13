# furina vNext 公共契约 v1

固定源基线 ce3ebeea79c507825e66a9e97097a6f80ede33cc。本提交冻结现有接口与首批消费边界，不修改运行协议或数据库。

## 执行与配置

- Core/Execution/Contracts.cs：IGameAdapter.PreflightAsync(RunContext, CancellationToken)、StartAsync(RunContext, IProcessSupervisor, CancellationToken)；IEngineRun 的 Events、WaitForVerdictAsync、RequestStopAsync、ReconcileAsync；暂停仅用 IPausableEngineRun.SetPausedAsync 并等待真实确认。
- Core/Engines/IAccountConfigurationProvider.cs：ReadAsync(target, ct)、ProposeAsync(target, revision, changes, ct)、ApplyAsync(proposal, stillAllowed, ct)。复用现有目标、提案和审计类型。
- RunPresentation 提供阶段、结果和清理文案；未知进度显示阶段，不生成百分比。Success/Failed/Cancelled/Indeterminate 与 Cleanup 分开。
- C/F 对新共享字段、幂等提交、多游戏计划、输入前安全失焦提出具体调用和失败用例；仅 I 修改公共 DTO/注册/迁移，发布契约 v2 后消费。未具备安全失焦能力时聊天输入不开放。

## B/C 现有绑定接口

B 消费 AssistantViewModel：Messages、Input、IsBusy、StatusLine、QuickCommands、SendCommand、CancelRequestCommand、QuickCommand、TestConnectionCommand、SaveSettingsCommand；复用 ChatItem 与 ProposalCard.ConfirmCommand。B 不修改 AssistantViewModel 或复制工具逻辑。C 保持兼容，新增绑定先登记。关闭聊天不取消游戏；停止调用现有本地控制链。

## A/B 主题 v1（由 A 实现，B 消费）

字典入口：src/VIvian.App/Themes/Furina/Theme.xaml；由 I 合入 App.xaml。资源前缀统一 Furina.。

- 画刷：StageBrush、PanelBrush、TextBrush、MutedTextBrush、AccentBrush、GoldBrush、DangerBrush。
- 尺寸：PanelCornerRadius（CornerRadius）、PanelPadding（Thickness）、BodyFontSize（Double）。
- 样式：PrimaryButtonStyle、SecondaryButtonStyle、PanelStyle、InputStyle、ProgressStyle、MessageBubbleStyle；各目标类型分别为 Button、Button、Border、TextBox、ProgressBar、Border。
- 资产：Assets/Furina/scene-background.png、furina-main.png、water-foreground.png、furina-avatar.png、game-genshin.png、game-hsr.png、game-zzz.png。
- ThemeAssets.cs 归 A，保留用户外置素材覆盖与缺图回退；不要把资源存在当作绘图已验收。无法准备某项素材时记录缺口，不以合成 UI 图充当分层素材。

视觉品牌 furina，助手 芙宁娜。1600×900 DIP 为视觉基准，低于1280 DIP紧凑布局，960×500 DIP 最小工作区。
