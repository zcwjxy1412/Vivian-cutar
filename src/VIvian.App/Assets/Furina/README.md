# Furina 分层资产清单与接入

契约：`docs/parallel/furina-vnext-contracts.md` 的 A/B v1。生成日期：2026-11-13。

所有 PNG 均由本轮原生图像工具生成；角色以任务书的 `references/home.png` 为构图与身份参考，头像以本轮独立角色为参考。没有从整张参考界面裁出面板或添加点击热点。游戏横幅是主题场景插画，不是官方游戏 Logo；名称、版本、状态与日志必须由真实控件绘制。

| 文件 | 像素 | 透明 | 用途与缩放 |
|---|---|---|---|
| scene-background.png | 1672×941 | 否 | 舞台、建筑和喷泉；UniformToFill，正文使用不透明面板 |
| furina-main.png | 1024×1536 | 是 | 完整坐姿角色及座椅；Uniform，不拉伸、不裁掉帽饰或鞋 |
| furina-avatar.png | 1254×1254 | 是 | 独立头像；Uniform，建议 48–72 DIP，高质量采样 |
| water-foreground.png | 2172×724 | 是 | 底部水纹；按比例放置并限制在装饰区，不覆盖操作和状态 |
| game-genshin.png | 2172×724 | 否 | 原神主题横幅，左侧留给真实名称 |
| game-hsr.png | 2172×724 | 否 | 星铁主题横幅，左侧留给真实名称 |
| game-zzz.png | 2172×724 | 否 | 绝区零主题横幅，左侧留给真实名称 |

完整性、尺寸、字节数与 SHA256 见 `manifest.json`。主角色、头像和水纹已检查真实 Alpha 通道；缩放后的角色轮廓、帽饰、白发、双鞋和座椅在离屏 WPF 样张中可见。头像必须设置 `RenderOptions.BitmapScalingMode="HighQuality"`，避免大尺寸透明边缘缩小时出现锯齿。高 DPI 窗口与最终页面构图仍交 B/Q 验收。

## 外置素材兼容

默认读取安装目录相邻的 `../VIvian-素材`，只读，不生成或改写用户配置。

- 保留 `character.(gif|png|jpg|jpeg|webp)`、`assistant.(gif|png|jpg|jpeg|webp)`、`background.(png|jpg|jpeg)` 和 `banner-genshin/starrail/zzz.(png|jpg|jpeg|gif)` 的顺序及格式。
- 新标准 PNG 文件名也可以直接放入外置目录；同一槽位中旧名称优先。
- 外置 assistant / furina-avatar 优先，其次外置 character / furina-main，再其次内置头像、内置角色；这保留旧用户只有 character 时助手沿用该图的行为。
- 缺少或空文件、达到 60 MB 的文件跳过；剩余槽位从程序目录 `Assets/Furina` 取默认资源。默认资源也缺失时返回 null，现有 ArtSlot 矢量回退仍工作。
- 不新增图片解码器，现有 WEBP 支持程度不变；本加载器的检查仍是路径/大小检查，损坏但非空图片的解码回退不属于本次改动。
- theme.json 的自定义名称/标题保留；仅旧默认 `Vivian-cutar` 显示为 `furina`，文件本身不改。默认助手为芙宁娜。非法 JSON 或非字符串字段忽略。

用户现有素材清点（只读）：`E:\auto agent game\VIvian-素材` 只有 character.png（2,207,453 字节、带背景站姿）与 theme.json（62 字节）；这张用户图不复制进仓库，仍优先于新内置坐姿图。

## 总控 I 的最小注册

A 没有修改 App.xaml 或项目文件。

在 `src/VIvian.App/VIvian.App.csproj` 加入：

```xml
<ItemGroup>
  <Content Include="Assets\Furina\*.png" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
</ItemGroup>
```

字典已由 WPF 默认 Page glob 编译，不需重复 Page Include。App.xaml 的 Application.Resources 现为直接资源集合；将既有资源完整保留，包入 ResourceDictionary 并在开头加入：

```xml
<Application.Resources>
  <ResourceDictionary>
    <ResourceDictionary.MergedDictionaries>
      <ResourceDictionary Source="Themes/Furina/Theme.xaml" />
    </ResourceDictionary.MergedDictionaries>
    <!-- 全部原有资源原样保留在这里 -->
  </ResourceDictionary>
</Application.Resources>
```

主题只有显式 Furina.* 键，无隐式样式，因此单纯合入不会改变旧页面。PNG 选择 Content 是为了继续返回绝对文件路径，兼容现有 ArtSlot.Uri / GIF 加载流程，不引入第二套 pack-URI 解析器。注册后应验证普通构建和自包含 publish 目录均带七张 PNG。

## B 的消费

```xml
<Border Style="{StaticResource Furina.PanelStyle}">
  <Button Style="{StaticResource Furina.PrimaryButtonStyle}" Content="一键日常" Command="{Binding OneClickCommand}" IsEnabled="{Binding CanOperateWorkspace}" />
</Border>
<Image Source="{Binding Theme.Background}" Stretch="UniformToFill" IsHitTestVisible="False" />
<Image Source="{Binding Theme.WaterForeground}" Stretch="Uniform" IsHitTestVisible="False" />
```

示例消费已有 OneClickCommand 和 CanOperateWorkspace；B 对其他操作绑定实际既有命令。沿用 MainViewModel.CharacterArt、AssistantArt、各 EngineCard.Banner、AssistantViewModel.Avatar；不复制主题加载器。所有背景/角色/水纹/冠饰设 `IsHitTestVisible="False"`。文字叠在横幅上时加深色底/渐变，不能靠插画本身保证文字对比度。

字典提供 v1 全部画刷、尺寸和六种样式，另有 DangerButtonStyle、ErrorPanelStyle、CrownGeometry、WaveGeometry、字体/边框/输入等附加令牌。按钮提供普通/悬停/按下/键盘焦点/禁用状态；输入保留 PART_ContentHost、只读/禁用/校验错误状态。进度保留系统模板的范围、方向和不定动画语义，真实是否运行/是否错误由 B 绑定，不用 Tag 暗藏业务状态。键盘焦点在控件外侧预留 3 DIP 空间。

## 可复现验证

`FurinaThemeVisualTests` 在 STA 线程加载编译后的字典和真实控件，用 RenderTargetBitmap 离屏输出，无 Window.Show、无焦点/键盘/鼠标输入、无游戏。输出路径为仓库 `artifacts/tests-a/visual`。

覆盖 1600×900、960×500 DIP；各导出 100/125/150/200% 像素密度，共八张。小尺寸演示使用可滚动容器；它验证样式可布局，不代表最终产品在最小尺寸已经通过验收。像素密度导出也不等于真实系统 DPI 切换、多屏恢复或焦点测试。普通/禁用/错误/输入/消息/进度例子带明确演示标记，不能作为任务执行成功证据。



## Reference fidelity increment (2026-11-13)
Replaced only three bundled game banners with separate native-generated, text-free character artwork: Paimon / March 7th / Belle-inspired portraits on the right, dark text-safe area on the left, 2172x724. No reference screenshot was edited, cropped into controls or flattened into a UI. Existing canonical filenames and external/GIF lookup untouched. Original art remains in prior git revision. These fan-art illustrations are not official game assets; visual identity/fidelity still requires Q assessment. Source files reside under .codex/generated_images/01a1d4c3-6067-7b22-8179-e9fde0fb2f69; hashes in manifest version2. Code/compatibility review travels with the B reference-fidelity task; visual scores are not self-certified.
