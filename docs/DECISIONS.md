# M1 设计决策

## ADR-001：保持技术栈

使用 .NET 10、WPF、MVVM、SQLite、Serilog、xUnit。SDK固定为实际使用的10.0.403，NuGet精确版本与packages.lock.json入库。沿用原生WPF控件构建UI壳；WPF-UI不是用户强制依赖，待M5评估，不因此切换框架。

## ADR-002：M1与待验证引擎严格分离

只建立Core、Infrastructure、App、Tests，不为三个尚未验证的适配器放入猜测接口。IGameAdapter和生产状态机属于M2。没有真实启动/停止按钮，避免“已实现”假象。基础自检记录独立于未来游戏任务记录。

## ADR-003：配置持久化

M1引擎路径配置保存在VIvian的SQLite中，避免同时维护JSON与DB两个权威来源。三项设置作为同一事务保存；每项revision匹配才提交，冲突整体回滚并提示重新加载。保留Unicode、空格、字面SQL字符；不经shell解释。

数据库WAL和FULL同步、短连接、5秒busy timeout。UI将SQLite工作放到后台Task，以避免同步底层I/O阻塞界面。schema大于当前版本时拒绝启动，不重置；损坏不静默覆盖。M1无生产任务恢复，不能将SQLite事务等同于游戏任务幂等性。

## ADR-004：最小权限与数据边界

asInvoker，PerMonitorV2。仅VIvian数据目录和用户输入的路径字符串进入自身DB。没有关闭进程、读写游戏文件、提权及遥测逻辑。日志只写固定事件、诊断ID和异常类型，不写原始路径/配置/异常消息。代价是诊断详情较少，未来需设计白名单诊断字段。

## ADR-005：M1验证方式

Linux执行可移植测试及WPF交叉编译；Windows CI提供真实ContentRendered与DB回读冒烟。CI尚未运行时不能声称Windows启动通过。UI视觉和100/125/150/200%缩放保留人工检查。

## ADR-006：发布范围

M1可提供未签名自包含便携预览与源码；安装器属于M6。无远程仓库则仅本地feature分支/提交和PR正文草稿，不制造远程PR链接。M1等待审查，未经质量关口不开展后续里程碑。
