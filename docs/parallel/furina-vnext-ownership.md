# furina 分工与依赖

总任务 #59。用户选择：本聊天总控＋三个独立工作聊天。工作项不是八个同时运行的聊天。

|任务|负责人|分支|任务单|
|---|---|---|---|
|T0/I|总控|integration/furina-vnext|#60|
|A|视觉与界面|feat/furina-art-theme|#61|
|B|视觉与界面|feat/furina-wpf-ui|#62|
|C|AI 助手|feat/furina-ai-agent|#63|
|F|执行内核与游戏|feat/furina-runtime|#66|
|E|执行内核与游戏|feat/furina-zzz|#65|
|D|执行内核与游戏|feat/furina-genshin|#64|
|Q|总控与独立审查|test/furina-acceptance|#67|

## 所有权

I 唯一修改公共契约/DTO、App.xaml、App.xaml.cs、项目文件、数据库迁移、构建与工作流。A 修改 Assets/Furina、Themes/Furina、ThemeAssets.cs。B 修改 MainWindow、AssistantWindow 视图及交互、MainViewModel 和其余视图。C 修改 AssistantViewModel、Infrastructure/Assistant 及专属测试。F 修改调度、桌面租约、进程监管、恢复和星铁回归；D/E 分别修改 BetterGi/OneDragon 专属实现及测试。Q 修改验收规则、固定场景、证据清单。

公共文件变更交 I，不以跨目录补丁绕过所有权。每条分支独立工作树与测试目录，禁止切换另一聊天工作目录的分支。

## 调度

T0 后首批 A/C/F；A 合流后 B；F 核验后执行聊天 E→D。C/F 合流先做星铁请求到回执闭环。Q 持续采证。真实游戏、前台输入和安装切换只由总控安排，同一桌面一次一个；工作聊天先做离线/模拟验证。

各分支更新 docs/parallel/furina-<code>-status.md，记录 BASE_SHA、HEAD、依赖公共提交、验证命令/结果、未验证项和下一步。草稿 PR 的 base 为 integration/furina-vnext；I 自身不能向自身开 PR，维护集成分支即可。无差异分支暂不开空 PR，首个有效交付后创建。

## 完成门槛与明确排除

审查遵循根 AGENTS.md：首审一次、按审查修改后复审一次；替代原任务书 §9.7 的三轮建议。工具缺失不可标通过。不要虚构账号、完成证据、百分比或暂停能力。

保留 WPF、现有引擎版本、原安装与账号数据。静态角色及轻量动画先行；不含 Live2D/语音/桌宠。不得合并 main 或替换正式安装。
