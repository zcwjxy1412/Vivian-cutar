# furina 角色应用图标与视觉修复

2026-11-14：按用户最新要求采用芙宁娜Q版头像，白发、蓝色礼帽和面部为主体。用户要求再换一张后，最终采用小礼帽/张嘴微笑版本；纯皇冠徽章和此前大礼帽版本不交付。

原始参考为既有 Assets/Furina/furina-avatar.png；原生图像工具编辑输出保存在本目录 icon-source.png。提示要求头部特写、紧凑蓝帽、白发、清晰蓝眼、透明背景、简化轮廓，适合小尺寸。生成结果是角色图标资产，不是软件运行截图。

Export-Icon.ps1仅做尺寸和格式转换，生成16/24/32/48/256五张PNG以及含同一五帧的32位ICO；未以代码重绘图像。各帧原样嵌入ICO，目录与PNG头尺寸已校验。16像素下五官细节有限，蓝帽/白发/脸轮廓仍可辨；真实Windows任务栏/快捷方式视觉待总控验收。

总控接入路径（仓库相对）：
- src/VIvian.App/Assets/Furina/furina-app.ico：ApplicationIcon使用此文件，含16/24/32/48/256帧。
- src/VIvian.App/Assets/Furina/furina-app.png：256×256透明PNG，窗口Icon同源。

窗口通过AppContext.BaseDirectory下Assets/Furina/furina-app.png加载并冻结ImageSource；不写死程序集名称。当前项目已有PNG复制规则，项目/namespace迁移后需继续保留相对路径。ApplicationIcon、快捷方式与发布打包由总控设置，本分支不改项目文件、不部署。

同批界面修复：三游横幅的图片、渐变和标题统一在按RenderSize计算的四角圆角几何内裁切；权限标签和值分为两列，值允许换行；发行包大面板改为默认收起的“安装核验与修复”，保留选择路径、核对命令和状态，后台核验不变。仅相关可见品牌文案统一furina，不迁移namespace。

验证与独立审查结果见提交交接记录；离屏截图不等于真实DPI、焦点或游戏执行验收。

## 本批验证与审查

- 默认与 M5DailyTestBuild=true 各2/2针对性测试通过，零失败/跳过。完整窗口离屏夹具遍历各页、三游子页与多尺寸/DPI，并增加256PNG加载、三横幅裁切容器、长管理员权限文本不交叠、修复入口默认收起和原核验命令/路径绑定断言。独立像素测试覆盖156/240宽往返与100/125/150/200%四角透明、中心不透明，并覆盖图标损坏/缺失不阻断加载。
- ICO目录5帧32位、尺寸和每帧PNG字节一致校验通过。已人工查看16/24/32/48/256图。16像素只可辨轮廓与色块，不声称眼睛等细节完整。
- 最终日志：artifacts/tests-b/brand-layout-final.log、brand-layout-m5.log。代表离屏截图：brand-final-default/与brand-final-m5/各4张（宽/窄首页、核验展开/收起底部）；完整夹具输出在visual/。小窗口内容需要滚动，不将单屏视口裁断称作不可达。
- 本批DeepSeek独立审查2/2轮。首轮后修复图标损坏异常防护，核验状态移出折叠内容保持可见。复审完整结束，明确“通过，无阻塞或非阻塞缺陷”。原始请求/返回：artifacts/tests-b/brand-review{1,2}-{request,response}.json。
- 首审“RenderSize旧一帧”意见已核实不成立：WPF UIElement Arrange先ArrangeCore更新RenderSize，再ensureClip调用GetLayoutClip，保留按实际渲染尺寸裁切。来源：[dotnet/wpf UIElement.cs](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/UIElement.cs)。当前仅支持均匀圆角，唯一调用为12。
- 未重跑全量业务测试、未Show/部署/启动游戏、未重建CourtPreview。真实系统图标、任务栏/快捷方式、DPI/焦点及完整实机验收交总控统一改名合流后安排。
