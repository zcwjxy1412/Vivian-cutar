> **历史文档**：M1 阶段的 PR 说明草稿，作为 M1 验收材料保留（`M1_ACCEPTANCE.md` 引用）。其中“无远程 PR”等描述只对当时成立；M1 已经合并，当前状态见 [`../README.md`](../README.md)。

# PR：建立VIvian M1桌面基础工程

新增.NET 10/WPF桌面壳、三游戏路径配置、SQLite持久化、Serilog日志和基础自检。配置保存使用revision与事务防止多窗口覆盖，损坏/未来版本数据库不会静默重建。

真实游戏控制尚未接入。基础自检只验证VIvian自身，不写游戏完成状态。添加隔离xUnit测试和Windows构建、发布、WPF启动冒烟工作流。

验证详情见M1_ACCEPTANCE.md及原始日志；Windows视觉/高DPI待验收。本地feature/m1-foundation，无远程PR。
