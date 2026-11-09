using System.Text;
using System.Text.Json;

namespace VIvian.Infrastructure.Engines.BetterGi;

/// <summary>The effective built-in and group task order plus the one-dragon fields VIvian can inspect.</summary>
public sealed record BetterGiOneDragonPlan(string Name, IReadOnlyList<string> EnabledTasks, string CompletionAction,
    string CraftingBenchCountry, int MinResinToKeep, string DomainName, string PartyName, bool WeeklyDomainEnabled,
    string AdventurersGuildCountry, string DailyRewardPartyName);

public sealed record BetterGiDomainResinPlan(bool SpecifyResinUse, IReadOnlyList<string> Priority,
    int Original, int Original20, int Original40, int Condensed, int Transient, int Fragile, bool AutoArtifactSalvage)
{
    internal bool HasExplicitBudget
    {
        get
        {
            int[] counts = [Original, Original20, Original40, Condensed, Transient, Fragile];
            return SpecifyResinUse && counts.All(count => count >= 0) && counts.Any(count => count > 0);
        }
    }
}

/// <summary>Resolves 0.66.0 requests against the same on-disk names BetterGI will use.</summary>
public static class BetterGiConfiguration
{
    internal const int MaxConfigBytes = 1024 * 1024;
    internal static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static async Task<BetterGiOneDragonPlan> InspectOneDragonAsync(string installRoot, string? name = null,
        CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(installRoot))
            throw new ArgumentException("BetterGI 安装目录必须是绝对路径。", nameof(installRoot));
        var request = BetterGiArguments.OneDragon(name);
        if (request.Request is null) throw new InvalidDataException(request.Error);
        var resolved = await ResolveAsync(Path.GetFullPath(installRoot), request.Request, ct).ConfigureAwait(false);
        return resolved.OneDragonPlan!;
    }

    public static Task<BetterGiDomainResinPlan> InspectDomainResinAsync(string installRoot, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(installRoot))
            throw new ArgumentException("BetterGI 安装目录必须是绝对路径。", nameof(installRoot));
        return ReadOnlyFileAccess.BoundedAsync(() =>
        {
            using var document = ReadJson(Path.Combine(Path.GetFullPath(installRoot), "User", "config.json"));
            var domain = ReadProperty(document.RootElement, "autoDomainConfig", StringComparison.Ordinal);
            var priority = Domain("resinPriorityList")?.Deserialize<string[]>() ?? ["浓缩树脂", "原粹树脂"];
            return new BetterGiDomainResinPlan(Domain("specifyResinUse")?.GetBoolean() ?? false, Array.AsReadOnly(priority),
                Count("originalResinUseCount"), Count("originalResin20UseCount"), Count("originalResin40UseCount"), Count("condensedResinUseCount"),
                Count("transientResinUseCount"), Count("fragileResinUseCount"),
                Domain("autoArtifactSalvage")?.GetBoolean() ?? false);

            JsonElement? Domain(string key) => domain is { } value ? ReadProperty(value, key, StringComparison.Ordinal) : null;
            int Count(string key) => Domain(key)?.GetInt32() ?? 0;
        }, ReadOnlyFileAccess.QueryTimeout, ct);
    }

    /// <summary>Finds the installed CN game executable already configured in BetterGI.</summary>
    public static Task<string?> FindClientExecutableAsync(string installRoot, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(installRoot))
            throw new ArgumentException("BetterGI 安装目录必须是绝对路径。", nameof(installRoot));
        return ReadOnlyFileAccess.BoundedAsync<string?>(() =>
        {
            using var document = ReadJson(Path.Combine(Path.GetFullPath(installRoot), "User", "config.json"));
            var start = ReadProperty(document.RootElement, "genshinStartConfig", StringComparison.Ordinal);
            var configured = start is { ValueKind: JsonValueKind.Object } config ? ReadString(config, "installPath") : null;
            if (configured is null || !Path.IsPathFullyQualified(configured) ||
                !string.Equals(Path.GetFileName(configured), "YuanShen.exe", StringComparison.OrdinalIgnoreCase))
                return null;
            var path = Path.GetFullPath(configured);
            return File.Exists(path) && ReadOnlyFileAccess.FindReparsePoint(path) is null ? path : null;
        }, ReadOnlyFileAccess.QueryTimeout, ct);
    }

    internal static Task<BetterGiTaskRequest> ResolveAsync(string installRoot, BetterGiTaskRequest request, CancellationToken ct) =>
        ReadOnlyFileAccess.BoundedAsync(() => Resolve(installRoot, request, ct), ReadOnlyFileAccess.QueryTimeout, ct);

    private static BetterGiTaskRequest Resolve(string installRoot, BetterGiTaskRequest request, CancellationToken ct)
    {
        var user = Path.Combine(installRoot, "User");
        if (request.Kind == BetterGiTaskKind.StartGroups)
        {
            foreach (var group in request.Groups)
            {
                ct.ThrowIfCancellationRequested();
                CheckGroup(user, group);
            }
            return request;
        }

        var requestedName = request.ConfigName;
        if (request.Kind == BetterGiTaskKind.OneDragonSelected)
        {
            using var settings = ReadJson(Path.Combine(user, "config.json"));
            requestedName = ReadProperty(settings.RootElement, "selectedOneDragonFlowConfigName", StringComparison.Ordinal)?.GetString();
        }
        if (BetterGiArguments.ValidateName(requestedName, "一条龙配置名") is { } invalid)
            throw new InvalidDataException(invalid);

        var directory = Path.Combine(user, "OneDragon");
        if (ReadOnlyFileAccess.FindReparsePoint(directory) is not null)
            throw new InvalidDataException("BetterGI 一条龙配置目录含链接。");

        var count = 0;
        BetterGiOneDragonPlan? plan = null;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            if (++count > 256)
                throw new InvalidDataException("BetterGI 一条龙配置超过 256 份，无法确认目标唯一。");
            using var json = ReadJson(path);
            if (!string.Equals(ReadString(json.RootElement, "Name"), requestedName, StringComparison.Ordinal))
                continue;
            if (plan is not null)
                throw new InvalidDataException("BetterGI 存在同名的一条龙配置，无法确认目标唯一。");
            plan = ReadOneDragonPlan(requestedName!, json.RootElement);
        }
        if (plan is null)
            throw new InvalidDataException("BetterGI 找不到指定的一条龙配置，已阻止回退到默认配置。");

        foreach (var task in plan.EnabledTasks.Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (!BuiltInTasks.Contains(task)) CheckGroup(user, task);
        }
        return BetterGiArguments.OneDragon(requestedName).Request! with { OneDragonPlan = plan };
    }

    private static readonly HashSet<string> BuiltInTasks = new(StringComparer.Ordinal)
    {
        "领取邮件", "合成树脂", "自动秘境", "自动首领讨伐", "自动幽境危战", "自动地脉花", "领取每日奖励", "领取尘歌壶奖励",
    };

    private static void CheckGroup(string user, string group)
    {
        if (BetterGiArguments.ValidateName(group, "配置组名") is { } invalid)
            throw new InvalidDataException(invalid);
        using var json = ReadJson(Path.Combine(user, "ScriptGroup", group + ".json"));
        if (!string.Equals(ReadString(json.RootElement, "name"), group, StringComparison.Ordinal))
            throw new InvalidDataException("BetterGI 配置组文件名与内部名称不一致。");
    }

    private static BetterGiOneDragonPlan ReadOneDragonPlan(string name, JsonElement json)
    {
        var action = ReadString(json, "CompletionAction") ?? "";
        if (!IsSafeCompletionAction(action))
            throw new InvalidDataException("BetterGI 一条龙配置包含关机或无法识别的完成后动作。");

        return new BetterGiOneDragonPlan(name, ReadEnabledTasks(json), action,
            ReadString(json, "CraftingBenchCountry") ?? "枫丹",
            ReadProperty(json, "MinResinToKeep")?.GetInt32() ?? 0,
            ReadString(json, "DomainName") ?? "",
            ReadString(json, "PartyName") ?? "",
            ReadProperty(json, "WeeklyDomainEnabled")?.GetBoolean() ?? false,
            ReadString(json, "AdventurersGuildCountry") ?? "枫丹",
            ReadString(json, "DailyRewardPartyName") ?? "");
    }

    private static IReadOnlyList<string> ReadEnabledTasks(JsonElement json)
    {
        var enabled = ReadProperty(json, "TaskEnabledList")?.Deserialize<Dictionary<string, bool>>() ?? [];
        var definitions = ReadProperty(json, "TaskDefinitions")?.Deserialize<Dictionary<string, string>>() ?? [];
        var configuredOrder = ReadProperty(json, "TaskOrder")?.Deserialize<List<string>>();
        var order = configuredOrder is { Count: > 0 } ? configuredOrder : enabled.Keys.ToList();
        var visible = order.Where(id => enabled.ContainsKey(id) && (definitions.Count == 0 || definitions.ContainsKey(id))).ToList();
        var next = ReadString(json, "NextTaskId");
        var start = visible.FindIndex(id => id == next);
        return visible.Skip(Math.Max(0, start)).Where(id => enabled[id])
            .Select(id => definitions.Count == 0 ? id : definitions[id]).ToList().AsReadOnly();
    }

    private static JsonDocument ReadJson(string path)
    {
        if (ReadOnlyFileAccess.FindReparsePoint(path) is not null)
            throw new InvalidDataException("BetterGI 配置文件路径含链接。");
        using var stream = ReadOnlyFileAccess.OpenForStableRead(path);
        if (!ReadOnlyFileAccess.HandlePathMatches(stream.SafeFileHandle, path) || stream.Length > MaxConfigBytes)
            throw new InvalidDataException("BetterGI 配置文件路径或大小不符合读取要求。");

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return JsonDocument.Parse(reader.ReadToEnd(), JsonOptions);
    }

    internal static bool IsSafeCompletionAction(string action) =>
        action is "" or "无" or "关闭游戏" or "关闭软件" or "关闭游戏和软件";

    internal static string? ReadString(JsonElement json, string key) => ReadProperty(json, key)?.GetString();

    private static JsonElement? ReadProperty(JsonElement json, string key, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        JsonElement? result = null;
        foreach (var property in json.EnumerateObject())
        {
            if (!string.Equals(property.Name, key, comparison))
                continue;
            if (result.HasValue)
                throw new InvalidDataException("BetterGI 配置包含含义重复的字段。");
            result = property.Value;
        }
        return result;
    }
}
