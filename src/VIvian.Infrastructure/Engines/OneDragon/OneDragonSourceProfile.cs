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
        var lines = OneDragonConfigRead.Lines(account);
        var region = YamlScalarEditor.Locate(lines, "game_region")?.Value ?? "cn";
        var language = YamlScalarEditor.Locate(lines, "game_language")?.Value ?? "cn";
        var gamePath = YamlScalarEditor.Locate(lines, "game_path")?.Value;
        if (region != "cn" || language != "cn" || gamePath is null || !Path.IsPathFullyQualified(gamePath)
            || !string.Equals(Path.GetFileName(gamePath), "ZenlessZoneZero.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请在 OneDragon 为该实例设置国服简体中文客户端及 ZenlessZoneZero.exe 路径。");
        var (text, tasks) = await ReadGroupAsync(binding, ct).ConfigureAwait(false);
        return new(Path.GetFullPath(gamePath), Digest(text), tasks);
    }

    public static async Task SaveTaskAsync(OneDragonProfileBinding binding, string revision, string appId, bool enabled, CancellationToken ct = default)
    {
        var (text, tasks) = await ReadGroupAsync(binding, ct).ConfigureAwait(false);
        if (revision != Digest(text)) throw new InvalidOperationException("OneDragon 已修改该档案，请刷新后重试。");
        if (tasks.SingleOrDefault(task => task.Id == appId) is not { CanEdit: true } existing)
            throw new InvalidOperationException("此任务在 OneDragon 的详细设置中维护。");
        if (existing.Enabled == enabled) return;
        var lines = YamlScalarEditor.Lines(text);
        var index = lines.FindIndex(line => line.StartsWith("- app_id:", StringComparison.Ordinal)
            && YamlScalarEditor.Locate([line[2..]], "app_id")?.Value == appId);
        if (index < 0 || index + 1 >= lines.Count || !lines[index + 1].StartsWith("  enabled:", StringComparison.Ordinal)
            || YamlScalarEditor.Locate([lines[index + 1][2..]], "enabled") is not { } value)
            throw new InvalidOperationException("此档案的字段布局请在 OneDragon 中修改。");
        var updated = YamlScalarEditor.Replace(text, value with { Line = index + 1, ValueStart = value.ValueStart + 2 },
            enabled ? "true" : "false");
        var path = Path.Combine(binding.Root, "config", Group(binding));
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, updated, new UTF8Encoding(false), ct).ConfigureAwait(false);
            var (current, _) = await ReadGroupAsync(binding, ct).ConfigureAwait(false);
            if (current != text || new OneDragonProcessProbe().CountRunning(Path.Combine(binding.Root, OneDragonContract.EntryProgram)) > 0)
                throw new InvalidOperationException("OneDragon 已打开或修改了档案，请关闭后刷新。");
            File.Replace(temp, path, null);
            var (readBack, _) = await ReadGroupAsync(binding, CancellationToken.None).ConfigureAwait(false);
            if (readBack != updated) throw new InvalidOperationException("源档案写入后的回读不一致，请在 OneDragon 核对。");
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
