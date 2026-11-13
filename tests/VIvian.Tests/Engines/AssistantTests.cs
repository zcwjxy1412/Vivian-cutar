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

    [Theory]
    [InlineData(GameId.Genshin, "genshin")]
    [InlineData(GameId.StarRail, "starrail")]
    [InlineData(GameId.ZenlessZoneZero, "zzz")]
    public async Task AccountDiscoveryUsesSafeSlotsAndSavedSelectionWithoutConfigurationProvider(GameId game, string key)
    {
        var db = new LocalDatabase(DbPath);
        await db.InitializeAsync();
        var account = await db.AddAccountAsync(game, "秘密名称 138****2468", "秘密区服", "987654321");
        var otherGame = game == GameId.Genshin ? GameId.StarRail : GameId.Genshin;
        var other = await db.AddAccountAsync(otherGame, "不应出现的账号", "", null);
        var tools = new AssistantTools(() => [], _ => Task.FromResult<IReadOnlyList<RunRecord>>([]), db);
        foreach (var selected in new[] { false, true })
        {
            account = await db.SaveAccountAsync(account with { AutoRun = selected, LoginState = "13812342468" });
            var output = (await tools.InvokeAsync("list_accounts", new JsonObject { ["game"] = key }.ToJsonString(), Ct)).Content;
            var rows = JsonNode.Parse(output)!.AsArray();
            var row = Assert.Single(rows, r => r!["account_id"]!.GetValue<Guid>() == account.Id)!;
            Assert.All(rows, r => Assert.Equal(key, r!["game"]!.GetValue<string>()));
            Assert.DoesNotContain(other.Id.ToString(), output);
            Assert.Equal(account.Order + 1, row["slot"]!.GetValue<int>());
            Assert.Equal(selected, row["saved_auto_run"]!.GetValue<bool>());
            Assert.Equal(selected, row["AutoRun"]!.GetValue<bool>());
            Assert.Equal(new[] { "AutoRun", "Enabled", "account_id", "game", "last_result", "saved_auto_run", "slot", "verification" },
                row.AsObject().Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());
        }
        using var handler = new AccountDiscoveryModel(key);
        using var http = new HttpClient(handler);
        var session = new AssistantSession(new ChatCompletionsClient(http), tools, "测试助手");
        Assert.Equal("请选择游戏和账号序号。", await session.AskAsync(Endpoint, "查看账号", Ct));
        Assert.Equal(3, handler.Requests);
    }

    private sealed class AccountDiscoveryModel(string game) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var messages = body["messages"]!.ToJsonString(new System.Text.Json.JsonSerializerOptions
            { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            foreach (var secret in new[] { "秘密名称", "138****2468", "13812342468", "987654321", "秘密区服" })
                Assert.DoesNotContain(secret, messages);
            var call = ++Requests == 1 ? "list_games" : "list_accounts";
            var reply = Requests == 3
                ? new JsonObject { ["role"] = "assistant", ["content"] = "请选择游戏和账号序号。" }
                : new JsonObject { ["role"] = "assistant", ["tool_calls"] = new JsonArray(new JsonObject
                {
                    ["id"] = call, ["type"] = "function", ["function"] = new JsonObject
                    { ["name"] = call, ["arguments"] = Requests == 1 ? "{}" : new JsonObject { ["game"] = game }.ToJsonString() }
                }) };
            return new(HttpStatusCode.OK) { Content = new StringContent(new JsonObject
            { ["choices"] = new JsonArray(new JsonObject { ["message"] = reply, ["finish_reason"] = Requests == 3 ? "stop" : "tool_calls" }) }.ToJsonString()) };
        }
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
        var toolReadback = JsonNode.Parse((await tools.InvokeAsync("read_configuration", TargetArgs(a.Id), Ct)).Content)!;
        Assert.Equal(a.Id.ToString("D"), toolReadback["account_id"]!.GetValue<string>());
        Assert.Equal("starrail", toolReadback["game"]!.GetValue<string>());
        Assert.Equal(applied.Revision, toolReadback["revision"]!.GetValue<string>());
        Assert.Equal("饰品提取", toolReadback["values"]!.AsArray()
            .Single(v => v!["Path"]!.GetValue<string>() == "instance_type")!["value"]!.GetValue<string>());
        Assert.Empty((await db.LoadAccountSettingsAsync(b.Id)).Values);
        Assert.Equal(before, File.ReadAllText(Config));
        Assert.Equal(("{}", "{\"instance_type\":\"\\u9970\\u54C1\\u63D0\\u53D6\"}"), await ReadAuditAsync(applied.AuditId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.ApplyConfirmedAsync(card.Id, _ => true));
    }

    [Fact]
    public async Task TruncatedFinalReplyDoesNotPublishOrWritePreparedProposal()
    {
        var (db, _, tools, a, _) = await SetupAsync();
        using var http = new HttpClient(new ScriptedModel(a.Id, "length"));
        var session = new AssistantSession(new ChatCompletionsClient(http), tools, "芙宁娜");
        await Assert.ThrowsAsync<AssistantException>(() => session.AskAsync(Endpoint, "修改配置", Ct));
        Assert.Empty(session.LastProposals);
        Assert.Empty(session.LastRunPlans);
        Assert.Empty((await db.LoadAccountSettingsAsync(a.Id)).Values);
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

    [Fact]
    public async Task DiagnosticsUseStoredRunLinksAndOmitRawAccountAndFailureDetails()
    {
        var (db, _, tools, a, b) = await SetupAsync();
        var batch = await db.BeginBatchAsync(GameId.StarRail);
        var runId = Guid.NewGuid();
        const string secret = "private-path-and-credentials";
        await db.PublishAsync(new RunUpdate(runId, Guid.NewGuid(), GameId.StarRail, "test", false,
            RunPhase.Finished, RunOutcome.Failed, CleanupState.Confirmed, ReasonCodes.ReportedFailure, DateTimeOffset.UtcNow), Ct);
        await db.RecordAccountResultAsync(batch.Id, a.Id, runId, "failed", secret);

        var run = Assert.Single(JsonNode.Parse((await tools.InvokeAsync("get_run_status", TargetArgs(a.Id), Ct)).Content)!.AsArray());
        Assert.Equal(runId.ToString("D"), run!["run_id"]!.GetValue<string>());
        Assert.Equal("Confirmed", run["cleanup"]!.GetValue<string>());
        Assert.Empty(JsonNode.Parse((await tools.InvokeAsync("get_run_status", TargetArgs(b.Id), Ct)).Content)!.AsArray());
        var progress = (await tools.InvokeAsync("get_batch_progress", "{}", Ct)).Content;
        Assert.Contains(a.Id.ToString("D"), progress);
        Assert.DoesNotContain(secret, progress);
        Assert.DoesNotContain(a.Name, progress);
    }

    [Theory]
    [InlineData("genshin", "genshin.daily_rewards")]
    [InlineData("zzz", "engagement_reward")]
    public async Task IntegratedTaskCatalogsRemainReadOnly(string game, string task)
    {
        var tools = new AssistantTools(() => [], _ => Task.FromResult<IReadOnlyList<RunRecord>>([]));
        var result = await tools.InvokeAsync("list_tasks", new JsonObject { ["game"] = game }.ToJsonString(), Ct);
        var tasks = JsonNode.Parse(result.Content)!.AsArray();
        Assert.Contains(tasks, t => t!["Id"]!.GetValue<string>() == task);
        Assert.All(tasks, t => Assert.False(t!["can_run_for_real"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task ExactBatchProgressDoesNotFallBackToRecentBatches()
    {
        var (db, _, tools, _, _) = await SetupAsync();
        var wanted = await db.BeginBatchAsync(GameId.StarRail);
        for (var i = 0; i < 6; i++) await db.BeginBatchAsync(GameId.Genshin);
        string Args(string id) => new JsonObject { ["batch_id"] = id }.ToJsonString();
        var result = await tools.InvokeAsync("get_batch_progress", Args(wanted.Id.ToString("D")), Ct);
        var batch = Assert.Single(JsonNode.Parse(result.Content)!.AsArray());
        Assert.Equal(wanted.Id.ToString("D"), batch!["batch_id"]!.GetValue<string>());
        Assert.Empty(batch["account_results"]!.AsArray());
        foreach (var id in new[] { Guid.NewGuid().ToString("D"), Guid.Empty.ToString("D"), "bad", wanted.Id.ToString("N") })
            Assert.NotNull(JsonNode.Parse((await tools.InvokeAsync("get_batch_progress", Args(id), Ct)).Content)!["error"]);
        foreach (var args in new[] { "{\"batch_id\":null}", "{\"batch_id\":123}" })
            Assert.NotNull(JsonNode.Parse((await tools.InvokeAsync("get_batch_progress", args, Ct)).Content)!["error"]);
        Assert.Equal(5, JsonNode.Parse((await tools.InvokeAsync("get_batch_progress", "{}", Ct)).Content)!.AsArray().Count);
    }

    [Fact]
    public async Task StarRailRunPlansAreOptInReadOnlyAndHaveASingleDurableActionId()
    {
        File.AppendAllText(Config, "use_fuel: false\nuse_reserved_trailblaze_power: false\nbreak_down_level_four_relicset: false\n");
        var (db, provider, tools, a, _) = await SetupAsync();
        await db.SaveLoginProfileAsync(a.Id, "123****1234", "123****1234", 0, 1, null, "fixture", Ct);
        var target = new AccountConfigurationTarget(GameId.StarRail, a.Id);
        var snapshot = await provider.ReadAsync(target, Ct);
        var args = new JsonObject
        {
            ["game"] = "starrail", ["account_id"] = a.Id.ToString("D"), ["revision"] = snapshot.Revision,
            ["task_id"] = "routine", ["resource_policy"] = "saved_account_settings",
        };
        Assert.DoesNotContain("propose_run", tools.Definitions().ToJsonString());
        Assert.Null((await tools.InvokeAsync("propose_run", args.ToJsonString(), Ct)).RunPlan);

        tools.ScheduleProposalsEnabled = true;
        Assert.Contains("propose_run", tools.Definitions().ToJsonString());
        args["resource_policy"] = "spend_everything";
        Assert.Null((await tools.InvokeAsync("propose_run", args.ToJsonString(), Ct)).RunPlan);
        args["resource_policy"] = "saved_account_settings";
        var result = await tools.InvokeAsync("propose_run", args.ToJsonString(), Ct);
        var plan = Assert.IsType<AssistantRunPlan>(result.RunPlan);
        Assert.Equal(target, plan.Target);
        Assert.Contains("燃料：禁止", plan.Resources);
        Assert.Empty(await db.LoadRecentBatchesAsync());

        var submitted = await db.BeginBatchAsync(GameId.StarRail, actionId: plan.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.BeginBatchAsync(GameId.StarRail, actionId: plan.Id));
        Assert.Equal(submitted, Assert.Single(await db.LoadRecentBatchesAsync()));

        foreach (var replacement in new[] { "break_down_level_four_relicset: true", "# absent" })
        {
            var original = File.ReadAllText(Config);
            File.WriteAllText(Config, original.Replace("break_down_level_four_relicset: false", replacement));
            args["revision"] = (await provider.ReadAsync(target, Ct)).Revision;
            var blocked = await tools.InvokeAsync("propose_run", args.ToJsonString(), Ct);
            Assert.Null(blocked.RunPlan);
            Assert.Contains("分解遗器", blocked.Content);
            File.WriteAllText(Config, original);
        }
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

    private sealed class ScriptedModel(Guid accountId, string? finalFinishReason = null) : HttpMessageHandler
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
                Content = new StringContent(new JsonObject { ["choices"] = new JsonArray(new JsonObject
                { ["message"] = reply, ["finish_reason"] = _request == 5 ? finalFinishReason : null }) }.ToJsonString()),
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
