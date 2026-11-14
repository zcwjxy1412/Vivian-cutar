# 已提交视觉候选 DeepSeek 补审（2026-11-14）

用户明确要求先诊断超时、恢复后补审最新提交，再交总控安排实机。此次为用户授权的补审，不追记此前无效请求为通过。

## 固定范围

当前已提交基线 4a46ad0；产品文件与06fa8e2一致（git diff 06fa8e2 4a46ad0 -- src tests为空）。本轮提交80d47ba之后至06fa8e2的实际src/tests累计差异，覆盖ff02d0b、47d37f9、06fa8e2此前未审范围，并附MainWindow代码后置必要上下文。CourtPreview已在其独立任务审查，不重复计入。

工作区正在制作的圆角裁切、权限排版、图标、发行包折叠与品牌文案未提交，明确不在此次审查结论内；不得把本记录视为这些修改已完成或已验收。

## 超时诊断及当前复核

历史证据见furina-b-deepseek-recovery.md。已确认原Node请求把thinking放进extra_body，disabled没有作用，是长耗时的重要原因；历史600秒超时没有服务端耗时，不能排除当时服务负载。该缺陷此前已修复，本次没有重复修改工具或凭据。

本次实际复核：
- DeepSeekHelper auth：models/chat均ok。
- verify-deepseek-wire.mjs退出0：实际请求源码enabled/disabled均序列化到JSON根级，无extra_body嵌套；测试不联网、不用凭据。
- deepseek_review实际返回：deepseek-v4-pro，thinking disabled，finish stop，reasoning tokens 0；prompt16421/completion1120。正常完整结束。

## 独立审查及逐项判断

本次有效补审1轮，结论“无阻塞缺陷”，无需修改实现或复审。原始请求/结果保存在artifacts/tests-b/supplement-4a46ad0-review1-{request,response}.json。

- T1尺寸时序/标题容差：审查所称产品仅读取Width不准确；UpdateHomeLayout优先ActualWidth，离屏无HWND时回退Width。夹具显式FlushBindings，Render执行Measure/Arrange/UpdateLayout后再取标题坐标，测试的是DIP左边界而非字体字形像素。没有确认缺陷，不随意放宽断言。真实窗口DPI事件仍待验收。
- L1键盘焦点不可见：未成立。AccountChoiceCard已有IsKeyboardFocusWithin触发器，金色2 DIP边框；不能仅凭FocusVisualStyle为空判断无焦点标识。
- L2极窄视口smoke失败：该检查刻意拒绝零宽无效布局；没有已复现缺陷，不移除防护。
- T2反射播种：承认私有字段重命名时需同步测试，缺字段明确抛错；仅合成数据夹具。
- T3依赖CommunityToolkit的Raise：事实不符。ObservableObject为src/VIvian.App/Mvvm.cs自有实现，受保护Raise(string?)明确存在。
- DPI覆盖说法需限定：夹具已有960×500/200%及宽屏单/双列检查，但离屏DPI设置不代替真实显示器切换。

## 验证与交接

无产品代码改动，因此沿用固定候选已有验证，不冒称本次重跑：
- 47d37f9默认/M5全量各1270/1270，固定DEMO截图各211张。
- 06fa8e2默认真实WPF模拟smoke通过，默认/M5相关测试各12/12（本次复核日志）。
- M5真实smoke启动取消，未完成。无真实游戏或任务奖励验收。
- 本次git diff --check通过；未部署、未显示窗口、未启动游戏。

本轮补审已通过，可由总控对固定已提交候选安排串行实机验收，重点是真实DPI/多屏、滚动和键盘焦点、助手输入、M5权限/启动。待制作中的新视觉批次仍须单独完成验证和最多两轮独立审查后才能作为新候选交付。

