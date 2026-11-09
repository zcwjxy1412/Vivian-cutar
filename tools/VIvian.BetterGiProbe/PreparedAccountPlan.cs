using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using VIvian.Infrastructure.Engines;
using VIvian.Infrastructure.Engines.BetterGi;

internal static class PreparedAccountPlan
{
    private const int MaxPlanBytes = 64 * 1024;
    private const string ManifestName = "account-plan.json";
    private static readonly JsonSerializerOptions PlanJson = new() { AllowDuplicateProperties = false };

    public static int Render(Guid accountId, string settingsFile, string gameExecutable)
    {
        using var file = OpenPlanFile(Path.GetFullPath(settingsFile));
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(file, PlanJson)
            ?? throw new InvalidDataException("原神账号任务配置为空。");
        var plan = BetterGiAccountPlans.Compile(values);
        var files = plan.Render(accountId, gameExecutable);
        Console.WriteLine(JsonSerializer.Serialize(new { files.ProfileName, files.ProfileJson, files.SettingsJson, plan.Values }));
        return 0;
    }

    public static string Verify(string stage, Guid accountId)
    {
        if (accountId == Guid.Empty) throw new InvalidDataException("需要原神账号档案编号。");
        using var manifest = OpenPlanFile(Path.Combine(stage, ManifestName));
        using var json = JsonDocument.Parse(manifest);
        var root = json.RootElement;
        if (!root.TryGetProperty("AccountId", out var id) || !id.TryGetGuid(out var saved) || saved != accountId)
            throw new InvalidDataException("隔离任务档案与指定原神账号不匹配。");

        var name = $"VIvian {accountId:N}";
        var user = Path.Combine(stage, "BetterGI", "User");
        var profile = ReadVerifiedJson(root, "ProfileSha256", Path.Combine(user, "OneDragon", name + ".json"));
        var settings = ReadVerifiedJson(root, "SettingsSha256", Path.Combine(user, "config.json"));
        var values = root.GetProperty("Values").Deserialize<Dictionary<string, string>>(PlanJson)
            ?? throw new InvalidDataException("隔离档案缺少已保存的账号任务设置。");
        var client = settings["genshinStartConfig"]?["installPath"]?.GetValue<string>()
            ?? throw new InvalidDataException("隔离档案缺少游戏客户端路径。");
        var expected = BetterGiAccountPlans.Compile(values).Render(accountId, client);
        if (!JsonNode.DeepEquals(profile, JsonNode.Parse(expected.ProfileJson))
            || !JsonNode.DeepEquals(settings, JsonNode.Parse(expected.SettingsJson)))
            throw new InvalidDataException("隔离设置与当前账号的已校验任务计划不一致。");
        return name;
    }

    private static JsonNode ReadVerifiedJson(JsonElement manifest, string key, string path)
    {
        using var file = OpenPlanFile(path);
        if (!manifest.TryGetProperty(key, out var digest) || digest.ValueKind != JsonValueKind.String
            || !string.Equals(digest.GetString(), Convert.ToHexString(SHA256.HashData(file)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("隔离任务设置已变化，需要重新从已保存的账号生成。");
        file.Position = 0;
        return JsonNode.Parse(file) ?? throw new InvalidDataException("隔离任务设置为空。");
    }

    private static FileStream OpenPlanFile(string path)
    {
        if (ReadOnlyFileAccess.FindReparsePoint(path) is not null)
            throw new InvalidDataException("隔离任务设置路径含链接。");
        var file = ReadOnlyFileAccess.OpenForStableRead(path);
        if (file.Length <= MaxPlanBytes && ReadOnlyFileAccess.HandlePathMatches(file.SafeFileHandle, path)
            && ReadOnlyFileAccess.LinkCount(file.SafeFileHandle) == 1)
            return file;
        file.Dispose();
        throw new InvalidDataException("无法验证隔离任务设置文件的身份或大小。");
    }
}
