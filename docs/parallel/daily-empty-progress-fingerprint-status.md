# 星铁空任务进度确认指纹修复

基线：`0297a9547885dd2cda11a00b8ce4ce0f2d86e9c9`。分支：`fix/daily-empty-progress-fingerprint`。

## 根因证据

2026-11-14 晚间计划 `fc291fd5-0db3-4d1b-964e-53f93ceefbf7` 的第一项星铁成功，第二项在适配器启动前以 `preflight_error` 停止，无 RunId，清理为 NotRequired、ConfigurationRestored=true。后续项目跳过。诊断通过只读 SQLite 连接、配置与日志读取完成，没有修改正式数据、游戏配置或恢复标记。

固定上游的 config_diag.log 在 22:57:36 / 22:57:43 / 22:57:53 依次记录 `daily_tasks={}` / `[]` / `{}`，22:57:55 写入完成时间戳。现有数值时间戳归一化已生效，但空列表变为空字典仍改变整份执行确认指纹。

两项星铁的任务、名称和资源摘要与接受回执相同，配置版本不同。只在内存中将当前 `daily_tasks: {}` 换回 `daily_tasks: []`，再按现有公式计算，两项完整 ConfigurationRevision 均与各自接受回执精确相等。这不仅是字段猜测，也验证了此次差异不需要由账号版本、独立设置版本或路径变化解释。历史内存中的运行条件状态无法回溯；本证据足以确认配置版本差异这一阻断原因。

## 上游语义

使用 GitHub 连接器读取固定标签 v2026.10.31，而非浮动 main：

- [tasks/daily/daily.py](https://github.com/moesnow/March7thAssistant/blob/v2026.10.31/tasks/daily/daily.py)：Daily.run 写入 `empty_tasks=[]`，随后 Daily.lookup 保存 `Tasks.daily_tasks`；仅 `len(cfg.daily_tasks)>0` 时处理非空任务。
- [tasks/daily/tasks.py](https://github.com/moesnow/March7thAssistant/blob/v2026.10.31/tasks/daily/tasks.py)：Tasks 初始化 `self.daily_tasks={}`，查询到任务后才填入任务项。
- [module/config/config.py](https://github.com/moesnow/March7thAssistant/blob/v2026.10.31/module/config/config.py)：set_value 对 list/dict/set 深拷贝并保存，不统一列表与字典形态。

因此只确认这里的两个精确空值等价；非空任务进度不属于本次豁免。

## 实现边界

仅在 StarRailAccountConfigurationProvider 的执行确认读取中，将顶层裸键 `daily_tasks:` 的精确 `[]` / `{}` 统一为 `[]`。值外的字节保留，包括注释、换行和资源配置。原有读取快照哈希复核保留。

顶层裸键或单／双引号键重复时不归一化；单独带引号键、带引号值、非空列表／字典、`{ }` 等未知形态不归一化。本实现面向固定上游的行式配置格式，不扩展为通用 YAML 语义规范化。普通 ReadAsync、ProposeAsync、ApplyAsync 仍使用严格原始配置版本。

未修改 Core、App、协调器或执行接线；未部署、启动游戏、重跑旧计划、清除未知清理记录。

## 验证

- 修复前行为回归：16 项中 2 失败 / 14 通过；失败仅为 `empty_map`、`empty_list`，第二项实际执行次数 1，预期 2。
- 修复后扩展针对性测试：21/21。
- 默认全量：1296/1296。
- M5 全量：1296/1296。
- 真实临时 SQLite 与合成配置覆盖双向空形态变化、CRLF、非空映射／列表、非空清空、重复裸键／引号键、引号标量、未知空格式、同时修改燃料、修改注释，以及既有时间戳／资源变化。等价进度允许第二项执行；其他变化仍停止。所有案例验证普通编辑版本发生变化，不能借执行归一化放宽编辑确认。

本地隔离证据：`artifacts/tests-empty-progress/{red,targeted-final,default,m5}.trx`。默认全量运行时，之后仅修订说明注释及测试名称；M5 使用最终源码。

## 独立审查

DeepSeekHelper / deepseek-v4-pro，thinking=disabled，共两轮。首轮提出的引号值、匹配位置、混合换行和注释测试疑点逐项对照源码：引号值无法匹配；位置不影响其他字节冻结；混合换行补测属于可选项；注释变化案例明确预期 Stopped，并非审查所称 Succeeded。采纳说明注释和测试命名改进，随后提交最终实际 diff 复审。

复审结论：No verified defect remains。复审文本对案例数量和无注释 CRLF 的个别描述不准确，不作为验证依据；以实际正则、用例预期及测试结果为准。没有经核实仍待修复的问题。

原始响应仅保存在 ignored artifacts/deepseek-empty-progress：

- review-1.json SHA256 `E2F83F29D993D140BB0B9C2F9DE9F435C43796AE01B5B82AD3253A3EC1A1D95D`
- review-2.json SHA256 `F459DB63B1A90888715A4606602E0C597FA3707A704ADB4F5BCAD6B90A29D88B`

实际游戏连续执行尚未验收；自动化测试通过不等于真实游戏验收。
