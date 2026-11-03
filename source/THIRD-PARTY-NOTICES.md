# 第三方依赖与发布边界

本M1源码仅包含VIvian自有代码，不捆绑BetterGI、March7thAssistant或OneDragon。三者名称仅说明计划兼容目标。本工程最终对外发布许可证待项目所有者确认，本预览不擅自添加“GPL且禁止商用”等矛盾条款。

主要直接依赖（完整传递依赖见packages.lock.json与artifacts中的dependency inventory）：

| 组件 | 主要许可证 | 来源 |
|---|---|---|
| .NET / WPF / Microsoft.Extensions.Hosting / Microsoft.Data.Sqlite | MIT（具体打包组件以自身通知为准） | https://github.com/dotnet |
| Serilog及Hosting/File扩展 | Apache-2.0 | https://github.com/serilog |
| xUnit | Apache-2.0 | https://github.com/xunit/xunit |
| Microsoft.NET.Test.Sdk | MIT | https://github.com/microsoft/vstest |
| SQLitePCLRaw | Apache-2.0；原生SQLite另有其自身声明 | https://github.com/ericsink/SQLitePCL.raw |

自包含运行时的LICENSE/ThirdPartyNotices应随发布包保留。此清单用于M1审查，不是完整法律合规或SBOM通过证明，M6需复核最终包的全部组件与许可证文本。
