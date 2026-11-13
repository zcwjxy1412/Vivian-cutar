using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VIvian.Infrastructure;
using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Engines.BetterGi;
using VIvian.Infrastructure.Engines.OneDragon;

namespace VIvian.Infrastructure.Assistant;

/// <summary>Live state the tools may read. Supplied by the app; the assistant never touches files directly.</summary>
public sealed record EngineStatusView(GameId Game, string Name, string Engine, string Version, string State, bool CanRunForReal);

public sealed record ProposalView(Guid Id, AccountConfigurationTarget Target, string AccountName,
    IReadOnlyList<(string Title, string Old, string New)> Changes, string Reason);

public sealed record AssistantRunPlan(Guid Id, AccountConfigurationTarget Target, string AccountName,
    string ConfigurationRevision, long TaskPlanRevision, string Resources, DateTimeOffset Expires)
{
    public const string TaskId = "routine";
    public const string ResourcePolicy = "saved_account_settings";
}

/// <summary>
/// The structured operations the assistant may call. Read-only, except <c>propose_changes</c>, which only
/// prepares a validated proposal: writing it requires the user to press "确认修改" in the window.
/// </summary>
public sealed class AssistantTools(Func<IReadOnlyList<EngineStatusView>> engines,
    Func<CancellationToken, Task<IReadOnlyList<RunRecord>>> history, LocalDatabase? accountDatabase = null,
    IEnumerable<IAccountConfigurationProvider>? configurationProviders = null, IExecutionPlanService? executionPlans = null)
{
    private readonly Dictionary<GameId, IAccountConfigurationProvider> _configuration =
        (configurationProviders ?? []).ToDictionary(provider => provider.Game);
    private readonly object _proposalLock = new();
    private readonly Dictionary<Guid, PendingProposal> _proposals = [];

    internal sealed record PendingProposal(ProposalView View, AccountConfigurationProposal Proposal, DateTimeOffset Expires);
    internal sealed record ToolResult(string Content, PendingProposal? Proposal = null, AssistantRunPlan? RunPlan = null,
        PreparedExecutionPlan? ExecutionPlan = null);
    private volatile bool _scheduleProposalsEnabled;
    public bool ScheduleProposalsEnabled
    {
        get => _scheduleProposalsEnabled;
        set => _scheduleProposalsEnabled = value;
    }

    internal IReadOnlyList<ProposalView> Publish(IReadOnlyList<PendingProposal> pending)
    {
        lock (_proposalLock)
        {
            foreach (var id in _proposals.Where(p => p.Value.Expires <= DateTimeOffset.UtcNow).Select(p => p.Key).ToArray())
                _proposals.Remove(id);
            foreach (var item in pending) _proposals.Add(item.View.Id, item);
            while (_proposals.Count > 32) _proposals.Remove(_proposals.First().Key);
            return pending.Select(p => p.View).ToArray();
        }
    }

    public JsonArray Definitions()
    {
        var definitions = JsonNode.Parse("""
    [
     {"type":"function","function":{"name":"list_games","description":"先查询三款游戏、对应引擎、固定版本、安装状态和执行能力，再用 list_accounts(game) 发现各游戏的内部账号 ID。","parameters":{"type":"object","properties":{}}}},
     {"type":"function","function":{"name":"list_tasks","description":"列出某款游戏的任务目录（每日/每周/按需）及当前能否真实执行。","parameters":{"type":"object","properties":{"game":{"type":"string","enum":["genshin","starrail","zzz"]}},"required":["game"]}}},
     {"type":"function","function":{"name":"read_configuration","description":"读取支持配置查询的游戏和内部账号的登记配置、版本和取值规则。配置修改及旧单星铁计划需要此步骤；新 items 计划不要求先调用此工具。账号 ID 由 list_accounts 获取。","parameters":{"type":"object","properties":{"game":{"type":"string","enum":["genshin","starrail","zzz"]},"account_id":{"type":"string"}},"required":["game","account_id"],"additionalProperties":false}}},
     {"type":"function","function":{"name":"propose_changes","description":"用刚读取的同一游戏、账号和 revision 生成提案。只能使用 read_configuration 登记的参数路径和字符串值，写入需本机确认。","parameters":{"type":"object","properties":{"game":{"type":"string","enum":["genshin","starrail","zzz"]},"account_id":{"type":"string"},"revision":{"type":"string"},"changes":{"type":"object","additionalProperties":{"type":"string"}}},"required":["game","account_id","revision","changes"],"additionalProperties":false}}},
     {"type":"function","function":{"name":"get_run_status","description":"读取真实记录或有标记的模拟结果。按账号查询时同时提供 game 和 account_id，尚未关联的运行不会猜测归属。","parameters":{"type":"object","properties":{"game":{"type":"string","enum":["genshin","starrail","zzz"]},"account_id":{"type":"string"},"limit":{"type":"integer","minimum":1,"maximum":20}},"additionalProperties":false}}}
     ,{"type":"function","function":{"name":"list_accounts","description":"列出指定游戏的内部账号 ID、slot 序号和核对状态，不依赖配置提供者。用游戏+slot作为安全账号称呼；目标不明确时先询问用户，不猜测姓名或登录身份。saved_auto_run 是已保存的日常勾选状态，旧键 AutoRun 与其相同，均不代表界面未保存的当前选择。列出不代表可执行。","parameters":{"type":"object","properties":{"game":{"type":"string","enum":["genshin","starrail","zzz"]}},"required":["game"],"additionalProperties":false}}}
     ,{"type":"function","function":{"name":"get_batch_progress","description":"读取批次进度。提供 batch_id 时只查该批次（星铁运行计划的 action_id 即 batch_id）；未知不回退，不代表可重新提交。省略时读取最近批次。","parameters":{"type":"object","properties":{"batch_id":{"type":"string"}},"additionalProperties":false}}}
    ]
    """)!.AsArray();
        if (ScheduleProposalsEnabled)
            definitions.Add(JsonNode.Parse("""
                {"type":"function","function":{"name":"propose_run","description":"为已绑定的单个星铁账号准备日常计划。必须先读取目标配置并用其 revision。计划需用户本机确认。",
                "parameters":{"type":"object","properties":{"game":{"type":"string","enum":["starrail"]},"account_id":{"type":"string"},
                "revision":{"type":"string"},"task_id":{"type":"string","enum":["routine"]},
                "resource_policy":{"type":"string","enum":["saved_account_settings"]}},
                "required":["game","account_id","revision","task_id","resource_policy"],"additionalProperties":false}}}
                """));
        var progress = definitions.Single(d => d!["function"]!["name"]!.GetValue<string>() == "get_batch_progress")!["function"]!;
        progress["parameters"]!["properties"]!["plan_id"] = new JsonObject { ["type"] = "string" };
        progress["description"] = "精确查询 plan_id 持久计划或 batch_id 旧批次，二者不能同时提供。未知不能自动重发。省略两者仅返回最近批次概览。";
        if (ScheduleProposalsEnabled && executionPlans is not null)
        {
            var function = definitions.Single(d => d!["function"]!["name"]!.GetValue<string>() == "propose_run")!["function"]!;
            function["description"] = "准备待确认计划。先 list_games，再 list_accounts(game)，让用户按游戏+slot辨认，歧义时先询问。新计划使用有序 items，只给游戏和内部账号 ID，不要求先 read_configuration，任务/资源/版本由宿主核对。旧单星铁五字段仍须先读取配置和 revision，不能混用。固定失败即停，不直接执行。";
            var schema = function["parameters"]!.AsObject();
            schema["properties"]!["items"] = JsonNode.Parse("""
                {"type":"array","minItems":1,"maxItems":32,"items":{"type":"object",
                "properties":{"game":{"type":"string","enum":["genshin","starrail","zzz"]},"account_id":{"type":"string"}},
                "required":["game","account_id"],"additionalProperties":false}}
                """);
            schema["oneOf"] = new JsonArray(new JsonObject { ["required"] = schema["required"]!.DeepClone() },
                new JsonObject { ["required"] = new JsonArray("items") });
            schema.Remove("required");
        }
        return definitions;
    }

    public void RevokeProposals()
    {
        lock (_proposalLock) _proposals.Clear();
    }

    internal async Task<ToolResult> InvokeAsync(string name, string argumentsJson, CancellationToken ct)
    {
        if (argumentsJson.Length > 16_384) return new(Error("工具参数过长。"));
        try
        {
            ct.ThrowIfCancellationRequested();
            if (JsonNode.Parse(argumentsJson, documentOptions: new() { MaxDepth = 12 }) is not JsonObject args)
                return new(Error("工具参数必须是 JSON 对象。"));
            var definition = Definitions().FirstOrDefault(d => ChatCompletionsClient.StringValue(d?["function"]?["name"]) == name);
            if (definition is null) return new(Error("没有这个工具。"));
            var schema = definition["function"]!["parameters"]!;
            var properties = schema["properties"]!.AsObject();
            if (args.Any(p => !properties.ContainsKey(p.Key) || !MatchesType(p.Value, properties[p.Key]!))
                || (schema["required"] is JsonArray required && required.Any(p => !args.ContainsKey(p!.GetValue<string>()))))
                return new(Error("工具参数缺失、类型不符或包含未知字段。"));
            if (name == "propose_changes") return await ProposeAsync(args, ct).ConfigureAwait(false);
            if (name == "propose_run") return await ProposeRunAsync(args, ct).ConfigureAwait(false);
            return new(name switch
            {
                "list_games" => Serialize(engines().Select(e => new
                { game = Key(e.Game), e.Name, e.Engine, e.Version, e.State, can_run_for_real = e.CanRunForReal,
                    account_configuration = _configuration.ContainsKey(e.Game),
                    ai_run_proposals = (executionPlans is not null || e.Game == GameId.StarRail) && e.CanRunForReal && ScheduleProposalsEnabled })),
                "list_tasks" => ListTasks(ParseGame(args)),
                "read_configuration" => await ReadConfigurationAsync(ParseTarget(args), ct).ConfigureAwait(false),
                "get_run_status" => await RunStatusAsync(args, ct).ConfigureAwait(false),
                "list_accounts" => await ListAccountsAsync(ParseGame(args), ct).ConfigureAwait(false),
                "get_batch_progress" => await BatchProgressAsync(args, ct).ConfigureAwait(false),
                _ => Error("没有这个工具。"),
            });
        }
        catch (JsonException) { return new(Error("工具参数或配置不是有效的 JSON。")); }
        catch (SettingsConflictException) { return new(Error("配置版本已变化，请重新读取指定账号的配置。")); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException
            or UnauthorizedAccessException or KeyNotFoundException or Microsoft.Data.Sqlite.SqliteException)
        { return new(Error("工具无法读取目标或校验参数，请检查游戏、账号及安装配置。")); }
    }

    private static bool MatchesType(JsonNode? value, JsonNode schema) => schema["type"]!.GetValue<string>() switch
    {
        "string" => ChatCompletionsClient.StringValue(value) is { Length: > 0 } s
            && (schema["enum"] is not JsonArray choices || choices.Any(c => c!.GetValue<string>() == s)),
        "integer" => value is JsonValue number && number.TryGetValue<int>(out var n)
            && n >= schema["minimum"]!.GetValue<int>() && n <= schema["maximum"]!.GetValue<int>(),
        "object" => value is JsonObject obj && (schema["properties"] is JsonObject properties
            ? obj.All(p => properties.ContainsKey(p.Key) && MatchesType(p.Value, properties[p.Key]!))
                && (schema["required"] is not JsonArray required || required.All(p => obj.ContainsKey(p!.GetValue<string>())))
            : obj.All(p => ChatCompletionsClient.StringValue(p.Value) is not null)),
        "array" => value is JsonArray array && array.Count >= schema["minItems"]!.GetValue<int>()
            && array.Count <= schema["maxItems"]!.GetValue<int>() && array.All(v => MatchesType(v, schema["items"]!)),
        _ => false,
    };

    public async Task<AccountConfigurationApplied> ApplyConfirmedAsync(Guid proposalId,
        Func<AccountConfigurationTarget, bool> stillAllowed, CancellationToken ct = default)
    {
        PendingProposal? pending;
        lock (_proposalLock) _proposals.Remove(proposalId, out pending);
        if (pending is null || pending.Expires <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("这个修改提案已失效，请让助手重新生成。");
        var proposal = pending.Proposal;
        return await _configuration[proposal.Target.Game].ApplyAsync(proposal, () => stillAllowed(proposal.Target), ct)
            .ConfigureAwait(false);
    }

    private string ListTasks(GameId game)
    {
        if (game == GameId.ZenlessZoneZero)
            return Serialize(OneDragonTaskPlans.Catalog.Select(t => new
            {
                t.Id, title = t.DisplayName, uses_stamina_plan = t.UsesStaminaPlan, can_run_for_real = false,
            }));

        var tasks = game == GameId.Genshin ? BetterGiTaskCatalog.Tasks : EngineCatalog.For(game).Tasks;
        var available = engines().Any(e => e.Game == game && e.CanRunForReal);
        return Serialize(tasks.Select(t => new
        {
            t.Id, t.Title, frequency = t.Frequency.ToString(),
            can_run_for_real = game == GameId.StarRail && available && RealExecutionPolicy.AllowsTask(t.Id),
            t.Note, t.Preconditions,
        }));
    }

    private async Task<string> ReadConfigurationAsync(AccountConfigurationTarget target, CancellationToken ct)
    {
        if (!_configuration.TryGetValue(target.Game, out var provider)) return UnsupportedConfiguration();
        var snapshot = await provider.ReadAsync(target, ct).ConfigureAwait(false);
        return Serialize(new
        {
            game = Key(target.Game), account_id = target.AccountId, revision = snapshot.Revision,
            write_unavailable_reason = snapshot.WriteUnavailableReason,
            values = snapshot.Values.Select(v => new
            {
                v.Schema.Path, v.Schema.Title, value = v.Value,
                editable = snapshot.WriteUnavailableReason is null && v.Schema.Access == ParameterAccess.VerifiedReadWrite && v.Value is not null,
                rule = ParameterRules.Describe(v.Schema), options = ParameterRules.LabelledOptions(v.Schema).Select(o => o.Value),
                v.Schema.Description, v.Schema.Dependencies,
            }),
        });
    }

    private async Task<ToolResult> ProposeAsync(JsonObject args, CancellationToken ct)
    {
        var target = ParseTarget(args);
        if (!_configuration.TryGetValue(target.Game, out var provider)) return new(UnsupportedConfiguration());
        var revision = args["revision"]!.GetValue<string>();
        if (revision.Length > 256) return new(Error("配置版本格式无效。"));
        if (args["changes"] is not JsonObject changes || changes.Count is 0 or > 20)
            return new(Error("请提供 1 至 20 项修改。"));
        var snapshot = await provider.ReadAsync(target, ct).ConfigureAwait(false);
        if (snapshot.Revision != revision) return new(Error("配置版本已变化，请重新读取指定账号的配置。"));
        if (snapshot.WriteUnavailableReason is { } reason) return new(Error(reason));

        var proposal = await provider.ProposeAsync(target, revision, changes.ToDictionary(p => p.Key, p => p.Value!.GetValue<string>()), ct)
            .ConfigureAwait(false);
        if (!proposal.Validation.Valid) return new(Serialize(new { valid = false, reason = proposal.Validation.Reason }));
        if (proposal.Target != target || proposal.Revision != revision || proposal.Id == Guid.Empty || proposal.Changes.Count == 0)
            return new(Error("提供者没有返回匹配目标的有效提案。"));

        var view = new ProposalView(proposal.Id, target, snapshot.AccountName, proposal.Changes.Select(c =>
            (snapshot.Values.Single(v => v.Schema.Path == c.Path).Schema.Title, c.OldValue, c.NewValue)).ToArray(), proposal.Validation.Reason);
        return new(Serialize(new
        {
            proposal_id = proposal.Id, game = Key(target.Game), account_id = target.AccountId, revision, valid = true,
            changes = proposal.Changes.Select(c => new { c.Path, from = c.OldValue, to = c.NewValue }),
            note = "尚未写入。本轮成功结束后才会显示确认卡。",
        }), new(view, proposal, DateTimeOffset.UtcNow.AddMinutes(10)));
    }

    private static string UnsupportedConfiguration() => Error("此游戏的账号配置提供者尚未验收，当前可查询游戏和任务能力。");

    private async Task<ToolResult> ProposeRunAsync(JsonObject args, CancellationToken ct)
    {
        if (args.ContainsKey("items")) return await PrepareExecutionPlanAsync(args, ct).ConfigureAwait(false);
        if (args.Count != 5 || new[] { "game", "account_id", "revision", "task_id", "resource_policy" }.Any(k => !args.ContainsKey(k)))
            return new(Error("单星铁计划需要完整五字段，不能与 items 混用。"));
        if (!ScheduleProposalsEnabled || accountDatabase is null || !_configuration.TryGetValue(GameId.StarRail, out var provider)
            || !engines().Any(e => e.Game == GameId.StarRail && e.CanRunForReal))
            return new(Error("本机未开放星铁运行计划。"));
        var target = ParseTarget(args);
        var snapshot = await provider.ReadAsync(target, ct).ConfigureAwait(false);
        if (snapshot.Revision != args["revision"]!.GetValue<string>())
            return new(Error("配置版本已变化，请重新读取。"));
        if (snapshot.WriteUnavailableReason is not null)
            return new(Error("只有已登记且可核对的独立星铁账号支持运行计划。"));

        var plan = await accountDatabase.LoadAccountTaskPlanAsync(target.AccountId, ct).ConfigureAwait(false);
        var profiles = await accountDatabase.LoadLoginProfilesAsync(GameId.StarRail, ct).ConfigureAwait(false);
        if (!plan.DailyEnabled || !profiles.Any(p => p.AccountId == target.AccountId))
            return new(Error("目标账号须已绑定登录界面，并启用日常任务。"));
        string? Value(string path) => snapshot.Values.SingleOrDefault(v => v.Schema.Path == path)?.Value;
        var fuel = Value("use_fuel");
        var reserve = Value("use_reserved_trailblaze_power");
        if (Value("break_down_level_four_relicset") != "false")
            return new(Error("需确认每周分解遗器已关闭，才能生成 AI 运行计划。"));
        if (fuel is not ("true" or "false") || reserve is not ("true" or "false"))
            return new(Error("无法核对燃料或后备开拓力策略，暂不能生成运行计划。"));
        var resources = $"按账号已保存的副本策略清体力。燃料：{(fuel == "true" ? "允许" : "禁止")}。后备开拓力：{(reserve == "true" ? "允许" : "禁止")}。每周分解遗器：关闭。";
        var run = new AssistantRunPlan(Guid.NewGuid(), target, snapshot.AccountName, snapshot.Revision,
            plan.Revision, resources, DateTimeOffset.UtcNow.AddMinutes(5));
        return new(Serialize(new
        {
            action_id = run.Id, game = Key(target.Game), account_id = target.AccountId,
            task_id = AssistantRunPlan.TaskId, resource_policy = AssistantRunPlan.ResourcePolicy, use_fuel = fuel == "true",
            use_reserved_trailblaze_power = reserve == "true",
            note = "计划尚未执行。用户必须查看目标、任务和资源策略，并在本机确认后才能提交。",
        }), RunPlan: run);
    }

    private async Task<ToolResult> PrepareExecutionPlanAsync(JsonObject args, CancellationToken ct)
    {
        if (!ScheduleProposalsEnabled || executionPlans is null || args.Count != 1 || args["items"] is not JsonArray items)
            return new(Error("有序计划不可用或混用了旧计划字段。"));
        var requests = items.Select(i => ParseTarget(i!.AsObject()))
            .Select(t => new ExecutionPlanRequestItem(t.Game, t.AccountId)).ToArray();
        if (requests.Distinct().Count() != requests.Length) return new(Error("同一游戏账号不能重复。"));
        var prepared = await executionPlans.PrepareAsync(requests, ct).ConfigureAwait(false);
        if (prepared.Plan is not { } plan) return new(Serialize(new { error = "宿主未允许此计划。", reason_code = prepared.ReasonCode }));
        if (plan.PlanId == Guid.Empty || plan.ExpiresAt <= DateTimeOffset.UtcNow || plan.Items.Count != requests.Length
            || plan.Items.Where((item, index) => item.Ordinal != index || item.Game != requests[index].Game || item.AccountId != requests[index].AccountId
                || item.TaskId != "routine" || item.ResourcePolicy != "saved_account_settings" || string.IsNullOrWhiteSpace(item.ConfigurationRevision)
                || string.IsNullOrWhiteSpace(item.AccountName) || string.IsNullOrWhiteSpace(item.ResourceSummary)).Any())
            return new(Error("宿主准备结果与请求目标或受支持策略不匹配。"));
        // Detach the displayed/confirmed snapshot from mutable service collections.
        plan = plan with { Items = plan.Items.ToArray() };
        return new(Serialize(new { plan_id = plan.PlanId, expires_at = plan.ExpiresAt, failure_policy = "stop_on_failure",
            items = plan.Items.Select(i => new { ordinal = i.Ordinal, game = Key(i.Game), account_id = i.AccountId, task_id = i.TaskId,
                revision = i.ConfigurationRevision, task_plan_revision = i.TaskPlanRevision, resource_policy = i.ResourcePolicy, resource_summary = i.ResourceSummary }),
            note = "尚未执行；需用户按顺序核对并确认。" }), ExecutionPlan: plan);
    }

    private async Task<string> RunStatusAsync(JsonObject args, CancellationToken ct)
    {
        var limit = args["limit"]?.GetValue<int>() ?? 5;
        var game = args["game"] is null ? (GameId?)null : ParseGame(args);
        Guid? account = args["account_id"] is null ? null : ParseTarget(args).AccountId;
        if (account is not null && (accountDatabase is null
            || !(await accountDatabase.LoadAccountsAsync(game, ct).ConfigureAwait(false)).Any(a => a.Id == account)))
            return Error("没有此游戏的目标账号。");

        var runs = game is { } g && accountDatabase is not null
            ? await accountDatabase.ReadBoundRunsAsync(g, account, limit, ct).ConfigureAwait(false)
            : await history(ct).ConfigureAwait(false);
        return Serialize(runs.Where(r => game is null || r.Game == game).Take(limit).Select(r => new
        {
            run_id = r.RunId, game = Key(r.Game), account_id = account, phase = r.Phase.ToString(),
            outcome = r.Outcome?.ToString(), cleanup = r.Cleanup.ToString(), reason = RunPresentation.Reason(r.ReasonCode),
            simulation = r.IsSimulation, updated = r.UpdatedAt,
        }));
    }

    private async Task<string> ListAccountsAsync(GameId game, CancellationToken ct)
    {
        if (accountDatabase is null) return Error("账号数据尚未连接。");
        var accounts = await accountDatabase.LoadAccountsAsync(game, ct).ConfigureAwait(false);
        return Serialize(accounts.Take(100).Select(a => new
        {
            account_id = a.Id, game = Key(a.Game), slot = a.Order + 1, a.Enabled, a.AutoRun, saved_auto_run = a.AutoRun,
            verification = a.VerificationState is "verified" or "manual_verified" ? "verified" : "unverified",
            last_result = ResultCode(a.LastResult),
        }));
    }

    private async Task<string> BatchProgressAsync(JsonObject args, CancellationToken ct)
    {
        if (args.ContainsKey("plan_id"))
        {
            if (args.ContainsKey("batch_id") || !Guid.TryParseExact(ChatCompletionsClient.StringValue(args["plan_id"]), "D", out var id) || id == Guid.Empty)
                return Error("计划 ID 无效，且不能与批次 ID 同时查询。");
            if (executionPlans is null) return Error("持久计划查询尚未接入。");
            var receipt = await executionPlans.ReadAsync(id, ct).ConfigureAwait(false);
            if (receipt is null) return Error("未找到该计划；不能据此自动重新提交。");
            if (receipt.Plan.PlanId != id) return Error("回执标识不匹配，拒绝显示其他计划。");
            return Serialize(new { plan_id = id, state = receipt.State.ToString(), receipt.AcceptedAt, receipt.UpdatedAt, reason_code = receipt.ReasonCode,
                items = receipt.Items.Select(i => new { ordinal = i.Item.Ordinal, game = Key(i.Item.Game), account_id = i.Item.AccountId,
                    task_id = i.Item.TaskId, revision = i.Item.ConfigurationRevision, batch_id = i.ChildBatchId, run_ids = i.RunIds,
                    state = i.State.ToString(), outcome = i.Result?.Outcome.ToString(), cleanup = i.Result?.Cleanup.ToString(),
                    configuration_restored = i.Result?.ConfigurationRestored, reason_code = i.Result?.ReasonCode }) });
        }
        if (accountDatabase is null) return Error("账号数据尚未连接。");
        IReadOnlyList<AccountBatch> batches;
        if (args["batch_id"] is { } batchNode)
        {
            if (!Guid.TryParseExact(ChatCompletionsClient.StringValue(batchNode), "D", out var batchId) || batchId == Guid.Empty)
                return Error("批次 ID 格式无效。");
            var batch = await accountDatabase.LoadBatchAsync(batchId, ct).ConfigureAwait(false);
            if (batch is null) return Error("未找到该批次；不能据此自动重新提交。");
            batches = [batch];
        }
        else batches = await accountDatabase.LoadRecentBatchesAsync(5, ct).ConfigureAwait(false);
        var summaries = new List<object>();
        foreach (var batch in batches)
        {
            var results = await accountDatabase.LoadBatchResultsAsync(batch.Id, ct, limit: 20).ConfigureAwait(false);
            summaries.Add(new
            {
                batch_id = batch.Id, game = Key(batch.Game), current_account_id = batch.CurrentAccountId,
                state = batch.State is "running" or "finished" or "failed" or "stopped" or "interrupted" ? batch.State : "unknown",
                batch.StartedAt, batch.UpdatedAt, account_results = results.Select(r => new
                {
                    account_id = r.AccountId, run_id = r.RunId, result = ResultCode(r.Result), r.At,
                }),
            });
        }
        return Serialize(summaries);
    }

    private static string? ResultCode(string? code) => code switch
    {
        null => null,
        "success" or "failed" or "needs_user" or "login_screen_failed" or "login_profile_missing"
            or "identity_uncertain" or "switch_failed" or "uac_refused" or "input_host_unavailable" or "batch_exception" => code,
        _ => "unknown",
    };

    private static AccountConfigurationTarget ParseTarget(JsonObject args)
    {
        if (!Guid.TryParseExact(ChatCompletionsClient.StringValue(args["account_id"]), "D", out var id) || id == Guid.Empty)
            throw new ArgumentException("内部账号 ID 无效。");
        return new(ParseGame(args), id);
    }

    private static GameId ParseGame(JsonObject args) => (args["game"]?.GetValue<string>() ?? "") switch
    {
        "genshin" => GameId.Genshin, "starrail" => GameId.StarRail, "zzz" => GameId.ZenlessZoneZero,
        var other => throw new ArgumentException("未知游戏：" + other),
    };
    private static string Key(GameId g) => g switch { GameId.Genshin => "genshin", GameId.StarRail => "starrail", _ => "zzz" };
    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static string Serialize(object o) => JsonSerializer.Serialize(o, Json);
    private static string Error(string message) => Serialize(new { error = message });
}

/// <summary>Endpoint settings in the data directory. The key is encrypted for the current Windows user (DPAPI).</summary>
public sealed class AssistantSettingsStore(string dataDirectory)
{
    private string FilePath => Path.Combine(dataDirectory, "assistant.json");
    private sealed record Stored(string BaseUrl, string Model, string ProtectedKey);

    public AssistantEndpoint Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new("", "", "");
            var s = JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath, Encoding.UTF8));
            if (s is null) return new("", "", "");
            var key = string.IsNullOrEmpty(s.ProtectedKey) || !OperatingSystem.IsWindows() ? ""
                : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s.ProtectedKey), null, DataProtectionScope.CurrentUser));
            return new(s.BaseUrl ?? "", s.Model ?? "", key);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException or FormatException) { return new("", "", ""); }
    }

    public void Save(AssistantEndpoint endpoint)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("密钥加密只在 Windows 上可用。");
        Directory.CreateDirectory(dataDirectory);
        var protectedKey = endpoint.ApiKey.Length == 0 ? "" :
            Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(endpoint.ApiKey), null, DataProtectionScope.CurrentUser));
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new Stored(endpoint.BaseUrl.Trim(), endpoint.Model.Trim(), protectedKey)), Encoding.UTF8);
        File.Move(temp, FilePath, overwrite: true);
    }
}
