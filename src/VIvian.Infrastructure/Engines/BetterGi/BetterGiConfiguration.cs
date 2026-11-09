using System.Text;
using System.Text.Json;

namespace VIvian.Infrastructure.Engines.BetterGi;

/// <summary>Resolves 0.66.0 requests against the same on-disk names BetterGI will use.</summary>
internal static class BetterGiConfiguration
{
    private const int MaxConfigBytes = 1024 * 1024;
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static Task<BetterGiTaskRequest> ResolveAsync(string installRoot, BetterGiTaskRequest request, CancellationToken ct) =>
        ReadOnlyFileAccess.BoundedAsync(() => Resolve(installRoot, request, ct), ReadOnlyFileAccess.QueryTimeout, ct);

    private static BetterGiTaskRequest Resolve(string installRoot, BetterGiTaskRequest request, CancellationToken ct)
    {
        var user = Path.Combine(installRoot, "User");
        if (request.Kind == BetterGiTaskKind.StartGroups)
        {
            foreach (var group in request.Groups)
            {
                ct.ThrowIfCancellationRequested();
                var path = Path.Combine(user, "ScriptGroup", group + ".json");
                using var json = ReadJson(path);
                if (!string.Equals(ReadString(json.RootElement, "name"), group, StringComparison.Ordinal))
                    throw new InvalidDataException("BetterGI 配置组文件名与内部名称不一致。");
            }
            return request;
        }

        var requestedName = request.ConfigName;
        if (request.Kind == BetterGiTaskKind.OneDragonSelected)
        {
            using var settings = ReadJson(Path.Combine(user, "config.json"));
            requestedName = ReadString(settings.RootElement, "selectedOneDragonFlowConfigName");
        }
        if (BetterGiArguments.ValidateName(requestedName, "一条龙配置名") is { } invalid)
            throw new InvalidDataException(invalid);

        var directory = Path.Combine(user, "OneDragon");
        if (ReadOnlyFileAccess.FindReparsePoint(directory) is not null)
            throw new InvalidDataException("BetterGI 一条龙配置目录含链接。");

        var count = 0;
        var matches = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            if (++count > 256)
                throw new InvalidDataException("BetterGI 一条龙配置超过 256 份，无法确认目标唯一。");
            using var json = ReadJson(path);
            if (!string.Equals(ReadString(json.RootElement, "Name"), requestedName, StringComparison.Ordinal))
                continue;
            if (++matches > 1)
                throw new InvalidDataException("BetterGI 存在同名的一条龙配置，无法确认目标唯一。");
            if (ReadString(json.RootElement, "CompletionAction") is { } action
                && action is not ("" or "关闭游戏" or "关闭软件" or "关闭游戏和软件"))
                throw new InvalidDataException("BetterGI 一条龙配置包含关机或无法识别的完成后动作。");
        }
        if (matches == 0)
            throw new InvalidDataException("BetterGI 找不到指定的一条龙配置，已阻止回退到默认配置。");

        return BetterGiArguments.OneDragon(requestedName).Request!;
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

    private static string? ReadString(JsonElement json, string key)
    {
        string? result = null;
        var found = false;
        foreach (var property in json.EnumerateObject())
        {
            if (!string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
                continue;
            if (found)
                throw new InvalidDataException("BetterGI 配置包含含义重复的字段。");
            result = property.Value.GetString();
            found = true;
        }
        return result;
    }
}
