# VIvian M3 三上游引擎接口与外部调度矩阵 (M3_ENGINE_INTERFACE_MATRIX.md)

> Gemini 原始研究。文中的版本状态、示例和建议保留原貌，实测状态与冲突见[审查记录](../reviews/M3_GEMINI_RESEARCH_VALIDATION.md)。

文档路径：docs/research/M3_ENGINE_INTERFACE_MATRIX.md

核查基准日期：2026-11-03

覆盖引擎：

1.  原神：BetterGI (v0.66.0 / refactor/20260930)
2.  崩坏：星穹铁道：March7thAssistant (v2026.10.31)
3.  绝区零：ZenlessZoneZero-OneDragon (v2.6.0)

## 1. 外部调用接口能力横向矩阵

<table>
<colgroup>
<col style="width: 25%" />
<col style="width: 25%" />
<col style="width: 25%" />
<col style="width: 25%" />
</colgroup>
<tbody>
<tr class="odd">
<td><p>评估维度</p></td>
<td><p>BetterGI (原神)</p></td>
<td><p>March7thAssistant (星铁)</p></td>
<td><p>ZZZ-OneDragon (绝区零)</p></td>
</tr>
<tr class="even">
<td><p>可执行程序入口</p></td>
<td><p>{Root}\BetterGI.exe</p>
<p>[已从源码确认]</p></td>
<td><p>{Root}\m7a.exe（免提权入口）</p>
<p>{Root}\March7th Assistant.exe（主程序）</p>
<p>[已从源码确认]</p></td>
<td><p>{Root}\OneDragon-RuntimeLauncher.exe（集成启动器）</p>
<p>[官方文档已说明，但尚未实际测试]</p></td>
</tr>
<tr class="odd">
<td><p>命令行启动一条龙</p></td>
<td><p>BetterGI.exe startOneDragon [配置名]</p>
<p>[已从源码确认]</p></td>
<td><p>m7a.exe main --after-finish None</p>
<p>或 March7th Launcher.exe main -e</p>
<p>[已从源码确认]</p></td>
<td><p>OneDragon-RuntimeLauncher.exe -o -c</p>
<p>[官方文档已说明，但尚未实际测试]</p></td>
</tr>
<tr class="even">
<td><p>命令行指定子任务</p></td>
<td><p>不支持（需通过预写配置文件映射）</p>
<p>[已从源码确认]</p></td>
<td><p>支持（直接传参 daily, power, currencywars, fight, universe）</p>
<p>[已从源码确认]</p></td>
<td><p>不支持（仅支持 -i 实例ID，具体任务需读取配置）</p>
<p>[官方文档已说明，但尚未实际测试]</p></td>
</tr>
<tr class="odd">
<td><p>无界面静默运行</p></td>
<td><p>必须在 Windows 实机验证（无专用 headless 标志，但可后台分身）</p></td>
<td><p>支持（控制台应用，不加载 PySide6）</p>
<p>[已从源码确认]</p></td>
<td><p>必须在 Windows 实机验证（底层为 PySide6，-o 启动时是否有闪烁待实测）</p></td>
</tr>
<tr class="even">
<td><p>结构化状态查询</p></td>
<td><p>暂时无法确认（稳定版无；开发分支提供 HTTP/MCP 服务）</p></td>
<td><p>支持（m7a.exe --status 返回 JSON，m7a.exe --list --json）</p>
<p>[官方文档已说明，但尚未实际测试]</p></td>
<td><p>支持（内置 MCP 服务 http://127.0.0.1:23001/mcp，或命令行 -v）</p>
<p>[已从源码确认]</p></td>
</tr>
<tr class="odd">
<td><p>标准输出包含事件</p></td>
<td><p>不支持（分配独立控制台窗口，不写父管道）</p>
<p>[已从源码确认]</p></td>
<td><p>支持（标准输出重定向直接捕获 UTF-8 事件流）</p>
<p>[已从源码确认]</p></td>
<td><p>必须在 Windows 实机验证（启动器是否向 stdout 转发核心运行事件）</p></td>
</tr>
<tr class="even">
<td><p>日志文件捕获</p></td>
<td><p>{Root}\log\better-genshin-impact{yyyyMMdd}.log</p>
<p>[已从源码确认]</p></td>
<td><p>{Root}\logs\*.log</p>
<p>[已从源码确认]</p></td>
<td><p>{Root}\.log\*.log</p>
<p>[已从源码确认]</p></td>
</tr>
<tr class="odd">
<td><p>任务成功判定</p></td>
<td><p>日志证据为准：匹配 一条龙和配置组任务结束</p>
<p>[已从源码确认]</p></td>
<td><p>进程退出码为准：ExitCode == 0（配合非阻塞配置）</p>
<p>[已从源码确认]</p></td>
<td><p>进程与游戏状态为准：启动器退出且 ZenlessZoneZero.exe 关闭</p>
<p>[必须在 Windows 实机验证]</p></td>
</tr>
<tr class="even">
<td><p>任务失败判定</p></td>
<td><p>日志包含 任务被取消、UnHandle Exception、PaddleOcr预热异常 或超时</p>
<p>[已从源码确认]</p></td>
<td><p>进程退出码 ExitCode != 0 或日志包含 发生错误</p>
<p>[已从源码确认]</p></td>
<td><p>进程异常退出、日志包含 Traceback 或超时</p>
<p>[必须在 Windows 实机验证]</p></td>
</tr>
<tr class="odd">
<td><p>安全停止能力</p></td>
<td><p>内部热键支持；外部调用需优雅关闭窗口或 Kill 进程树</p>
<p>[已从源码确认]</p></td>
<td><p>内部热键 F10 停止、F8 暂停；外部可终止进程树</p>
<p>[已从源码确认]</p></td>
<td><p>缺乏专用命令行停止，外部超时强杀</p>
<p>[必须在 Windows 实机验证]</p></td>
</tr>
<tr class="even">
<td><p>管理员权限要求</p></td>
<td><p>强制要求（非管理员启动立即退出并触发 runas 提权）</p>
<p>[已从源码确认]</p></td>
<td><p>任务执行必须提权，查询状态免提权</p>
<p>[已从源码确认]</p></td>
<td><p>强制要求（启动器拉起 UAC）</p>
<p>[已从源码确认]</p></td>
</tr>
<tr class="odd">
<td><p>多实例支持</p></td>
<td><p>稳定版单实例强互斥；新架构支持 RDP 分身实例</p>
<p>[已从源码确认]</p></td>
<td><p>本地游戏仅单实例；云游戏支持容器多开</p>
<p>[已从源码确认]</p></td>
<td><p>原生支持多配置实例（config/01/, config/02/，-i 1,2 串行）</p>
<p>[已从源码确认]</p></td>
</tr>
<tr class="even">
<td><p>配置持久化格式</p></td>
<td><p>JSON (User/config.json, User/OneDragon/*.json)</p>
<p>[已从源码确认]</p></td>
<td><p>YAML (config.yaml)</p>
<p>[已从源码确认]</p></td>
<td><p>YAML (config/env.yml, config/one_dragon.yml, config/01/)</p>
<p>[已从源码确认]</p></td>
</tr>
</tbody>
</table>

## 2. VIvian 调用参数构建规范（生产就绪推荐）

### 2.1 BetterGI（原神）调用参数规范

- 执行命令：BetterGI.exe startOneDragon VivianDefault
- 执行前置动作：

1.  检查是否存在残留的 BetterGI.exe 进程，若存在则强制清理，防止单实例锁（65535）拦截。
2.  读写 {Root}\User\OneDragon\VivianDefault.json，根据用户在 VIvian 界面的勾选设置写入 TaskEnabledList，并强制设置 "CompletionAction": "关闭游戏和软件"。
3.  记录 {Root}\log\\ 下当日日志文件末尾的字节偏移（Byte Offset）。

- 进程启动规范：

<!-- -->

- FileName: {BetterGiRoot}\BetterGI.exe
- Arguments: startOneDragon VivianDefault
- WorkingDirectory: {BetterGiRoot}
- UseShellExecute: false
- CreateNoWindow: false（WPF 应用必须允许创建宿主窗口）

### 2.2 March7thAssistant（星穹铁道）调用参数规范

- 执行命令：m7a.exe main --after-finish None（或指定子任务如 m7a.exe daily --after-finish None）
- 执行前置动作：

1.  执行 m7a.exe --status 查询免责声明状态。若 first_run_completed == false，阻断运行并弹窗引导用户打开原版 GUI 点击确认。
2.  检查 {Root}\config.yaml，打入非阻塞补丁：

- pause_after_success: false
- exit_after_failure: true
- check_update: false
- auto_update: false
- 注入当前勾选的日常开关（daily_enable, power_enable 等）。

<!-- -->

- 进程启动规范：

<!-- -->

- FileName: {March7thRoot}\m7a.exe（或直接使用 {March7thRoot}\March7th Assistant.exe）
- Arguments: main --after-finish None
- WorkingDirectory: {March7thRoot}
- UseShellExecute: false
- RedirectStandardOutput: true
- RedirectStandardError: true
- StandardOutputEncoding: Encoding.UTF8
- CreateNoWindow: true（纯控制台工具，支持完全后台静默运行）

### 2.3 ZenlessZoneZero-OneDragon（绝区零）调用参数规范

- 执行命令：OneDragon-RuntimeLauncher.exe -o -c（多账号实例为 OneDragon-RuntimeLauncher.exe -o -c -i 1,2）
- 执行前置动作：

1.  校验路径是否为全英文、无空格。
2.  检查 {Root}\config\env.yml，设置 auto_update: false，防止启动时网络拉取代码破坏稳定性。
3.  预先更新 {Root}\config\01\\ 下的目标子任务开关。

- 进程启动规范：

<!-- -->

- FileName: {ZzzRoot}\OneDragon-RuntimeLauncher.exe
- Arguments: -o -c
- WorkingDirectory: {ZzzRoot}
- UseShellExecute: false
- CreateNoWindow: false
