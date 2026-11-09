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

public sealed record ProposalView(string Id, IReadOnlyList<(string Title, string Path, string Old, string New)> Changes,
    bool Valid, string Reason);

/// <summary>
/// The structured operations the assistant may call. Read-only, except <c>propose_changes</c>, which only
/// prepares a validated proposal: writing it requires the user to press "确认修改" in the window.
/// </summary>
public sealed class AssistantTools(IConfigurationControl configuration, Func<GameId, string> installPath,
    Func<IReadOnlyList<EngineStatusView>> engines, Func<CancellationToken, Task<IReadOnlyList<RunRecord>>> history,
    LocalDatabase? accountDatabase = null, Func<Guid?>? selectedStarRailAccount = null)
{
    private readonly object _proposalLock = new();
    private readonly Dictionary<string, PendingProposal> _proposals = [];
    public IReadOnlyList<ProposalView> NewProposals { get; private set; } = [];

    internal sealed record PendingProposal(ProposalView View, ConfigurationProposal Proposal, DateTimeOffset Expires);
    internal sealed record ToolResult(string Content, PendingProposal? Proposal = null);

    internal IReadOnlyList<ProposalView> Publish(IEnumerable<PendingProposal> pending)
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
     {"type":"function","function":{"name":"read_configuration","description":"读取某款游戏引擎的真实配置：每项的中文名、路径、当前值、能否修改、取值规则、可选项与依赖。","parameters":{"type":"object","properties":{"game":{"type":"string","enum":["genshin","starrail","zzz"]}},"required":["game"]}}},
     {"type":"function","function":{"name":"propose_changes","description":"为星铁配置生成修改提案并校验（类型、范围、冲突）。不会写入；用户在界面上确认后才写入。值必须取自 read_configuration 给出的规则或可选项。","parameters":{"type":"object","properties":{"changes":{"type":"object","description":"参数路径 → 新值，例如 {\"instance_type\":\"饰品提取\"}","additionalProperties":{"type":"string"}}},"required":["changes"]}}},
     {"type":"function","function":{"name":"get_run_status","description":"读取最近的运行记录：结果、清理状态、原因代码与时间。","parameters":{"type":"object","properties":{"limit":{"type":"integer","minimum":1,"maximum":20}}}}}
     ,{"type":"function","function":{"name":"list_accounts","description":"读取已保存的账号档案、执行顺序、登录核对状态和上次结果；不返回密码。","parameters":{"type":"object","properties":{}}}}
     ,{"type":"function","function":{"name":"read_account_settings","description":"按内部账号 ID 查询其独立的已登记任务设置。","parameters":{"type":"object","properties":{"account_id":{"type":"string"}},"required":["account_id"]}}}
     ,{"type":"function","function":{"name":"get_batch_progress","description":"读取最近多账号批次的进度、状态和失败原因。","parameters":{"type":"object","properties":{}}}}
    ]
    """)!.AsArray();

    public async Task<string> ExecuteAsync(string name, string argumentsJson)
    {
        var result = await InvokeAsync(name, argumentsJson, CancellationToken.None).ConfigureAwait(false);
        NewProposals = result.Proposal is { } proposal ? Publish([proposal]) : [];
        return result.Content;
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
            if (name == "propose_changes") return Propose(args);
            return new(name switch
            {
                "list_games" => Serialize(engines().Select(e => new
                { game = Key(e.Game), e.Name, e.Engine, e.Version, e.State, can_run_for_real = e.CanRunForReal })),
                "list_tasks" => ListTasks(ParseGame(args)),
                "read_configuration" => ReadConfiguration(ParseGame(args)),
                "get_run_status" => await RunStatusAsync(args, ct).ConfigureAwait(false),
                "list_accounts" => await ListAccountsAsync(ct).ConfigureAwait(false),
                "read_account_settings" => await ReadAccountSettingsAsync(args, ct).ConfigureAwait(false),
                "get_batch_progress" => await BatchProgressAsync(ct).ConfigureAwait(false),
                _ => Error("没有这个工具。"),
            });
        }
        catch (JsonException) { return new(Error("工具参数或配置不是有效的 JSON。")); }
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

    /// <summary>Writes a proposal the user confirmed in the window. Never called by the model.</summary>
    public ConfigurationApplyResult ApplyConfirmed(string proposalId, string auditDirectory, bool engineRunning)
    {
        if (selectedStarRailAccount?.Invoke() is { } selectedAccountId
            && selectedAccountId != Guid.Parse("00000000-0000-4000-8000-000000000002"))
            throw new InvalidOperationException("当前选中新增账号；这份旧提案指向共享上游配置，已阻止写入。");
        PendingProposal? pending;
        lock (_proposalLock) _proposals.Remove(proposalId, out pending);
        if (pending is null || pending.Expires <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("这个修改提案已失效，请让助手重新生成。");
        return configuration.ApplyChanges(pending.Proposal, auditDirectory, engineRunning);
    }

    private string ListTasks(GameId game) => Serialize(EngineCatalog.For(game).Tasks.Select(t => new
    {
        t.Id, t.Title, frequency = t.Frequency.ToString(), can_run_for_real = RealExecutionPolicy.AllowsTask(t.Id) && game == GameId.StarRail,
        t.Note, t.Preconditions,
    }));

    private string ReadConfiguration(GameId game)
    {
        if (game == GameId.StarRail && selectedStarRailAccount?.Invoke() is { } id
            && id != Guid.Parse("00000000-0000-4000-8000-000000000002"))
            return Error("当前选中新增账号；请使用 read_account_settings 读取该账号独立设置。");
        var snapshot = configuration.ReadConfiguration(game, installPath(game));
        return Serialize(snapshot.Values.Select(v => new
        {
            v.Schema.Path, v.Schema.Title, v.Schema.Group, value = v.Value, editable = v.Schema.Access == ParameterAccess.VerifiedReadWrite && v.Value is not null,
            rule = ParameterRules.Describe(v.Schema), options = ParameterRules.LabelledOptions(v.Schema).Select(o => o.Value),
            v.Schema.Description, v.Schema.Dependencies, v.State,
        }));
    }

    private ToolResult Propose(JsonObject args)
    {
        if (selectedStarRailAccount?.Invoke() is { } selectedAccountId
            && selectedAccountId != Guid.Parse("00000000-0000-4000-8000-000000000002"))
            return new(Error("当前选中新增账号；此工具不会直接修改共享上游配置，请在游戏管理中修改该账号独立设置。"));
        if (args["changes"] is not JsonObject changes || changes.Count == 0) return new(Error("changes 不能为空。"));
        if (changes.Count > 20) return new(Error("一次最多修改 20 项。"));
        var values = changes.ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>());
        var snapshot = configuration.ReadConfiguration(GameId.StarRail, installPath(GameId.StarRail));
        var proposal = configuration.ProposeChanges(snapshot, values);
        if (proposal.Changes.Count == 0) return new(Serialize(new { valid = false, reason = "这些参数已经是目标值，无需修改。" }));
        var check = configuration.ValidateChanges(proposal, engineRunning: false);
        string Title(string p) => snapshot.Values.First(v => v.Schema.Path == p).Schema.Title;
        var id = Guid.NewGuid().ToString("N");
        var view = new ProposalView(id, proposal.Changes.Select(c => (Title(c.Path), c.Path, c.OldValue, c.NewValue)).ToList(), check.Valid, check.Reason);
        var pending = check.Valid || check.Deferred ? new PendingProposal(view, proposal, DateTimeOffset.UtcNow.AddMinutes(10)) : null;
        return new(Serialize(new
        {
            proposal_id = check.Valid || check.Deferred ? id : null, valid = check.Valid, check.Reason,
            changes = view.Changes.Select(c => new { c.Title, c.Path, from = c.Old, to = c.New }),
            note = "尚未写入。提案将在本轮成功结束后展示给用户确认。",
        }), pending);
    }

    private async Task<string> RunStatusAsync(JsonObject args, CancellationToken ct)
    {
        var limit = Math.Clamp(args["limit"]?.GetValue<int>() ?? 5, 1, 20);
        var runs = await history(ct).ConfigureAwait(false);
        return Serialize(runs.Take(limit).Select(r => new
        {
            game = r.Game.ToString(), outcome = r.Outcome?.ToString() ?? "未结束", cleanup = r.Cleanup.ToString(), reason = r.ReasonCode,
            simulation = r.IsSimulation, updated = r.UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm"),
        }));
    }

    private async Task<string> ListAccountsAsync(CancellationToken ct)
    {
        if (accountDatabase is null) return Error("账号数据尚未连接。");
        var accounts = await accountDatabase.LoadAccountsAsync(ct: ct).ConfigureAwait(false);
        return Serialize(accounts.Select(a => new { account_id = a.Id, game = Key(a.Game), a.Name, a.Region,
            a.Enabled, a.AutoRun, a.Order, a.VerificationState, a.LastResult }));
    }

    private async Task<string> ReadAccountSettingsAsync(JsonObject args, CancellationToken ct)
    {
        if (accountDatabase is null) return Error("账号数据尚未连接。");
        if (!Guid.TryParse(args["account_id"]?.GetValue<string>(), out var id)) return Error("账号 ID 无效。");
        var account = (await accountDatabase.LoadAccountsAsync(ct: ct).ConfigureAwait(false)).SingleOrDefault(a => a.Id == id);
        if (account is null) return Error("账号不存在。");
        var settings = await accountDatabase.LoadAccountSettingsAsync(id, ct).ConfigureAwait(false);
        var plan = await accountDatabase.LoadAccountTaskPlanAsync(id, ct).ConfigureAwait(false);
        return Serialize(new { account_id = id, account.Name, settings.Values, plan.DailyEnabled });
    }

    private async Task<string> BatchProgressAsync(CancellationToken ct)
    {
        if (accountDatabase is null) return Error("账号数据尚未连接。");
        var batches = await accountDatabase.LoadRecentBatchesAsync(5, ct).ConfigureAwait(false);
        return Serialize(batches.Select(b => new { batch_id = b.Id, game = Key(b.Game),
            current_account_id = b.CurrentAccountId, b.State, b.StartedAt, b.UpdatedAt }));
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
