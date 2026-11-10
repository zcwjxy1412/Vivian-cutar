using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VIvian.Infrastructure.Engines.BetterGi;

/// <summary>Applies only the current account's configured counts to BetterGI's shared native settings.</summary>
internal sealed class BetterGiDomainOverride(string root, IBetterGiProcessProbe probe)
{
    private string Config => Path.Combine(root, "User", "config.json");
    private string Journal => Config + ".vivian-domain-recovery";
    internal bool HasPending => File.Exists(Journal);
    private sealed record Change(bool Existed, JsonNode? Original, JsonNode? Applied);

    internal void Apply(IReadOnlyDictionary<string, string> values)
    {
        EnsureStopped();
        if (HasPending) throw new InvalidOperationException("上一账号的 BetterGI 资源参数尚未恢复。");
        var fields = new[] { BetterGiAccountPlans.Original20, BetterGiAccountPlans.Original40, BetterGiAccountPlans.Condensed };
        if (!fields.Any(values.ContainsKey)) return;
        var changes = new Dictionary<string, Change>();
        var bytes = Read(Config);
        var config = Parse(bytes);
        var domain = config["autoDomainConfig"]?.AsObject() ?? new JsonObject();
        foreach (var key in fields)
        {
            var raw = values.TryGetValue(key, out var configured) ? configured : "0";
            if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count is < 0 or > 99)
                throw new InvalidDataException("账号树脂次数必须在 0 至 99 之间。");
            Set(key.Split('.')[1], JsonValue.Create(count));
        }
        if (changes.Count == 0) return;
        Set("specifyResinUse", JsonValue.Create(true));
        if (config["autoDomainConfig"] is null) config["autoDomainConfig"] = domain;
        Publish(Journal, JsonSerializer.SerializeToUtf8Bytes(changes));
        Replace(bytes, JsonSerializer.SerializeToUtf8Bytes(config));

        void Set(string key, JsonNode value)
        {
            var existed = domain.TryGetPropertyValue(key, out var original);
            changes[key] = new(existed, original?.DeepClone(), value.DeepClone());
            domain[key] = value;
        }
    }

    internal RestoreResult Restore()
    {
        try
        {
            EnsureStopped();
            if (!HasPending) return new(RestoreState.NothingToRestore, "没有待恢复的账号资源参数。");
            var changes = JsonSerializer.Deserialize<Dictionary<string, Change>>(Read(Journal))
                ?? throw new InvalidDataException("资源参数恢复记录为空。");
            var bytes = Read(Config);
            var config = Parse(bytes);
            var domain = config["autoDomainConfig"]?.AsObject() ?? new JsonObject();
            foreach (var (key, change) in changes)
            {
                if (key is not ("originalResin20UseCount" or "originalResin40UseCount" or "condensedResinUseCount" or "specifyResinUse"))
                    throw new InvalidDataException("资源参数恢复字段无效。");
                var exists = domain.TryGetPropertyValue(key, out var current);
                if (exists == change.Existed && JsonNode.DeepEquals(current, change.Original)) continue;
                if (!exists || !JsonNode.DeepEquals(current, change.Applied))
                    return new(RestoreState.Blocked, "BetterGI 资源参数已有其他修改，保留当前配置和恢复记录。");
                if (change.Existed) domain[key] = change.Original?.DeepClone();
                else domain.Remove(key);
            }
            if (config["autoDomainConfig"] is null && domain.Count > 0) config["autoDomainConfig"] = domain;
            Replace(bytes, JsonSerializer.SerializeToUtf8Bytes(config));
            File.Delete(Journal);
            return new(RestoreState.Restored, "本账号资源参数已恢复，其他原生设置更新已保留。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            return new(RestoreState.Blocked, $"账号资源参数恢复未确认（{ex.GetType().Name}）。");
        }
    }

    private void EnsureStopped()
    {
        if (probe.IsBusy(Path.Combine(root, BetterGiContract.ProgramName)))
            throw new InvalidOperationException("BetterGI 正在运行，资源参数保持冻结。");
    }

    private static JsonObject Parse(byte[] bytes) => JsonNode.Parse(System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'),
        documentOptions: BetterGiConfiguration.JsonOptions)!.AsObject();

    private static byte[] Read(string path)
    {
        using var file = ReadOnlyFileAccess.OpenForStableRead(path);
        if (ReadOnlyFileAccess.FindReparsePoint(path) is not null || file.Length > BetterGiConfiguration.MaxConfigBytes ||
            !ReadOnlyFileAccess.HandlePathMatches(file.SafeFileHandle, path) || ReadOnlyFileAccess.LinkCount(file.SafeFileHandle) != 1)
            throw new InvalidDataException("资源配置路径或大小不符合要求。");
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        return bytes;
    }

    private void Replace(byte[] expected, byte[] replacement)
    {
        EnsureStopped();
        if (ReadOnlyFileAccess.FindReparsePoint(Config) is not null)
            throw new InvalidDataException("资源配置路径含链接。");
        using var current = new FileStream(Config, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (!ReadOnlyFileAccess.HandlePathMatches(current.SafeFileHandle, Config) || ReadOnlyFileAccess.LinkCount(current.SafeFileHandle) != 1)
            throw new InvalidDataException("无法确认资源配置文件归属。");
        var original = new byte[checked((int)current.Length)];
        current.ReadExactly(original);
        if (!SHA256.HashData(original).AsSpan().SequenceEqual(SHA256.HashData(expected)))
            throw new InvalidOperationException("BetterGI 配置已被修改，停止参数同步。");
        var backup = Config + ".vivian-domain-write-" + Guid.NewGuid().ToString("N") + ".backup";
        Publish(backup, original);
        try { WriteAndVerify(replacement); }
        catch
        {
            // Keep the durable original if even same-handle rollback fails.
            WriteAndVerify(original);
            File.Delete(backup);
            throw;
        }
        File.Delete(backup);

        void WriteAndVerify(byte[] bytes)
        {
            current.Position = 0;
            current.Write(bytes);
            current.SetLength(bytes.Length);
            current.Flush(flushToDisk: true);
            current.Position = 0;
            var written = new byte[bytes.Length];
            current.ReadExactly(written);
            if (!written.AsSpan().SequenceEqual(bytes)) throw new IOException("资源配置写后核对失败。");
        }
    }

    private static void Publish(string path, byte[] bytes, bool replace = false)
    {
        var staged = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            File.Move(staged, path, overwrite: replace);
        }
        finally { File.Delete(staged); }
    }
}
