using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Core.Execution;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Assistant;
using VIvian.Infrastructure.Engines;
using Xunit;

namespace VIvian.Tests.Engines;

public sealed class AssistantTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vivian-ai-").FullName;
    private string Config => Path.Combine(_root, "config.yaml");
    private string DbPath => Path.Combine(_root, "test.db");
    private static readonly AssistantEndpoint Endpoint = new("https://example.invalid/v1", "test-model", "sk-secret-123");
    private static readonly CancellationToken Ct = CancellationToken.None;

    public AssistantTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "assets", "config"));
        File.WriteAllText(Path.Combine(_root, "assets", "config", "version.txt"), "v2026.10.31\n");
        File.WriteAllText(Path.Combine(_root, "assets", "config", "instance_names.json"),
            """{"饰品提取":{"无":"跳过","永恒笑剧":""},"拟造花萼（金）":{"无":"跳过","回忆之蕾":"角色经验"}}""");
        File.WriteAllText(Config, "power_enable: true\ninstance_type: 拟造花萼（金）\ninstance_names:\n  拟造花萼（金）: 回忆之蕾\n  饰品提取: 永恒笑剧\n");
    }

    [Fact]
    public async Task ScriptedModelRoundTripProposesThenUserConfirmsOneAccountWithAuditAndReadback()
    {
        var (db, provider, tools, a, b) = await SetupAsync();
        var before = File.ReadAllText(Config);
        var handler = new ScriptedModel(a.Id);
        using var http = new HttpClient(handler);
        var session = new AssistantSession(new ChatCompletionsClient(http), tools, "芙宁娜");

        Assert.Equal("请确认修改。", await session.AskAsync(Endpoint, "给星铁测试账号 A 改成饰品提取。", Ct));
        var card = Assert.Single(session.LastProposals);
        Assert.Equal(new AccountConfigurationTarget(GameId.StarRail, a.Id), card.Target);
        Assert.Equal(a.Name, card.AccountName);
        Assert.Equal(("清体力副本类型", "拟造花萼（金）", "饰品提取"), Assert.Single(card.Changes));
        Assert.Empty((await db.LoadAccountSettingsAsync(a.Id)).Values);

        var applied = await tools.ApplyConfirmedAsync(card.Id, target => target.AccountId == a.Id);
        var current = await provider.ReadAsync(card.Target, Ct);
        Assert.Equal(applied.Revision, current.Revision);
        Assert.Equal("饰品提取", current.Values.Single(v => v.Schema.Path == "instance_type").Value);
        Assert.Empty((await db.LoadAccountSettingsAsync(b.Id)).Values);
        Assert.Equal(before, File.ReadAllText(Config));
        Assert.Equal(("{}", "{\"instance_type\":\"\\u9970\\u54C1\\u63D0\\u53D6\"}"), await ReadAuditAsync(applied.AuditId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.ApplyConfirmedAsync(card.Id, _ => true));
    }

    [Fact]
    public async Task WrongGameLegacyAndUnknownParametersCannotCreateAWrite()
    {
        var (db, provider, tools, a, _) = await SetupAsync();
        var legacy = (await db.LoadAccountsAsync(GameId.StarRail)).First();
        var legacyTarget = new AccountConfigurationTarget(GameId.StarRail, legacy.Id);
        var snapshot = await provider.ReadAsync(legacyTarget, Ct);
        Assert.Contains("只提供读取", snapshot.WriteUnavailableReason!);
        Assert.Null((await tools.InvokeAsync("propose_changes",
            ChangeArgs(legacy.Id, snapshot.Revision), Ct)).Proposal);
        var other = await db.AddAccountAsync(GameId.Genshin, "原神测试账号", "测试区", null);
        Assert.Contains("error", (await tools.InvokeAsync("read_configuration",
            TargetArgs(other.Id), Ct)).Content);
        Assert.Contains("尚未验收", (await tools.InvokeAsync("read_configuration",
            TargetArgs(other.Id, "genshin"), Ct)).Content);
        var aView = await provider.ReadAsync(new(GameId.StarRail, a.Id), Ct);
        Assert.Contains("error", (await tools.InvokeAsync("propose_changes",
            ChangeArgs(a.Id, aView.Revision, path: "password"), Ct)).Content);
    }

    [Fact]
    public async Task StaleAccountSelectionRevisionAndRuntimeBlockWrites()
    {
        var busy = false;
        var (db, provider, tools, a, b) = await SetupAsync(() => busy);
        var target = new AccountConfigurationTarget(GameId.StarRail, a.Id);
        async Task<ProposalView> ProposeAsync()
        {
            var view = await provider.ReadAsync(target, Ct);
            var result = await tools.InvokeAsync("propose_changes", ChangeArgs(a.Id, view.Revision), Ct);
            Assert.NotNull(result.Proposal);
            return Assert.Single(tools.Publish([result.Proposal]));
        }

        var staleSelection = await ProposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.ApplyConfirmedAsync(staleSelection.Id, t => t.AccountId == b.Id));
        var staleRevision = await ProposeAsync();
        var current = await db.LoadAccountSettingsAsync(a.Id);
        await db.SaveAccountSettingsAsync(current with { Values = new Dictionary<string, string> { ["power_enable"] = "true" } });
        await Assert.ThrowsAsync<SettingsConflictException>(() => tools.ApplyConfirmedAsync(staleRevision.Id, _ => true));

        var duringRun = await ProposeAsync();
        busy = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.ApplyConfirmedAsync(duringRun.Id, _ => true));
        Assert.False((await db.LoadAccountSettingsAsync(a.Id)).Values.ContainsKey("instance_type"));
        Assert.Empty((await db.LoadAccountSettingsAsync(b.Id)).Values);
    }

    [Fact]
    public async Task SqliteAuditAndSettingsRollBackTogetherWhenCommitGuardChanges()
    {
        var (db, provider, _, a, _) = await SetupAsync();
        var target = new AccountConfigurationTarget(GameId.StarRail, a.Id);
        var view = await provider.ReadAsync(target, Ct);
        var proposal = await provider.ProposeAsync(target, view.Revision,
            new Dictionary<string, string> { ["instance_type"] = "饰品提取" }, Ct);
        var checks = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ApplyAsync(proposal, () => ++checks < 3, Ct));
        Assert.Empty((await db.LoadAccountSettingsAsync(a.Id)).Values);
        Assert.Null(await ReadAuditAsync(proposal.Id));
    }

    private async Task<(LocalDatabase Db, StarRailAccountConfigurationProvider Provider, AssistantTools Tools, GameAccount A, GameAccount B)>
        SetupAsync(Func<bool>? busy = null)
    {
        var db = new LocalDatabase(DbPath);
        await db.InitializeAsync();
        var a = await db.AddAccountAsync(GameId.StarRail, "本机 A", "测试区", "123456789");
        var b = await db.AddAccountAsync(GameId.StarRail, "本机 B", "测试区", null);
        var provider = new StarRailAccountConfigurationProvider(db, new VersionedConfigurationControl(), () => _root, busy ?? (() => false));
        var tools = new AssistantTools(
            () => [new(GameId.StarRail, "崩坏：星穹铁道", "March7thAssistant", "v2026.10.31", "已核对", true)],
            _ => Task.FromResult<IReadOnlyList<RunRecord>>([]), db, [provider]);
        return (db, provider, tools, a, b);
    }

    private async Task<(string Before, string After)?> ReadAuditAsync(Guid id)
    {
        await using var connection = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT before_json,after_json FROM account_configuration_audit WHERE proposal_id=$id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    private static string TargetArgs(Guid id, string game = "starrail") =>
        new JsonObject { ["game"] = game, ["account_id"] = id.ToString("D") }.ToJsonString();

    private static string ChangeArgs(Guid id, string revision, string path = "instance_type") =>
        new JsonObject
        {
            ["game"] = "starrail", ["account_id"] = id.ToString("D"), ["revision"] = revision,
            ["changes"] = new JsonObject { [path] = "饰品提取" },
        }.ToJsonString();

    private sealed class ScriptedModel(Guid accountId) : HttpMessageHandler
    {
        private int _request;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("Bearer sk-secret-123", request.Headers.Authorization?.ToString());
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var messages = body["messages"]!.AsArray();
            var reply = ++_request switch
            {
                1 => Call("games", "list_games", "{}"),
                2 => Call("accounts", "list_accounts", """{"game":"starrail"}"""),
                3 => Call("read", "read_configuration", TargetArgs(accountId)),
                4 => Call("proposal", "propose_changes", ChangeArgs(accountId, Result(messages, "read")!["revision"]!.GetValue<string>())),
                5 => new JsonObject { ["role"] = "assistant", ["content"] = "请确认修改。" },
                _ => throw new InvalidOperationException("Unexpected model request."),
            };
            if (_request == 3)
            {
                var output = Result(messages, "accounts")!.ToJsonString();
                Assert.DoesNotContain("123456789", output);
                Assert.DoesNotContain("本机 A", output);
            }
            if (_request == 5) Assert.True(Result(messages, "proposal")!["valid"]!.GetValue<bool>());
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = reply }) }.ToJsonString()),
            };
        }

        private static JsonNode? Result(JsonArray messages, string callId) =>
            JsonNode.Parse(messages.Single(m => m!["tool_call_id"]?.GetValue<string>() == callId)!["content"]!.GetValue<string>());

        private static JsonObject Call(string id, string name, string args) => new()
        {
            ["role"] = "assistant",
            ["tool_calls"] = new JsonArray(new JsonObject
            {
                ["id"] = id, ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args },
            }),
        };
    }

    public void Dispose() => TestDirectories.DeleteWithRetry(_root);
}
