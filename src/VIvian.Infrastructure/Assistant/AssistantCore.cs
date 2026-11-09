using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VIvian.Infrastructure;
using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Core.Execution;

namespace VIvian.Infrastructure.Assistant;

/// <summary>Live state the tools may read. Supplied by the app; the assistant never touches files directly.</summary>
public sealed record EngineStatusView(GameId Game, string Name, string Engine, string Version, string State, bool CanRunForReal);

public sealed record ProposalView(Guid Id, AccountConfigurationTarget Target, string AccountName,
    IReadOnlyList<(string Title, string Old, string New)> Changes, string Reason);

/// <summary>
/// The structured operations the assistant may call. Read-only, except <c>propose_changes</c>, which only
/// prepares a validated proposal: writing it requires the user to press "确认修改" in the window.
/// </summary>
public sealed class AssistantTools(Func<IReadOnlyList<EngineStatusView>> engines,
    Func<CancellationToken, Task<IReadOnlyList<RunRecord>>> history, LocalDatabase? accountDatabase = null,
    IEnumerable<IAccountConfigurationProvider>? configurationProviders = null)
{
    private readonly Dictionary<GameId, IAccountConfigurationProvider> _configuration =
        (configurationProviders ?? []).ToDictionary(provider => provider.Game);
    private readonly object _proposalLock = new();
    private readonly Dictionary<Guid, PendingProposal> _proposals = [];

    internal sealed record PendingProposal(ProposalView View, AccountConfigurationProposal Proposal, DateTimeOffset Expires);
    internal sealed record ToolResult(string Content, PendingProposal? Proposal = null);

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

    public static JsonArray Definitions() => JsonNode.Parse("""
    [
     {"type":"function","function":{"name":"list_games","description":"列出三款游戏、对应引擎、固定版本、安装状态，以及是否能真实执行。","parameters":{"type":"object","properties":{}}}},
     {"type":"function","function":{"name":"list_tasks","description":"列出某款游戏的任务目录（每日/每周/按需）及当前能否真实执行。","parameters":{"type":"object","properties":{"game":{"type":"string","enum":["genshin","starrail","zzz"]}},"required":["game"]}}},
     {"type":"function","function":{"name":"read_configuration","description":"读取指定游戏和内部账号的登记配置、版本和取值规则。","parameters":{"type":"object","properties":{"game":{"type":"string","enum":["genshin","starrail","zzz"]},"account_id":{"type":"string"}},"required":["game","account_id"],"additionalProperties":false}}},
     {"type":"function","function":{"name":"propose_changes","description":"用刚读取的同一游戏、账号和 revision 生成提案。只能使用 read_configuration 登记的参数路径和字符串值，写入需本机确认。","parameters":{"type":"object","properties":{"game":{"type":"string","enum":["genshin","starrail","zzz"]},"account_id":{"type":"string"},"revision":{"type":"string"},"changes":{"type":"object","additionalProperties":{"type":"string"}}},"required":["game","account_id","revision","changes"],"additionalProperties":false}}},
     {"type":"function","function":{"name":"get_run_status","description":"读取真实记录或有标记的模拟结果。按账号查询时同时提供 game 和 account_id，尚未关联的运行不会猜测归属。","parameters":{"type":"object","properties":{"game":{"type":"string","enum":["genshin","starrail","zzz"]},"account_id":{"type":"string"},"limit":{"type":"integer","minimum":1,"maximum":20}},"additionalProperties":false}}}
     ,{"type":"function","function":{"name":"list_accounts","description":"列出指定游戏的内部账号 ID、顺序和核对状态。目标不明确时先询问用户。","parameters":{"type":"object","properties":{"game":{"type":"string","enum":["genshin","starrail","zzz"]}},"required":["game"],"additionalProperties":false}}}
     ,{"type":"function","function":{"name":"get_batch_progress","description":"读取最近多账号批次的进度、状态和失败原因。","parameters":{"type":"object","properties":{}}}}
    ]
    """)!.AsArray();

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
            return new(name switch
            {
                "list_games" => Serialize(engines().Select(e => new
                { game = Key(e.Game), e.Name, e.Engine, e.Version, e.State, can_run_for_real = e.CanRunForReal,
                    account_configuration = _configuration.ContainsKey(e.Game), ai_control = false })),
                "list_tasks" => ListTasks(ParseGame(args)),
                "read_configuration" => await ReadConfigurationAsync(ParseTarget(args), ct).ConfigureAwait(false),
                "get_run_status" => await RunStatusAsync(args, ct).ConfigureAwait(false),
                "list_accounts" => await ListAccountsAsync(ParseGame(args), ct).ConfigureAwait(false),
                "get_batch_progress" => await BatchProgressAsync(ct).ConfigureAwait(false),
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
        "object" => value is JsonObject obj && obj.All(p => ChatCompletionsClient.StringValue(p.Value) is not null),
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

    private string ListTasks(GameId game) => Serialize(EngineCatalog.For(game).Tasks.Select(t => new
    {
        t.Id, t.Title, frequency = t.Frequency.ToString(), can_run_for_real = RealExecutionPolicy.AllowsTask(t.Id) && game == GameId.StarRail,
        t.Note, t.Preconditions,
    }));

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
            account_id = a.Id, game = Key(a.Game), slot = a.Order + 1, a.Enabled, a.AutoRun,
            verification = a.VerificationState is "verified" or "manual_verified" ? "verified" : "unverified",
            last_result = ResultCode(a.LastResult),
        }));
    }

    private async Task<string> BatchProgressAsync(CancellationToken ct)
    {
        if (accountDatabase is null) return Error("账号数据尚未连接。");
        var batches = await accountDatabase.LoadRecentBatchesAsync(5, ct).ConfigureAwait(false);
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
