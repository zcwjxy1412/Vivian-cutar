# VIvian V1.0 — M1 验收材料

日期：2026-11-03；应用版本：0.1.0；分支：feature/m1-foundation。

**结论：M1基础工程已实现并完成交叉构建、自动化测试及win-x64发布；Windows启动、视觉与高DPI验收尚未执行，M1未最终放行。停止于M1，不进入M2。**

## 1. 已完成功能与文件

| 功能 | 实现文件 |
|---|---|
| Solution、SDK/依赖锁定 | VIvian.slnx、global.json、Directory.Build.props、NuGet.Config、各项目packages.lock.json |
| 分层与存储契约 | src/VIvian.Core/Foundation.cs |
| SQLite模式、设置事务、并发冲突、历史 | src/VIvian.Infrastructure/LocalDatabase.cs |
| DI、Serilog、启动/未处理异常 | src/VIvian.App/App.xaml.cs |
| MVVM与互斥UI操作 | src/VIvian.App/Mvvm.cs、MainViewModel.cs |
| 概览、设置、自检记录、关于 | src/VIvian.App/MainWindow.xaml |
| 工作区尺寸适配、DPI、普通权限 | MainWindow.xaml.cs、app.manifest |
| 持久化与错误路径测试 | tests/VIvian.Tests/PersistenceTests.cs |
| Windows构建、便携发布、启动冒烟 | scripts/Build.ps1、Smoke.ps1、.github/workflows/windows.yml |
| 架构、使用及故障说明 | README.md、docs/DECISIONS.md、TROUBLESHOOTING.md、PR_DRAFT.md |
| 原始依据及摘要 | docs/reference、docs/INPUTS.md |

完整交付文件清单与SHA-256在交付包的 FILE_MANIFEST.json。

## 2. 提交与PR

本地feature/m1-foundation分支；确切commit位于交付包 COMMIT.txt，Git历史保存在 VIvian-M1.git.bundle。

未提供远程GitHub仓库，因此未推送、未创建远程PR，也未运行GitHub Actions。PR正文草稿已附。不要把工作流文件的存在当作CI已通过。

## 3. 实际验证结果

环境：Ubuntu 24.04 x64，.NET SDK 10.0.403，.NET运行时10.0.14。

| 检查 | 结果 | 原始证据 |
|---|---|---|
| NuGet锁定模式还原 | 通过 | evidence/logs/restore-locked.log |
| Release Solution构建（含WPF XAML） | 通过，0 warning / 0 error | evidence/logs/build.log |
| xUnit | 14通过、0失败、0跳过 | evidence/logs/test.log、evidence/tests/m1.trx |
| Windows x64自包含发布 | 通过，生成VIvian.exe及运行时 | evidence/logs/publish.log |
| 依赖清单 | 从生成的锁文件导出 | evidence/dependencies.json |
| Windows GUI启动/ContentRendered | 未执行 | 已提供Smoke.ps1与Windows CI |
| 100/125/150/200%缩放、双屏、视觉 | 未执行 | 下表人工验收 |
| 真实引擎、UAC、游戏操作 | 未执行，不在M1范围 | C-1～C-5仍未关闭 |

14项测试覆盖：重复初始化不覆盖设置、批量冲突完整回滚、两个写入者的版本冲突、取消前不写入、两种控制字符拒绝、重复游戏项拒绝、SQL样式字符串作为普通数据、历史跨连接保留及排序/限量、重复记录不覆盖、两种非法查询限额拒绝、未来模式不重置、损坏数据库不静默重建。

这些测试不等于任务调度、游戏判定或Windows进程管理测试；没有宣称覆盖率达到后续发布门槛。

## 4. 构建中发现并处理的问题

- 初始Microsoft.Data.Sqlite 10.0.0引入原生SQLite依赖的高危公告，审计实际阻止还原。升级到10.0.14并重新还原/构建通过；没有关闭NuGet漏洞检查。初始错误保留在evidence/logs/build-app.log。
- 发布RID与项目锁文件不一致，已将win-x64统一纳入项目运行时标识并重新生成锁文件，锁定还原和最终发布通过。
- 当前容器默认多节点MSBuild曾无有效诊断退出；使用-m:1成功构建。提供的构建脚本也使用单节点。
- SDK安装脚本因容器文件所有权限制报告提取失败；实际文件已提取，随后dotnet --info、编译、测试与发布均执行成功。这不代表已在Windows安装SDK。

## 5. Windows人工验收清单

在Windows x64新目录解压 portable，启动VIvian.exe。当前产物未代码签名。

- [ ] 正常用户权限下打开窗口，无UAC请求。
- [ ] 概览显示三个“尚未接入”，没有伪造的游戏成功状态。
- [ ] 保存含中文/空格的目录字符串，重启后保持；输入目录中的文件不变。
- [ ] 打开两个窗口，后保存的旧配置收到冲突提示，不覆盖新设置。
- [ ] 基础自检生成记录，刷新/重启后仍可查看；游戏不启动。
- [ ] 100%、125%、150%、200%缩放可读可操作，缩小窗口可滚动，双屏移动正常。
- [ ] 数据目录不可写或数据库损坏时显示受控错误，原数据库不被删除。
- [ ] ./scripts/Build.ps1 -SmokeTest在Windows通过，上传原始日志供复核。

UI已采用滚动布局、工作区尺寸限制和PerMonitorV2，但未以这些代码代替实际视觉检查。

## 6. 未完成内容及风险

- 没有Windows实机或远程Windows CI结果，故不承诺此交叉发布包已通过Windows运行验收。
- 暂无正式安装器、签名、托盘、浅色主题、调度队列、真实适配器、更新回滚。这些分别属于后续M2～M6。
- 不提供游戏路径真实性/版本兼容检测；M1只保存字符串。
- 没有程序单实例锁（M2范围）；数据库冲突保护已实现。
- 日志仅记异常类型以避免泄漏路径，复杂异常诊断细节仍需后续白名单设计。
- 自检历史尚无用户清理/保留期限设置；显示最近50条。
- Windows PowerShell构建脚本与CI工作流尚未实际运行；本轮实际命令为Linux上的等价dotnet命令。
- 许可证通知为M1清单，最终完整SBOM、许可证文本、签名和再分发审核属于发布门禁；不以此预览对外宣称合规验收通过。

## 7. 下一里程碑依赖

先审核本M1源码与上述Windows证据；确认界面基础、持久化和错误处理可接受，再进入M2。M2前须冻结IGameAdapter、生命周期/结果/清理状态的区分。真实三引擎接入继续受C-1～C-5验证约束，不能用14项M1测试替代。

本轮没有运行或修改任何真实引擎、游戏和账号；没有向外部AI发送消息。
