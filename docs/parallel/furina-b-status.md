# B 主界面与浮窗：首批实现与绑定映射

日期：2026-11-13（America/New_York），任务 #62，关联 #59。

- 工作树：C:\Users\胡桃\.codex\worktrees\furina-vnext-ui\Vivian-source。
- 分支：feat/furina-wpf-ui。
- SOURCE_BASE_SHA：ce3ebeea79c507825e66a9e97097a6f80ede33cc。
- 当前产品基线 / T0：8f857cf2cd16515d81f4c65f8e50b693374dccd4。
- A/I 已接收并合入 d8fd96b；C 的事件提交 e1a0067 也已合入。A 首批已由总控完成 DeepSeek 首审，B 不重复。
- 当前范围：已实现首批首页、浮窗、输入许可生命周期与 C 确认前同步释放；本地布局提交7685cd9，交接补丁待记录新提交。准备草稿PR，禁止部署。
- 依赖：已消费 v2 的 AssistantInputGate 与 ExclusiveActionStarting。下方布局/入口清单是 T0 只读审计事实，最新实现及验证见文末。

## 已固定的行为

1. 保留 WPF、现有导航、MainViewModel、AssistantViewModel 和独立 AssistantWindow；打开 AI 不增加主窗口列，不挤压主页。
2. 外置角色继续优先；正式用户现有站姿素材可以覆盖内置坐姿。没有外置素材的隔离预览包才使用内置新图，不修改正式素材目录。
3. 安全输入 v2 首版只在**桌面空闲且无待恢复状态**时获得许可。运行中拒绝；暂停请求或暂停回执都不能作为放行输入的依据。
4. 不在 Loaded、收到消息或任务通知时无条件 Focus/Activate。只有明确用户入口且安全许可已取得后，才考虑激活/聚焦。
5. 关闭助手仅隐藏窗口并取消模型请求，不停止游戏；浮窗另提供直接调用本地 StopAll 的明确停止入口，不依赖模型在线。

## 布局映射

| 区域 | 当前实现事实 | B 接入方向（收到合流 SHA 后） |
|---|---|---|
| 窗口 | MainWindow.xaml:3，1460×900，最小1180×720；构造时按 SystemParameters.WorkArea 缩小 | 1600×900 DIP 视觉基准，960×500 最小；小尺寸需要布局重排，不能只降低 MinWidth |
| 导航 | Navigation TabControl 自定义左侧模板，固定214 DIP | 保留已有 TabItem/命名入口；紧凑模式收窄导航，用 ToolTip/可访问名称保留含义 |
| 主页 | HomeTab 三列固定320 / * / 292，外加导航和边距 | 正常模式保持角色—工作区—控制区关系；低于1280进入紧凑布局，缩减/隐藏纯装饰角色，工作内容滚动或重排，停止入口始终可达 |
| 背景 | Theme.Background Image + 旧渐变/波纹 | 消费 A 背景和水纹，装饰 IsHitTestVisible=false，保持比例并使用 HighQuality 缩放；正文不透明面板 |
| 角色 | CharacterArt，Art DataTemplate，外层 Button 打开助手 | 保留 ArtSlot/GIF/缺图回退；明确入口按钮承接交互，图像自身不承担透明区域热点 |
| 今日概览 | UniformGrid 4列 | 四项均保留真实绑定；紧凑下改为2列，不把缺失数据补成参考图数字 |
| 游戏卡 | Engines ItemsControl，横幅200 / * / Auto | Banner 使用 A 三张独立图；原有名称文字只在 FallbackVisibility 显示，需改为有图仍显示真实 Name/Tagline，并加深色文字底 |
| 最近运行/进度 | 3*:2*；最近记录及 Queue.Current/Status、BatchStatus | 长中文换行和空态；无可计算总量时显示阶段/不定进度，不编百分比 |
| 控制/系统区 | 独立右侧 ScrollViewer，OneClick/暂停/停止/恢复 | 复用实际命令与能力；停止和恢复不被装饰或滚动末端隐藏 |
| AI浮窗 | 独立440×680、最小360×460，无标题栏；Owner=主窗，不参与主布局 | 共享主题浅蓝圆角面板；限定到有效屏幕工作区，恢复越界位置；不全局Topmost；宽度与高度变化不压缩主窗口 |
| AI设置/消息/输入 | 自动行高设置区 + *消息 + 自动快捷/输入行；气泡MaxWidth320 | 长消息换行，设置展开时仍可达输入/停止；快捷项可换行；小窗口必须有滚动约束，不能让固定区域挤没消息或输入 |

当前 960 DIP 下，导航214+角色320+控制292+主页边距16 已占842 DIP，中间不足118 DIP（尚未扣内边距），因此必须有紧凑布局；离屏 A 组件图不证明这套主窗口已合格。

## 现有真实绑定与入口

| 视图内容 | 现有绑定/调用 | 注意事项 |
|---|---|---|
| 品牌/角色/头像 | Theme.Title、CharacterArt、AssistantArt；助手 Avatar/Name | 保留外置覆盖，不在 B 重写 ThemeAssets；新 Theme.WaterForeground 等待 A 合流 |
| 今日概览 | TodayText、RunnableText、SelectedAccountsText、EstimateText、RunStateText | 现有含义分别为完整日常可运行数量、选中账号、估时、状态；不能改标签后仍当待执行任务数 |
| 游戏卡 | Engines；Name、Tagline、Banner、ShortVersion、StateBrush、RunHint、Chips | Image 是插画，名称/版本/状态来自控件；版本未知保留实际提示 |
| 单游戏运行 | PlayGameCardCommand + Id；CanPlayFromHome、HomePlayHint | 使用现有命令守卫与禁用原因，不新建执行路径 |
| 游戏设置 | OpenGameSettings(sender.Tag=GameId) | 保留切页与各游戏配置 ViewModel |
| 一键日常 | OneClickCommand；CanOperateWorkspace | 这个标志仅排除 RewardAcceptanceScope，不表示桌面空闲；不得拿它作为聊天安全输入许可 |
| 暂停/停止 | Queue.PauseCommand / CanPause / PauseButtonText；StopAllCommand / CanStopAll | 不伪造已暂停；主窗口 StopAll 覆盖批次、队列与登录验收；浮窗停止需消费同一个本地入口 |
| 恢复 | Queue.HasUnresolved / AcknowledgeCommand / CanAcknowledge；RecoverDesktopCommand；RestoreConfigurationCommand / PendingRestoreVisibility | 保留真实恢复确认文案；不能隐藏未清理状态 |
| 最近记录 | RecentRuns；Game、Result、Succeeded、When、Duration | 列表空时保留“还没有真实运行记录”，不制造成功行 |
| 本次运行 | Queue.Current、Queue.Status、BatchStatus | 现有 Progress 属性不作为默认契约；收到真实投影后再绑定；禁止用时间估算假进度 |
| 系统状态 | RunStateText、ConfigSyncText、InstalledText、PrivilegeText | 当前 RunStateText 文本不等于 v2 原子许可，尤其不能据此启用输入 |
| 助手消息 | Messages / ChatItem.Text、FromUser、Align、AvatarVisibility、Proposals | B 只消费，C 维护行为；新消息滚动不 Focus/Activate |
| 提案卡 | Title、TargetText、Lines、State、ButtonText、ConfirmCommand、CanConfirm | 保留可审阅确认卡，不能将模型文字当执行回执 |
| 输入/发送 | Input、SendCommand、IsBusy、CancelRequestCommand、QuickCommands、QuickCommand | 输入框、Enter快捷键、快捷指令、设置键盘输入都要纳入安全许可，不只禁用发送按钮 |
| AI设置 | BaseUrl、Model、NewApiKey、SettingsVisibility、SettingsStatus、SaveSettingsCommand、TestConnectionCommand、ToggleSettingsCommand | 密钥继续 PasswordBox/KeyChanged；不复制配置/连接逻辑 |

## 焦点与关闭的已核实入口

- `AssistantWindow.xaml.cs:18`：Loaded => InputBox.Focus，必须删除无条件行为，改为许可后的明确入口动作。
- `MainWindow.xaml.cs:119–130`：OpenAssistant 仅看 CanOperateWorkspace，创建 Owner、定位，然后 Show/Activate；这是 v2 许可应先于显示激活/启用输入接入的位置。
- `AssistantWindow.xaml.cs:14–16`：消息新增仅 Dispatcher.BeginInvoke(ChatScroll.ScrollToEnd)，目前没有消息自动 Focus；保持此边界。
- `AssistantWindow.xaml.cs:19–20,32`：隐藏/Closed 调用 CancelRequest；关闭按钮 Hide；没有停止游戏调用。保留，但还须验证系统关闭/Alt+F4路径是否按隐藏保留会话及下次重新打开安全。
- `MainWindow.xaml.cs:24` 的主窗口 Closing 停止逻辑属于退出整个程序，与关闭聊天不同，不能混为一谈。
- AssistantWindow 当前没有独立本地 StopAll 绑定；可在 B 拥有的窗口交互中消费 MainViewModel 的 StopAllCommand/CanStopAll，具体连接方式待 v2/集成代码确定，不改 C 的 AssistantViewModel 所有权。

## 等待 v2 的具体消费问题

公共接口名称/签名尚未发布，本文件不擅自声明已有 API。接入至少需要从契约确认：

1. 用户主动打开/切入聊天时如何取得许可，以及如何区分运行中、恢复待确认、空闲。
2. 许可生效期间或聊天已打开后，新任务启动/恢复状态改变如何撤销输入与防止先激活后拒绝；不能只在打开时读一次布尔值。
3. 显式用户入口的激活、文本编辑、设置中的地址/模型/密钥输入如何统一门控；窗口打开可以只读，不应借暂停回执绕过禁用。
4. 隐藏/关闭/失活时许可释放方式；本地停止永远不需模型许可，关闭请求取消不触发 StopAll。

## 后续验证清单（尚未执行）

- 外置素材测试：只有旧 character 与 theme.json 时仍显示用户素材；无外置目录的隔离预览使用内置七层，正常输出/发布目录有资源。
- 布局：1600×900、1280边界、960×500；100/125/150/200%系统DPI与长中文/错误/长消息。截图绑定真实构建，样例数据显式标记。
- 几何：打开/关闭AI前后主窗口及关键区域边界不变；AI越界/多屏工作区恢复。
- 安全：空闲且无恢复才能取得许可；运行中、暂停已确认但仍占桌面、待恢复全部拒绝；Loaded/新消息不抢焦点；许可撤销后输入不可用。
- 控制：关闭AI只取消模型请求且保留会话；停止走本地 StopAll；快速重复点击不额外启动业务动作；真实能力不支持暂停时按钮不声称已暂停。
- 自动验证使用独立测试数据；需要 Window.Show/真实焦点/游戏的场景交总控串行安排，不在本工作聊天争用桌面。
- 根 App.xaml.cs 已有 SmokeAssistantIsolationAsync（约451行）；它归 I，B 提供追加验证需求或专属测试，不擅自修改共享启动/冒烟入口。

## 当前验证/审查状态

本次只读源码与文档记录，未运行产品、未修改视图、未执行新的测试，也不复用 A 的测试结论冒称 B 通过。工作树起点干净、分支已核对。DeepSeek 仍未见可用工具；B 实现尚未开始，独立审查未执行。

## B 实现进行中（候选 d8fd96b 之后）

已 merge 总控指定 d8fd96b；首页/AI 浮窗正在本工作树实现，尚未提交产品修改。已接入 AssistantInputGate 与窗口许可生命周期；运行/恢复中拒绝，默认只读，失活/隐藏/关闭禁用并释放，恢复状态改变也立即撤销。页脚固定 AI/StopAll；小于1280收起装饰角色并压缩导航。

已跑默认全量1145/1145；新增4个输入会话/真实窗口内容离屏测试（曾捕获并修复初始 IsEnabled 绑定未解析默认启用、恢复记录出现后未撤销）。恢复撤销修复后针对性4/4、M5全量1145/1145通过。没有 Show/Activate 真实窗口或启动游戏；离屏截图见 artifacts/tests-b/visual。

当前 C 分支还没有 ExclusiveActionStarting 事件，B 尚未引用不存在事件。待 C 提交/总控给出 SHA 后接入同步释放并验证提案确认闭环；本批不能声称该交接已完成。已通过本机MCP桥接提交 B 自身 DeepSeek 首审（deepseek-v4-pro、thinking=disabled），正在等待结果，不重复审 A 首批。多屏定位当前仅限主屏工作区，真实DPI/焦点仍待总控串行安排。


验证证据文件：artifacts/tests-b/input-red.log（初始2失败/1通过）、input-green.log（3/3）、windows-first.log（初始绑定失败）、windows-second.log（修复后4/4）、recovery-red.log（恢复出现未撤销）、recovery-green.log（修复后4/4）、full-default.trx（1145/1145）、full-m5.trx（1145/1145）。最新离屏PNG为M5测试写出的真实窗口内容，未执行 Window.Show/Activate，不是实际桌面截图；待提交后会重建带提交身份的证据。

当前明确限制：系统DPI/多屏/实际游戏前台焦点、Alt+F4及主窗关闭时序需要总控串行实机；首次定位暂约束到主屏工作区，不声称多屏恢复完成。聊天锁定时消息区也禁用交互；需要阅读更早内容需空闲时显式进入。完整功能页紧凑适配未在本垂直切片全部验收。首批候选不部署。

### C 交接已接通（首审等待期间的新进展）

已合入 C 提交 e1a0067de0c3566153903d8ee7cee67e1b176fbe。AssistantWindow 订阅 ExclusiveActionStarting，并同步禁用输入/释放许可；Closed 取消订阅。实际 UI + C 确认路径测试先失败（提交时输入仍启用），接通后13/13通过；重复确认仅提交一次，模拟宿主接收前已经观察到 InputBox.IsEnabled=false、许可释放一次。没有引用不存在接口，也没有绕过 C/宿主自身准入。

首批布局本地提交7685cd9；当前新增交接仍待最终审查/提交。最终默认/M5全量正在执行；DeepSeek第一轮请求是交接事件到达前的B差异，复审将包含已接通事件与必要C方法上下文。


### 首批最终本地验证

- 默认全量1157/1157，M5全量1157/1157，0失败/跳过，含C事件增量。
- 最终绑定/输入/确认针对性13/13。补充检查960/1600布局停止按钮在视口内；全局底栏显示Status，拒绝聊天原因不只在首页可见。
- 证据：artifacts/tests-b/full-default-final.trx、full-m5-final.trx、confirmation-final.log；截图由真实Window.Content离屏渲染，最新为默认构建。实际桌面窗口、系统DPI、游戏前台与多屏仍未验收。
- DeepSeek第1轮仍等待提供方返回，未标通过；本轮请求之后接入的C交接与底栏调整会纳入第2轮最终差异。
- 当前工作只修改B视图、MainViewModel、输入会话及专属测试；公共注册与C实现均来自合入提交，没有越权改写。

### 审查与交付进展（以本节为最新）

- 当前产品提交53086374c95b4c7823193f263257015e9ebbab11已推送；草稿PR https://github.com/zcwjxy1412/Vivian-cutar/pull/74，base integration/furina-vnext。
- DeepSeek第1次调用在600秒后MCP超时（-32001），没有有效审查结果，不记通过；第2次/最后一次调用已提交最终B差异及C交接上下文，正在等待。两次均使用指定本机DPAPI桥接、deepseek-v4-pro、thinking=disabled；未读取/输出密钥。
- 提交后针对性验证13/13：targeted-5308637.trx。artifacts/tests-b/visual/evidence.json记录提交、默认Release配置与五张离屏PNG哈希；不是桌面截图或实机验收。
- 额外Codex只读质量复核正在进行，不替代DeepSeek。

### B 最终交付状态（覆盖上方进行中记录）

- 最新产品提交040408c（fix(ui): preserve message space with expanded shortcuts）已推送到PR74。
- 额外Codex只读复核发现1项Important：最小浮窗长输入+展开快捷指令把消息区挤空。真实.NET 10窗口内容回归复现失败（quick-viewport-red.log），快捷区限高并独立滚动后通过（quick-viewport-green.log）；同时检查至少一行消息视口和发送按钮可见。没有发现Critical。1280宽概览拥挤仅为未验证风险，不记确定缺陷。
- DeepSeek两次调用均在600秒MCP超时，错误码-32001，有效审查结果0。第2次请求针对5308637；随后040408c布局修复未获DeepSeek审查。已达本任务承诺的最后一次调用，不再请求第三次；独立审查阻塞/未通过，草稿不可据此认定可发布。
- 修复后默认全量1157/1157，0失败/跳过（full-default-shortcuts.trx）；M5修复后全量1157/1157，0失败/跳过（full-m5-shortcuts.trx）。
- 本次只操作独立B工作树和草稿PR；未部署、未启动游戏、未显示/激活桌面窗口。实际DPI/焦点/多屏、Alt+F4和关闭时序、其他功能页完整紧凑适配仍待后续验收。主屏定位及锁定历史不能滚动的取舍不变。

### 2026-11-13 用户授权恢复后补审（最新状态）

DeepSeek独立审查阻塞已解除。确认本机桥接将thinking错误嵌套进extra_body，导致disabled未生效；同模型对照已复现，修复根级参数并验证后，针对edf6fdd（产品040408c）完整累计改动的首次有效补审约11秒返回 `No blocking findings.`、stop、0 reasoning tokens。无新增产品修改，无需修复复审。此前两次600秒超时不追记为通过。

默认/M5产品验证仍各1157/1157。详细诊断、独立审查原文和核实边界见 [furina-b-deepseek-recovery.md](furina-b-deepseek-recovery.md)。此后提交仅增加审查记录，产品代码与已审目标一致。交总控串行安排实机；PR保持草稿，未部署/未启动游戏。

### 集成对比度修复及复审交付（覆盖此前状态）

总控集成冒烟发现10项对比度失败后，B在原有Smoke.ps1复现RED，查明游戏/日志页透明根Grid导致深字落深底。增量5ac4aab仅给两页根Grid设置Furina.PanelBrush，不改App/脚本/阈值/文字颜色。已检查修复前后截图。

在5ac4aab执行完整Build.ps1 -SmokeTest退出0：默认1157/1157，自包含发布、模拟WPF冒烟通过，243文字对比度通过；107非纯色背景仍未自动检查。最终证据C:/Temp/vivian-smoke-a3336e180e87431986a9933371542580，版本0.4.0-dev+5ac4aab9d7043621f6af0fe32ab9fe0df3feb83a。开发验证包未安装，未启动真实游戏。

DeepSeek第2有效轮复审通过（stop、0reasoning tokens），无具体回归；有效2轮额度已用完。完整原文、源码核实和证据哈希见furina-b-deepseek-recovery.md。交总控合流后重新执行集成检查，再串行安排真实DPI/多屏/焦点/输入法及关闭时序验收；不能把模拟冒烟替代实机。此次文档提交不改变已审产品代码。
