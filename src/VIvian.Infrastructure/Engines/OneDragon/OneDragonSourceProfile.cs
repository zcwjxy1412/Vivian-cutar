using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VIvian.Core.Accounts;

namespace VIvian.Infrastructure.Engines.OneDragon;

public sealed record OneDragonProfileBinding(string Root, int Instance)
{
    internal const string RootKey = "onedragon.root";
    internal const string InstanceKey = "onedragon.instance";

    public static OneDragonProfileBinding? FromSettings(IReadOnlyDictionary<string, string> values) =>
        values.GetValueOrDefault(RootKey) is { } root && Path.IsPathFullyQualified(root)
        && int.TryParse(values.GetValueOrDefault(InstanceKey), NumberStyles.None, CultureInfo.InvariantCulture, out var instance)
        && instance is >= 1 and <= 99 ? new(root, instance) : null;
}

public sealed record OneDragonSourceTask(string Id, string Title, bool Enabled, bool CanEdit);

public sealed record OneDragonSourceProfile(string GamePath, string Revision, IReadOnlyList<OneDragonSourceTask> Tasks)
{
    public static async Task<OneDragonSourceProfile> ReadAsync(OneDragonProfileBinding binding, CancellationToken ct = default)
    {
        var catalog = await OneDragonInstanceCatalog.ReadAsync(binding.Root, ct).ConfigureAwait(false);
        if (!catalog.ContainsExactlyOne([binding.Instance]))
            throw new InvalidOperationException("实例不可单独运行。请在 OneDragon 账号管理核对实例，并将一条龙实例范围设为全部实例。");

        var account = await OneDragonConfigRead.ReadAsync(binding.Root, Relative(binding, "game_account.yml"), ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("请在该 OneDragon 实例中保存绝区零游戏路径。");
        var gamePath = ReadGamePath(account);
        var (text, tasks) = await ReadGroupAsync(binding, ct).ConfigureAwait(false);
        return new(gamePath, Digest(text), tasks);
    }

    private static string ReadGamePath(string account)
    {
        var lines = OneDragonConfigRead.Lines(account);
        CheckRegion(lines);
        var gamePath = YamlScalarEditor.Locate(lines, "game_path")?.Value;
        if (gamePath is null || !Path.IsPathFullyQualified(gamePath)
            || !string.Equals(Path.GetFileName(gamePath), "ZenlessZoneZero.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请在 OneDragon 为该实例保存国服 ZenlessZoneZero.exe 路径。");
        return Path.GetFullPath(gamePath);
    }

    private static void CheckRegion(IReadOnlyList<string> lines)
    {
        if ((YamlScalarEditor.Locate(lines, "game_region")?.Value ?? "cn") != "cn"
            || (YamlScalarEditor.Locate(lines, "game_language")?.Value ?? "cn") != "cn")
            throw new InvalidOperationException("请在 OneDragon 为该实例选择国服和简体中文。");
    }

    internal static string GameAccountDefaults(string gamePath) =>
        $"game_path: \"{gamePath.Replace('\\', '/')}\"\ngame_region: cn\ngame_language: cn\n";

    internal static string TaskDefaults() => "app_list:\n" + string.Concat(OneDragonTaskPlans.Catalog.Select(task =>
        TaskEntry(task.Id, task.Id == "engagement_reward")));

    private static string TaskEntry(string id, bool enabled) =>
        $"- app_id: {id}\n  enabled: {(enabled ? "true" : "false")}\n";

    internal static async Task PrepareMissingAsync(OneDragonProfileBinding binding, string gamePath, CancellationToken ct)
    {
        var catalog = await OneDragonInstanceCatalog.ReadAsync(binding.Root, ct).ConfigureAwait(false);
        if (!catalog.ContainsExactlyOne([binding.Instance]))
            throw new InvalidOperationException("请在 OneDragon 核对该实例，并将一条龙实例范围设为全部实例。");

        var accountFile = Relative(binding, "game_account.yml");
        var account = await OneDragonConfigRead.ReadAsync(binding.Root, accountFile, ct).ConfigureAwait(false);
        var updatedAccount = account ?? GameAccountDefaults(gamePath);
        if (account is not null)
        {
            var lines = YamlScalarEditor.Lines(account);
            CheckRegion(lines);
            var portablePath = gamePath.Replace('\\', '/');
            if (YamlScalarEditor.Locate(lines, "game_path") is { Value.Length: 0, Quote: not null } path)
                updatedAccount = YamlScalarEditor.Replace(account, path, portablePath);
            else if (!lines.Any(line => line.StartsWith("game_path:", StringComparison.Ordinal)))
                updatedAccount = account.TrimEnd('\r', '\n') + $"\ngame_path: \"{portablePath}\"\n";
        }
        _ = ReadGamePath(updatedAccount);
        if (updatedAccount != account)
            await WriteAsync(binding.Root, accountFile, account, updatedAccount, ct).ConfigureAwait(false);

        var groupFile = Group(binding);
        if (await OneDragonConfigRead.ReadAsync(binding.Root, groupFile, ct).ConfigureAwait(false) is null)
            await WriteAsync(binding.Root, groupFile, null, TaskDefaults(), ct).ConfigureAwait(false);
        await ReadAsync(binding, ct).ConfigureAwait(false);
    }

    public static Task SaveTaskAsync(OneDragonProfileBinding binding, string revision, string appId, bool enabled, CancellationToken ct = default) =>
        SaveTasksAsync(binding, revision, new Dictionary<string, bool> { [appId] = enabled }, ct);

    public static async Task SaveTasksAsync(OneDragonProfileBinding binding, string revision,
        IReadOnlyDictionary<string, bool> desired, CancellationToken ct = default)
    {
        var (text, tasks) = await ReadGroupAsync(binding, ct).ConfigureAwait(false);
        if (revision != Digest(text)) throw new InvalidOperationException("OneDragon 已修改该档案，请刷新后重试。");

        var updated = text;
        foreach (var (appId, enabled) in desired)
        {
            if (!OneDragonTaskPlans.Catalog.Any(task => task.Id == appId))
                throw new InvalidOperationException("此任务在 OneDragon 的详细设置中维护。");
            var existing = tasks.SingleOrDefault(task => task.Id == appId);
            if (existing is null)
            {
                if (!enabled) continue;
                var empty = YamlScalarEditor.Lines(updated).FindIndex(line => line.TrimEnd() == "app_list: []");
                if (empty >= 0)
                {
                    var lines = YamlScalarEditor.Lines(updated);
                    lines[empty] = "app_list:";
                    updated = string.Join('\n', lines);
                }
                updated = updated.TrimEnd('\r', '\n') + "\n" + TaskEntry(appId, true);
                continue;
            }
            if (existing.Enabled == enabled) continue;
            var current = YamlScalarEditor.Lines(updated);
            var index = current.FindIndex(line => line.StartsWith("- app_id:", StringComparison.Ordinal)
                && YamlScalarEditor.Locate([line[2..]], "app_id")?.Value == appId);
            if (index < 0 || index + 1 >= current.Count || !current[index + 1].StartsWith("  enabled:", StringComparison.Ordinal)
                || YamlScalarEditor.Locate([current[index + 1][2..]], "enabled") is not { } value)
                throw new InvalidOperationException("此档案的字段布局请在 OneDragon 中修改。");
            updated = YamlScalarEditor.Replace(updated, value with { Line = index + 1, ValueStart = value.ValueStart + 2 },
                enabled ? "true" : "false");
        }
        if (updated != text)
            await WriteAsync(binding.Root, Group(binding), text, updated, ct).ConfigureAwait(false);
    }

    private static async Task WriteAsync(string root, string relative, string? before, string updated, CancellationToken ct)
    {
        var path = Path.Combine(root, "config", relative);
        if (ReadOnlyFileAccess.FindReparsePoint(path) is not null)
            throw new InvalidOperationException("OneDragon 源档案路径包含链接。");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, updated, new UTF8Encoding(false), ct).ConfigureAwait(false);
            var current = await OneDragonConfigRead.ReadAsync(root, relative, ct).ConfigureAwait(false);
            if (current != before || ZenlessLoginDriver.EngineBusy())
                throw new InvalidOperationException("OneDragon 已打开或修改了档案，请关闭后刷新。");
            if (before is null) File.Move(temp, path);
            else File.Replace(temp, path, null);
            if (await OneDragonConfigRead.ReadAsync(root, relative, CancellationToken.None).ConfigureAwait(false) != updated)
                throw new InvalidOperationException("源档案写入后的回读不一致，请在 OneDragon 核对。");
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static async Task<(string Text, IReadOnlyList<OneDragonSourceTask> Tasks)> ReadGroupAsync(OneDragonProfileBinding binding, CancellationToken ct)
    {
        var text = await OneDragonConfigRead.ReadAsync(binding.Root, Group(binding), ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("请先在 OneDragon 的一条龙页面为此实例保存应用组。");
        var parsed = OneDragonConfigRead.ReadSequence(text, "app_list");
        if (parsed is not { HasSequence: true } || parsed.Scalars.Count != 0)
            throw new InvalidOperationException("OneDragon 应用组格式无法识别。");
        var tasks = new List<OneDragonSourceTask>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in parsed.Rows)
        {
            if (row.Count != 2 || !row.TryGetValue("app_id", out var id) || id.Value.Length is < 1 or > 64
                || !id.Value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_') || !ids.Add(id.Value)
                || OneDragonConfigRead.ReadBool(row.GetValueOrDefault("enabled")) is not { } enabled)
                throw new InvalidOperationException("OneDragon 应用组包含重复或无效的任务项。");
            var known = OneDragonTaskPlans.Catalog.FirstOrDefault(task => task.Id == id.Value);
            tasks.Add(new(id.Value, known?.DisplayName ?? id.Value, enabled, known is not null));
        }
        return (text, tasks);
    }

    private static string Group(OneDragonProfileBinding binding) => Relative(binding, Path.Combine("one_dragon", "_group.yml"));
    private static string Relative(OneDragonProfileBinding binding, string file) =>
        Path.Combine(binding.Instance.ToString("D2", CultureInfo.InvariantCulture), file);
    private static string Digest(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
