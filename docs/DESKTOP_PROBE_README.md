# VIvian 阶段 B 无害进程测试

仅适用于 Windows 10/11 x64。测试包自包含，无需安装 .NET 或管理员权限，不启动游戏和真实引擎。

1. 从本 PR 的成功 Windows validation 运行下载 `VIvian-DesktopProbe-win-x64` Artifact，解开外层 Artifact，核对其中 ZIP 的 SHA-256 与 `.sha256` 文件一致。
2. 将 `VIvian-DesktopProbe-win-x64.zip` 解压至新的空目录。保留三个 EXE 在同一目录。
3. 右键 `Run-DesktopProbe.ps1`，选择“使用 PowerShell 运行”；如系统阻止脚本，可打开 PowerShell，在解压目录执行 `& '.\VIvian.WindowsBoundaryProbe.exe'`。**不要提升为管理员**。
4. 测试完成后，把终端末尾的“报告目录”中的 `报告.json` 和 `摘要.txt` 发给 Codex。报告默认位于当前账户的临时目录 `VIvian-DesktopProbe` 下。若工具异常退出，请提供终端错误截图。

工具只创建和结束自己的 FakeEngine、对照进程与 CrashHost；CrashHost 崩溃测试只结束该测试宿主。SQLite 数据只写在新建的临时测试目录，不读取或修改 `%LOCALAPPDATA%\VIvian`，也不接触用户已安装的 `E:\auto agent game\VIvian-win-x64\VIvian.exe`。

本机自动项目包括 Job 归属、同名对照进程、子进程清理、紧急停止与数据库结果、退出码无完成证据、静默卡死、拒绝优雅停止、启动失败、独立宿主崩溃和重启对账。迟到启动、未知清理及数据库写入故障注入目前由仓库已有的定向单测覆盖，**本机探针尚未覆盖**，报告明确标为 `NOT RUN`，不能据此宣布整个阶段 B 已完成。

结果含 `PASS`、`FAIL`、`INCONCLUSIVE` 和 `NOT RUN`。UAC 与官方 CLI 始终标为 `NOT RUN`。若有失败或证据不足，保留报告，暂不开展下一阶段。报告不记录账户名、SID、命令行参数、私人文件或正式数据库；请在分享前自行查看报告内容。
