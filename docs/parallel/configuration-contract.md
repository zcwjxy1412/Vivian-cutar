# M6 公共配置接口

- `IAccountConfigurationProvider` 按 `GameId` 注册。原神和绝区零可先沿用现有引擎契约开发，提供者验收后单独注册。
- `AccountConfigurationTarget` 包含 `Game` 和 VIvian 内部 `AccountId`。提供者负责核对其自己的实例绑定。找不到目标或绑定不明确时返回明确错误。
- `ReadAsync` 返回不透明的 `Revision`。它须反映目标账号绑定、可写设置及其来源版本。宿主只把已登记参数、值和规则投影给模型。账号显示名只用于本机确认卡。
- `ProposeAsync` 须复查 revision，校验参数及取值，返回改前和改后。提案阶段只读。
- `ApplyAsync` 只由本机确认入口调用。复查目标、revision、运行和恢复状态，写入前检查 `stillAllowed`，保留改前备份和审计，回读核对后返回新 revision。相同提案 ID 不能写入两次。
- 星铁沿用首页的「固定版本上游默认值 + 账号独立设置」语义。旧共享配置只读。首次星铁写入使用 SQLite 事务，配置、改前备份和改后审计一起提交。

调用顺序示例

```csharp
var target = new AccountConfigurationTarget(game, accountId);
var view = await provider.ReadAsync(target, ct);
var proposal = await provider.ProposeAsync(target, view.Revision, changes, ct);
// 用户看过目标、前值和后值并明确确认后，由宿主调用。
var applied = await provider.ApplyAsync(proposal, stillAllowed, ct);
```
