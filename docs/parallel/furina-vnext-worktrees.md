# 工作目录登记

每次命令显式指定下表目录。Angent 项目根目录自身不是 Git 仓库，新聊天以 local 项目创建后使用已分配的工作树，不再从项目默认分支新建。

|任务|分支|独立目录|Issue|
|---|---|---|---|
|T0|integration/furina-vnext|C:\Users\胡桃\.codex\worktrees\furina-vnext-integration\Vivian-source|#60|
|A|feat/furina-art-theme|C:\Users\胡桃\.codex\worktrees\furina-vnext-art\Vivian-source|#61|
|B|feat/furina-wpf-ui|C:\Users\胡桃\.codex\worktrees\furina-vnext-ui\Vivian-source|#62|
|C|feat/furina-ai-agent|C:\Users\胡桃\.codex\worktrees\furina-vnext-ai\Vivian-source|#63|
|D|feat/furina-genshin|C:\Users\胡桃\.codex\worktrees\furina-vnext-genshin\Vivian-source|#64|
|E|feat/furina-zzz|C:\Users\胡桃\.codex\worktrees\furina-vnext-zzz\Vivian-source|#65|
|F|feat/furina-runtime|C:\Users\胡桃\.codex\worktrees\furina-vnext-runtime\Vivian-source|#66|
|Q|test/furina-acceptance|C:\Users\胡桃\.codex\worktrees\furina-vnext-acceptance\Vivian-source|#67|

开发数据放各工作树 artifacts/test-data，或测试生成的独立临时目录。不得复制正式库作为可共享可变数据库。

三个工作聊天的 ID 在派发后由总控追加。提交公共需求写入各自状态文件，供总控读取；无需用户转贴消息。

