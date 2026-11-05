# M4-A2 首次单次包实测：任务入口未启动

2026-11-05 09:00（本机时间），用户再次确认后，在独立包 `be1106b` 中仅点击一次 `screen_test` 启动。官方 v2026.10.31 发行包和安装目录文件摘要先在 WPF 界面核对通过。界面最终显示“启动失败”，没有出现 UAC；未观察到游戏操作。一次性标记已创建，旧包不可重试。

独立 `%LOCALAPPDATA%\VIvian-M4A-ScreenTest\vivian.db` 的运行记录：VIvian RunId `2c409c97-a4b7-49f0-8e88-361f11ee0dbb`，`Finished / Failed / NotRequired / start_failed`。转换顺序为 `Pending → Preflight → Starting → Reconciling → Finished`，无运行事件。事后本机进程查询未发现 `m7a.exe`。此记录证明任务未成功启动；不构成真实游戏启动、UAC Worker 停止或完成路径的验证。

代码定位：`March7thAdapter.StartAsync` 原先将调度器的单次 `RunProcessScope` 依次用于 `--status` 和 `screen_test`；前者耗尽唯一的进程启动许可，后者必然被拒绝。`RequestStopAsync` 同样曾把该许可用于 `--control <RunId> stop`。修复后，状态查询与精确 RunId 控制命令各由独立 Windows Job 监管；调度器单次许可专供本次任务入口。状态查询必须确认退出和进程清理，才能进入任务启动。任何清理不明仍保守阻断。

修复的特殊构建回归测试核对：`--status` 与 `--control` 由辅助监管器承接，任务入口仅使用一次调度器许可。普通构建全部 370 项测试通过。上述均为无游戏的代码验证；新的真实运行需重新取得用户单次授权。第二包使用独立 `%LOCALAPPDATA%\VIvian-M4A-ScreenTest-R2`，不清除或复用旧包的一次性标记及数据库。
