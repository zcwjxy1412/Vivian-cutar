using System.Globalization;

namespace VIvian.Infrastructure.Engines.OneDragon;

internal static class OneDragonImportedProfile
{
    // The Vivian account ID is an explicit link for recovering an interrupted import.
    internal static async Task<OneDragonProfileBinding> EnsureAsync(string root, Guid account, string gamePath, CancellationToken ct)
    {
        var name = $"Vivian-{account:N}";
        var config = Path.Combine(root, "config");
        var catalogPath = Path.Combine(config, "one_dragon.yml");
        var original = await OneDragonConfigRead.ReadAsync(root, "one_dragon.yml", ct).ConfigureAwait(false);
        var catalog = original is null ? new(OneDragonInstanceStatus.Missing, []) : OneDragonInstanceCatalog.Parse(original);
        if (catalog.Status is not (OneDragonInstanceStatus.Available or OneDragonInstanceStatus.Empty or OneDragonInstanceStatus.Missing)
            || catalog.RunMode == OneDragonInstanceRunMode.CurrentOnly)
            throw new InvalidOperationException("请在 OneDragon 核对实例清单，并将一条龙实例范围设为全部实例。");

        var lines = original is null ? ["instance_list:"] : YamlScalarEditor.Lines(original);
        var parsed = OneDragonConfigRead.ReadSequence(original ?? "", "instance_list");
        if (original is not null && parsed is not { HasSequence: true })
            throw new InvalidOperationException("OneDragon 的实例清单缺少 instance_list。");
        var recovered = parsed?.Rows.Select((row, index) => (Row: row, Index: index))
            .Where(item => item.Row.GetValueOrDefault("name")?.Value == name).ToArray();
        if (recovered is { Length: 1 })
        {
            var binding = new OneDragonProfileBinding(root, catalog.Instances[recovered[0].Index].Index);
            await OneDragonSourceProfile.ReadAsync(binding, ct).ConfigureAwait(false);
            return binding;
        }
        if (recovered is { Length: > 1 }) throw new InvalidOperationException("OneDragon 有重复的导入实例，请先核对源档案。");

        var index = Enumerable.Range(1, 99).FirstOrDefault(candidate => !catalog.Instances.Any(row => row.Index == candidate)
            && !Path.Exists(Path.Combine(config, candidate.ToString("D2", CultureInfo.InvariantCulture))));
        if (index == 0) throw new InvalidOperationException("OneDragon 已无可用实例编号。");

        var sequence = lines.FindIndex(line => OneDragonConfigRead.KeyOf(line) == "instance_list");
        if (catalog.Instances.Count == 0) lines[sequence] = "instance_list:";
        var insert = lines.FindIndex(sequence + 1, line => OneDragonConfigRead.KeyOf(line) is not null);
        if (insert < 0) insert = lines.Count;
        lines.InsertRange(insert,
        [
            $"- idx: {index}",
            $"  name: '{name}'",
            $"  active: {(catalog.Instances.Any(row => row.Active) ? "false" : "true")}",
            "  active_in_od: false",
            "  force_login_before_run: false",
        ]);

        Directory.CreateDirectory(config);
        var folder = Path.Combine(config, index.ToString("D2", CultureInfo.InvariantCulture));
        var staging = Path.Combine(config, ".vivian-import-" + Guid.NewGuid().ToString("N"));
        var stagedProfile = Path.Combine(staging, "profile");
        var stagedCatalog = Path.Combine(staging, "one_dragon.yml");
        var publishedFolder = false;
        var publishedCatalog = false;
        try
        {
            Directory.CreateDirectory(Path.Combine(stagedProfile, "one_dragon"));
            var portablePath = gamePath.Replace('\\', '/');
            await File.WriteAllTextAsync(Path.Combine(stagedProfile, "game_account.yml"),
                $"game_path: \"{portablePath}\"\ngame_region: cn\ngame_language: cn\n", ct).ConfigureAwait(false);
            var tasks = OneDragonTaskPlans.Catalog.Select(task =>
                $"- app_id: {task.Id}\n  enabled: {(task.Id == "engagement_reward" ? "true" : "false")}\n");
            await File.WriteAllTextAsync(Path.Combine(stagedProfile, "one_dragon", "_group.yml"),
                "app_list:\n" + string.Concat(tasks), ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(stagedCatalog, string.Join('\n', lines).TrimEnd('\n') + "\n", ct).ConfigureAwait(false);
            if (ZenlessLoginDriver.EngineBusy() ||
                await OneDragonConfigRead.ReadAsync(root, "one_dragon.yml", ct).ConfigureAwait(false) != original)
                throw new InvalidOperationException("OneDragon 已打开或修改了实例清单，请关闭后重试。");
            Directory.Move(stagedProfile, folder);
            publishedFolder = true;
            if (original is null) File.Move(stagedCatalog, catalogPath);
            else File.Replace(stagedCatalog, catalogPath, null);
            publishedCatalog = true;
            var binding = new OneDragonProfileBinding(root, index);
            await OneDragonSourceProfile.ReadAsync(binding, ct).ConfigureAwait(false);
            return binding;
        }
        finally
        {
            if (publishedFolder && !publishedCatalog && !ZenlessLoginDriver.EngineBusy())
                Directory.Move(folder, stagedProfile);
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }
}
