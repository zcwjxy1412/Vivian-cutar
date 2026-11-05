# VIvian M4-A Windows 预览版

请将本 ZIP 解压到新目录，双击 `启动M4A预览.cmd`。预览包内有专用标记，即使直接启动 `VIvian.exe`，也使用 `%LOCALAPPDATA%\VIvian-M4A-Preview` 存放设置、日志和 SQLite，与现有 `%LOCALAPPDATA%\VIvian` 分离。保留现有安装，不要把本包解压到原安装目录。

目前可在 WPF 中选择 March7thAssistant 安装目录、核对官方 ZIP，并在“星铁实测”页查看固定任务选择、执行阻断原因、日志和停止入口；启动按钮在当前版本中保持禁用。模拟队列仍标为模拟。独立隔离状态探针用于 `m7a.exe --status` 的实时检查，预览版不会自动运行该命令。

真实任务执行仍关闭：无害类比进程的 UAC 停止已在本机通过，但 March7thAssistant 自身的提权 Worker、停止终态与独立完成证据尚待真实任务实测。请勿把预览版或退出码 0 当成真实日常完成证明。首次真实任务需要另行明确批准。
