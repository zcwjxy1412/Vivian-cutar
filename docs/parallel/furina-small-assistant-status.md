# 小尺寸助手输入可读性修复

基线 a1e05ed；产品提交 411835f2ecdb6ab4b472fc50ac6dc164db360848。只调整助手聊天输入框 Padding=8,3，设置滚动区增加测试定位名；不改共享模板、业务、素材、安装或账号数据。

## 根因与回归
共享 InputStyle 将 Padding 用作 PART_ContentHost 的 Margin，文本宿主内部也计入 Padding。聊天输入框有 MaxHeight=52：旧12,10留白使实际 ScrollContentPresenter 只有10 DIP高，小于中文字体行高17.7803 DIP。回归测试在改动前真实失败，日志 `artifacts/tests-b/tiny-red.log`；调整局部留白后通过。没有放宽断言。

测试不再只看发送按钮：读取实际 ScrollContentPresenter 尺寸，在任何 ScrollToLine 之前断言 viewport>=字体行高，再滚动到首/末字符并检查 GetRectFromCharacterIndex 的上下边界在可视区内。四档根DPI 100/125/150/200%，360x400下测试长消息、20行长计划、展开设置；检查内容区不压住输入、末端可达、完整确认/保存按钮可滚入视口。全部为合成数据，未点击确认/保存，不启动模型或游戏。

## 独立审查（本任务1轮）
直接 DeepSeekHelper MCP，deepseek-v4-pro / thinking disabled / finish stop / reasoning0，prompt6696 completion521。结论“未发现阻断性问题”，提出三项测试假设，逐项核实：

1. 怀疑模板/布局未准备：每次 helper 前实际 Render 已 Measure/Arrange/UpdateLayout；host.ApplyTemplate 后才取 presenter，真实执行有非零尺寸并通过。不是取错元素。
2. 建议滚动前保留视口行高断言：代码已在 foreach/ScrollToLine 之前执行，而且 RED 正是在该断言失败。循环不会掩盖已执行的失败断言。
3. 设置时 ChatScroll 可能遮挡：设置区与聊天区同属固定 star 行，设置区 ZIndex=1、实色背景，聊天区在下层；两者布局不改变输入的独立 Auto 行。实际设置保存按钮可完整显示，输入首/末字符仍完整。没有证据支持遮挡缺陷。

未因审查修改实现，无需凑第二轮；目前没有核实成立的剩余代码阻断。固定图仍是离屏图，实际输入法、系统DPI/多屏和视觉85/70不在此结果中。

## 最终验证与交付
最终默认/M5各1252/1252通过，0失败/跳过。固定产品同提交 Release 离屏证据为 artifacts/tests-b/tiny-411835f/，33张PNG及evidence.json哈希；包含四档缩放的首末行及完整保存/确认按钮。DLL ProductVersion核实为0.4.0-dev+411835f2ecdb6ab4b472fc50ac6dc164db360848。日志 tiny-default.log/trx、tiny-m5.log/trx、tiny-deepseek-response.json 均在 artifacts/tests-b。只读merge-tree对交付时集成分支可干净合并。安装、备份账号及原快捷方式由总控按用户授权统筹，本聊天不打开窗口或操作桌面。
