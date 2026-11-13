# A 素材与主题状态

日期：2026-11-13（America/New_York）。任务 #61，关联总任务 #59。

- 分支：feat/furina-art-theme；工作树：C:\Users\胡桃\.codex\worktrees\furina-vnext-art\Vivian-source。
- SOURCE_BASE_SHA：ce3ebeea79c507825e66a9e97097a6f80ede33cc。
- BASE_SHA / 依赖公共 T0：8f857cf2cd16515d81f4c65f8e50b693374dccd4。
- 实现 HEAD：f6cb0af3f7f21b1a29077a0d8a3760e54404998e；状态补记以本文件最后提交为准。
- 草稿 PR：https://github.com/zcwjxy1412/Vivian-cutar/pull/71；base=integration/furina-vnext，已推送自己的分支。
- 状态：首批资产/主题可审阅；尚未集成、部署，也未开始 B。

## 已交付

1. 七张独立 PNG：剧场背景、透明坐姿角色/座椅、透明头像、透明水纹和三游戏场景横幅。图像工具生成，没有参考 UI 整图热区。尺寸、来源、SHA256 见 `src/VIvian.App/Assets/Furina/manifest.json` 和 README。
2. `Themes/Furina/Theme.xaml` 实现契约 v1 全部资源键与类型，显式样式不污染旧页面；附加错误/停止样式和可伸缩冠饰/水纹几何。真实 WPF 控件保持输入、进度、焦点与禁用语义。
3. ThemeAssets 保留旧外置文件名、GIF 顺序和仅有 character 时助手复用该图；支持标准分层名并回退程序目录 Assets/Furina。外置目录只读，旧默认标题在内存转为 furina，自定义标题保留。修复非法类型 theme.json 导致启动异常。
4. 外置素材清点只读：正式素材目录只有 character.png（带背景站姿）和 theme.json；未改正式素材、安装、库、账号或引擎配置。

## 验证证据

仅为当前命令指定 DOTNET_ROOT=E:\auto agent game\.dotnet 与 PATH；global.json 未改。

```powershell
dotnet test tests/VIvian.Tests/VIvian.Tests.csproj -c Release --filter FullyQualifiedName~ThemeAssetsTests --logger "trx;LogFileName=theme-red.trx" --results-directory artifacts/tests-a
dotnet test tests/VIvian.Tests/VIvian.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ThemeAssetsTests|FullyQualifiedName~FurinaThemeVisualTests" --logger "trx;LogFileName=theme-visual.trx" --results-directory artifacts/tests-a
dotnet test tests/VIvian.Tests/VIvian.Tests.csproj -c Release --no-restore --logger "trx;LogFileName=full-default.trx" --results-directory artifacts/tests-a
```

- RED：新增行为原实现 7 失败 / 2 通过；增加内置回退用例后 8 失败 / 2 通过，确认新名称/品牌/非法 JSON 类型/内置回退缺口。
- GREEN：ThemeAssetsTests 10/10；含编译主题加载与离屏渲染共 11/11。
- 默认全量：1125/1125，失败 0、跳过 0；约 69 秒。包含 WPF 项目编译。
- `artifacts/tests-a/visual/theme-{1600x900|960x500}-{100|125|150|200}.png` 共八张离屏主题组件样张；已目视检查两种尺寸 100% 输出，角色帽饰、双鞋、椅子轮廓完整，头像使用高质量缩放。样张明确标注非产品界面验收。小尺寸以滚动容器承载内容。
- 未启动 Window、未争用前台；未启动任何真实游戏。

## I 需要的最小公共改动

完整片段与 B 用法见 `src/VIvian.App/Assets/Furina/README.md`：

- 在 VIvian.App.csproj 将 `Assets\Furina\*.png` 注册为 Content，并设 CopyToOutputDirectory/CopyToPublishDirectory=PreserveNewest。
- App.xaml 将现有资源完整包入 ResourceDictionary，在 MergedDictionaries 加入 `Themes/Furina/Theme.xaml`。XAML 已由默认 Page glob 编译，不重复添加 Page。
- A 不触碰上述公共文件。注册未落地之前，正常程序输出目录没有内置 PNG，现有外置图/矢量回退仍工作；不能宣称用户已看到新主题。
- B 消费已有 CharacterArt、AssistantArt、EngineCard.Banner、AssistantViewModel.Avatar 和 Theme.Background；新加 Theme.WaterForeground。装饰 IsHitTestVisible=false，图像 HighQuality 缩放。

## 未验证 / 阻塞

- DeepSeekHelper `deepseek_review` 工具不存在：blocked，0 轮；没有伪称通过或使用其他模型替代其结果。
- 实际产品 App.xaml 合入、普通/自包含发布 PNG 打包、B 主界面及浮窗构图尚未执行。
- 系统 100/125/150/200% DPI 切换、多屏、真实键盘焦点、悬停/按下动态效果及真实不定进度动画需总控串行安排；本次仅离屏像素密度输出，不冒充这些验收。
- 主角色和头像有 AI 生成细节，与参考图不是逐像素复刻；用户现有外置站姿仍优先覆盖新坐姿。
- 沿用现有仅文件存在/大小的图片加载检查，非空损坏图片或系统不支持的 WEBP 解码不新增修复。

## 下一步

首批已复核、提交/推送并创建草稿 PR #71。总控处理最小注册、合流 A 后，本聊天才进入 B；不部署、不合并 main。

## 首批复核与决策

- 额外 Codex 只读复核：无 Critical/Important；仅作代码质量检查，不替代 DeepSeek。
- Minor（延后）：FurinaThemeVisualTests 的 STA 线程默认前台线程；极端渲染卡死时，45 秒 Join 超时会报失败但可能仍拖住测试进程。以后可设置 IsBackground=true；正常已通过路径不受影响。
- Ruling：注册及普通/自包含打包交 I，最终主/AI 窗口布局、长文案可达性与恢复交 B/Q；未在 A 跨所有权修改。风险是目前不是可直接部署的最终 UI。
- Ruling：系统 DPI、多屏、键盘焦点、悬停/按下、不定进度动画及 AI/游戏安全失焦留待串行实机验收；离屏样张只证明组件渲染。风险是交互/OS 行为尚不确定。
- Ruling：重复请求幂等、恢复、日志与账号/RunId 归属、误停隔离及 AI 断网/假完成时执行链继续等由 C/D/E/F/Q 验证；A 不碰执行链。风险是本 PR 的测试不能证明这些行为。
- Ruling：保留旧的损坏非空图片/WEBP 解码边界；新默认 PNG 已实际解码，但不扩展外置图片校验。风险是用户损坏素材仍可能显示失败。
- Ruling：四种输出密度已生成，目视检查两个尺寸 100% 样张；没有参考图相似度评分。图像生成过程在本聊天的工具调用有记录，复核者仅验证文件/哈希而未独立认证生成过程。风险是最终像素级视觉与美术偏好仍待 B/Q/用户验收。


最终离屏图 SHA256（组件证据，可由专属测试重建）：
- theme-1600x900-100.png：55eb0ec3ba1b46153fa274a89a3092dacf4733a54a35c4c8186a4682eed4267f。
- theme-960x500-100.png：8b2fc8aa2714eeb2626960c1a7ed484fe942ffebadabd250aba3029f09fc3681。
- 对比例图只在 artifacts 下，不打包进产品。最终样张水纹按原比例限制在 24 DIP 装饰带，不覆盖控件；调整后针对性验证仍为 11/11。
