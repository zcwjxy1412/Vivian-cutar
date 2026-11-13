# B参考图还原与DPI：候选交付记录

## 状态与身份

本批从85977de开始，未沿用上一批视觉完成结论。B分支feat/furina-reference-dpi，产品代码ce9328530b651e3a33ab683447d94619a1ff15ae；后续文档提交不改变产品。A独立增量c01826f130878b60fcd3748d024280c0ebf874df，B消费为f8cc1de。

代码实现、本地验证和两轮DeepSeek代码审查完成；**参考图视觉评分尚未进行，不能标记总分>=85或单项>=70%通过**。真实Show/Smoke/DPI切换/多屏移除/游戏前台/输入法由Q与总控串行安排，本批没有执行这些操作或改正式安装。

## 实际比图与实现

先查看参考home.png、assistant.png，并对照总控q-live-e34c104/home.png、assistant-open-0.png。原版角色偏小且低、白卡片缺水光层次、标题牌/王冠简化、横幅无人像、圆按钮缺水纹、底部水纹只有12DIP、助手窗像普通表单。

- B新增FurinaChrome原生矢量装饰：王冠、折角金边标题牌、冰蓝双描边、顶部高光和执行按钮冠饰水环；没有把参考图改成UI或热点图。文本后保留不透明浅底。
- 首页角色放大上移，概览与游戏行更紧凑，真实最近运行/本次运行可见空间增加；恢复说明折叠但待恢复时自动展开。水纹提高到40DIP，装饰不命中；全局停止和AI入口保留。
- 1280/1440边界按中列实际预算用两列概览，>=640DIP中列才用四列；长游戏名称可换行，品牌/助手名称可截断并提供完整提示。
- A三张独立无文字人物横幅（派蒙、三月七、铃风格），使用已有canonical文件名；外置素材/GIF优先和加载逻辑未改。资产为生成的同人插画，不冒充官方素材。哈希/尺寸见manifest版本2。
- 助手窗重做冠饰顶部、头像/消息卡、快捷指令和一体化输入区。所有消息/提案/确认/状态仍真实绑定。原有显式许可、失活锁定及确认前同步释放保持。
- WindowWorkArea保持物理屏幕坐标，不全局除DPI原点；依窗口/owner最近monitor的work area拟合，窗口尺寸使用当前窗口DPI。SourceInitialized先恢复并排队，WM_DPICHANGED/显示器与工作区变化/拖动结束后排队恢复；不调用Show/Activate、不改Z序、不获取输入许可。Closed取消hook/事件。

## 测试与证据

- 1280概览：RED期望2实际4，随后GREEN。角色点击回归：真实视觉命中测试RED null，透明的角色栏内模板Border恢复命中后GREEN，放大装饰仍禁用命中。
- Tiny360x400长输入/快捷区：消息视口RED为0；内容高度<420时隐藏可选快捷区、缩短输入最大高度，GREEN保留至少22DIP消息视口和发送按钮。快捷功能仍可通过正常尺寸恢复。
- WindowWorkArea纯几何9例：100/125/150/200%比例、负原点、移除显示器、工作区缩小、混合DPI尺寸、owner左右屏边缘及幂等；最初stub7失败，最终9通过。
- 窗口内容/输入许可/C确认+几何针对性22/22。
- 最终产品ce93285默认全量1184/1184、M5全量1184/1184，0失败/跳过。日志reference-final-default.log、reference-final-m5.log，TRX同名。
- 实际Window.Content离屏：home1600x900、1280x800、1440x900、960x500；1920x1000像素预算对应100/125/150/200%四档逻辑大小；assistant460x720、360x460四DPI、360x400小工作区。另渲染7页并复用原ContrastAudit的背景解析、4.5阈值检查（仅替代未Show导致的IsVisible判定），未修改原审计器/脚本。
- 这些不是真实桌面截图。RenderTargetBitmap/SetRootDpi验证渲染与布局，不能证明系统原生DPI消息或IME安全。
- 全部25张PNG及哈希在artifacts/tests-b/visual/reference-evidence.json，产品commit=ce93285、Release M5配置；相同提交的首页与助手参考候选为home-content-1600x900.png、assistant-reference-460x720.png。图片中的空账号/空记录/长文本均为隔离测试数据，非真实运行回执。

## 独立审查与逐项核实

本新任务DeepSeek首审fa4b06e，最终复审ce93285（两轮额度已用完）；指定deepseek-v4-pro、thinking disabled，均stop/0reasoning。首审提出的问题不能直接当事实：

- B1声称fit==native提前返回导致错误屏幕：拒绝。代码在选屏与owner拟合之后比较；相等意味着已在目标最终矩形。新增左右1920原点owner测试验证包含和幂等；最终DeepSeek明确接受此裁定。
- H1/M1初次显示与DPI异步时序：记录真实验收待定，不声称缺少SourceInitialized订阅。补SourceInitialized即时Recover后再延迟校正；不接管WPF的建议矩形处理。原生首帧是否跳变仍需Q。
- H2每次渲染解析Geometry：接受成本意见，改为静态冻结几何；无需宣称实测性能故障。
- H3描边层次：原报告自身已澄清父OnRender在子内容后方，源码与离屏图无覆盖缺陷，不做无依据改动。
- M2初始1600中列只有64：算术不成立，当前为744；XAML先设Width1600，构造后四列断言通过。未引入虚假NaN回归。
- M3头像与关闭按钮重叠、M4关闭泄漏：未证实；固定头像尺寸/标题省略，Closed取消订阅与disposed护栏；补source.IsDisposed保护。
- L1空work area：补尺寸检查，避免原生异常矩形传入Fit抛错。L2未知Kind按皇冠回退不是故障。L3日期未更新：两批同为2026-11-13，日期正确。
- L4颜色分散：本批局部新palette保持既有公共主题键不变；共享主题进一步归并非阻塞后续项。
- T1增加owner边缘纯测试；T2原生Show验收归Q；T3没有实际超小标题高度触发；T4保留复用检查器，反射入口加Assert.NotNull，阈值实际引用ContrastAudit.MinimumRatio并非写死。
- 额外Codex只读复核查出的角色命中缺失已RED->GREEN；提示的tiny消息区为零也已RED->GREEN。该复核不替代DeepSeek。

最终DeepSeek：无阻塞性发现；同时明确原生多屏/DPI和独立视觉艺术评审未完成。原始请求/返回：reference-deepseek-first-request/response.json及reference-deepseek-final-request/response.json，均在artifacts/tests-b。原文及哈希单独保存同目录reference-review-evidence.md。

## Q交接顺序与未验项

1. 合流后由总控独占桌面运行现有Build/Smoke，保留对比度门槛。B不直接启动窗口。
2. 在同一候选提交采集实际主窗口与浮窗；比较参考图区域关系、角色/横幅、层次/密度、文字清晰度，独立给出85/70门槛结果；未评分前视觉不通过。
3. 测真实100/125/150/200%、混合DPI左右/上方负坐标屏、工作区任务栏变化/屏幕移除；确认窗口仍可见且被动恢复不激活/不抢输入。
4. 中文/IME、长消息/长账号名、反复显示/隐藏、确认许可交接、停止与关闭时序复测。跨重启位置持久化本批未新增；当前恢复针对会话内位置及首次owner显示器。
5. 非纯色背景视觉/对比度仍须人工检查，模型代码pass不能替代视觉gate。没有新增假计划入口、假成功/进度或附件按钮。

所有权：无MainViewModel改动，无App/项目/注册/DB/执行提交改动；I的ExecutionPlans partial可独立接入。
reference-deepseek-final-request.json: 7D9A7C1D69433AF28FFA3C72589127917536D7A785768274735816DD36D60C5C
reference-deepseek-first-request.json: 09599F5023D518DD178DDFD2678A35A0437295D04D594A6D11FE1327E92CE629
reference-deepseek-final-response.json: EB2227D277A9D9EAE3490CAA36A258E2B05D478825C920CBE8121705D4127A30
reference-deepseek-first-response.json: 96B922C48197448DFD4827600B8E0B8E51255DD670FFC6F670410E0059305F65
