using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VIvian.Infrastructure.Engines.BetterGi;

internal sealed record BetterGiProfileRevision(string Revision, string CompletionAction);

/// <summary>Temporarily changes only the native profile's completion action, with conflict-aware recovery.</summary>
internal sealed class BetterGiProfileOverride
{
    private readonly string _profileName;
    private readonly string _program;
    private readonly string _profile;
    private readonly string _recovery;
    private readonly string _displaced;
    private readonly IBetterGiProcessProbe _processes;

    public BetterGiProfileOverride(string installRoot, string profileName, IBetterGiProcessProbe processes)
    {
        if (!Path.IsPathFullyQualified(installRoot) || BetterGiArguments.ValidateName(profileName) is not null)
            throw new ArgumentException("BetterGI 安装位置或配置名无效。");
        _profileName = profileName;
        var root = Path.GetFullPath(installRoot);
        _program = Path.Combine(root, BetterGiContract.ProgramName);
        _profile = Path.Combine(root, "User", "OneDragon", profileName + ".json");
        _recovery = _profile + ".vivian-recovery";
        _displaced = _profile + ".vivian-displaced";
        _processes = processes;
    }

    public bool HasPending => File.Exists(_recovery);

    public BetterGiProfileRevision Read()
    {
        return Inspect(ReadBytes(_profile, BetterGiConfiguration.MaxConfigBytes));
    }

    public BetterGiProfileRevision Apply(string revision, string completionAction)
    {
        if (!BetterGiConfiguration.IsSafeCompletionAction(completionAction))
            throw new ArgumentException("BetterGI 完成后动作不受支持。", nameof(completionAction));
        EnsureStopped();
        using var transaction = OpenTransaction();
        if (HasPending) throw new InvalidOperationException("上一次 BetterGI 临时配置恢复待确认。");

        var original = ReadBytes(_profile, BetterGiConfiguration.MaxConfigBytes);
        var snapshot = Inspect(original);
        if (snapshot.Revision != revision)
            throw new ConfigurationConflictException(_profile, "BetterGI 一条龙配置", revision, snapshot.Revision);
        if (snapshot.CompletionAction == completionAction) return snapshot;

        using var document = Parse(original);
        var updated = JsonNode.Parse(document.RootElement.GetRawText(), documentOptions: BetterGiConfiguration.JsonOptions)!.AsObject();
        var field = updated.Select(p => p.Key).SingleOrDefault(k => k.Equals("CompletionAction", StringComparison.OrdinalIgnoreCase))
            ?? "CompletionAction";
        updated[field] = completionAction;
        var replacement = JsonSerializer.SerializeToUtf8Bytes(updated, new JsonSerializerOptions { WriteIndented = true });
        if (replacement.Length > BetterGiConfiguration.MaxConfigBytes)
            throw new InvalidDataException("写入后的 BetterGI 配置超过大小限制。");
        var expected = Inspect(replacement);
        if (expected.CompletionAction != completionAction)
            throw new InvalidDataException("BetterGI 临时值在写入前校验失败。");

        EnsureStopped();
        var journal = JsonSerializer.SerializeToUtf8Bytes(new Recovery(original, snapshot.Revision, expected.Revision, completionAction));
        PublishNew(_recovery, journal);
        ReplaceUnchanged(snapshot.Revision, replacement);
        return expected;
    }

    public RestoreResult Restore()
    {
        try
        {
            EnsureStopped();
            using var transaction = OpenTransaction();
            if (!HasPending) return new(RestoreState.NothingToRestore, "没有待恢复的 BetterGI 配置。");
            var recovery = JsonSerializer.Deserialize<Recovery>(ReadBytes(_recovery, 2 * BetterGiConfiguration.MaxConfigBytes));
            if (recovery?.Original is null || Inspect(recovery.Original).Revision != recovery.OriginalRevision)
                return new(RestoreState.Blocked, "BetterGI 恢复记录无法确认。");

            RestoreDisplaced(recovery);
            var currentBytes = ReadBytes(_profile, BetterGiConfiguration.MaxConfigBytes);
            var currentRevision = Hash(currentBytes);
            if (currentRevision == recovery.OriginalRevision || currentRevision == recovery.MergeTargetRevision)
            {
                File.Delete(_recovery);
                return new(RestoreState.NothingToRestore, "BetterGI 配置已经是原值。");
            }
            if (currentRevision != recovery.TemporaryRevision)
            {
                // BetterGI saves its own defaults/progress at startup. Preserve those updates; we own only this field.
                if (recovery.TemporaryAction is null || Inspect(currentBytes).CompletionAction != recovery.TemporaryAction)
                    return new(RestoreState.Blocked, "BetterGI 结束后动作已有新修改，保留当前配置及恢复记录。");
                using var document = Parse(currentBytes);
                var updated = JsonNode.Parse(document.RootElement.GetRawText())!.AsObject();
                var field = updated.Select(p => p.Key).SingleOrDefault(k => k.Equals("CompletionAction", StringComparison.OrdinalIgnoreCase))
                    ?? "CompletionAction";
                updated[field] = Inspect(recovery.Original).CompletionAction;
                var replacement = JsonSerializer.SerializeToUtf8Bytes(updated, new JsonSerializerOptions { WriteIndented = true });
                recovery = recovery with { MergeSourceRevision = currentRevision, MergeTargetRevision = Hash(replacement) };
                var journalStage = _recovery + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    WriteNew(journalStage, JsonSerializer.SerializeToUtf8Bytes(recovery));
                    File.Move(journalStage, _recovery, overwrite: true);
                }
                finally { File.Delete(journalStage); }
                EnsureStopped();
                ReplaceUnchanged(currentRevision, replacement);
                File.Delete(_recovery);
                return new(RestoreState.Restored, "BetterGI 结束后动作已恢复，其余原生配置更新已保留。");
            }

            EnsureStopped();
            ReplaceUnchanged(recovery.TemporaryRevision, recovery.Original);
            File.Delete(_recovery);
            return new(RestoreState.Restored, "BetterGI 原配置已恢复并核对。");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            return new(RestoreState.Blocked, $"BetterGI 恢复未确认（{ex.GetType().Name}），保留恢复记录。");
        }
    }

    private void EnsureStopped()
    {
        if (_processes.IsBusy(_program))
            throw new InvalidOperationException("BetterGI 有运行中的实例，配置保持冻结。");
    }

    private FileStream OpenTransaction()
    {
        var path = _profile + ".vivian-lock";
        if (ReadOnlyFileAccess.FindReparsePoint(path) is not null)
            throw new InvalidDataException("BetterGI 临时配置锁路径含链接。");
        var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (ReadOnlyFileAccess.HandlePathMatches(file.SafeFileHandle, path)
            && ReadOnlyFileAccess.LinkCount(file.SafeFileHandle) == 1)
            return file;
        file.Dispose();
        throw new InvalidDataException("无法确认 BetterGI 临时配置锁的归属。");
    }

    private static byte[] ReadBytes(string path, int limit)
    {
        using var file = ReadOnlyFileAccess.OpenForStableRead(path);
        if (!ReadOnlyFileAccess.HandlePathMatches(file.SafeFileHandle, path)
            || ReadOnlyFileAccess.LinkCount(file.SafeFileHandle) is > 1 || file.Length > limit)
            throw new InvalidDataException("BetterGI 配置文件路径或大小不符合读取要求。");
        file.Position = 0;
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        return bytes;
    }

    private BetterGiProfileRevision Inspect(byte[] bytes)
    {
        using var document = Parse(bytes);
        if (BetterGiConfiguration.ReadString(document.RootElement, "Name") != _profileName)
            throw new InvalidDataException("BetterGI 配置文件名与内部名称不一致。");
        return new(Hash(bytes), BetterGiConfiguration.ReadString(document.RootElement, "CompletionAction") ?? "");
    }

    private static JsonDocument Parse(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return JsonDocument.Parse(reader.ReadToEnd(), BetterGiConfiguration.JsonOptions);
    }

    private void RestoreDisplaced(Recovery recovery)
    {
        if (!File.Exists(_displaced)) return;
        var revision = Hash(ReadBytes(_displaced, BetterGiConfiguration.MaxConfigBytes));
        if (!IsKnownRevision(revision, recovery))
            throw new InvalidDataException("BetterGI 已保留冲突配置，需核对后恢复。");
        if (!File.Exists(_profile))
        {
            File.Move(_displaced, _profile, overwrite: false);
            return;
        }
        var live = Hash(ReadBytes(_profile, BetterGiConfiguration.MaxConfigBytes));
        if (!IsKnownRevision(live, recovery))
            throw new InvalidDataException("BetterGI 配置已发生变化，已保留现值和备份。");
        File.Delete(_displaced);
    }

    private void ReplaceUnchanged(string revision, byte[] bytes)
    {
        var staged = _profile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteNew(staged, bytes);
            File.Move(_profile, _displaced, overwrite: false);
            var displaced = Hash(ReadBytes(_displaced, BetterGiConfiguration.MaxConfigBytes));
            if (displaced != revision)
            {
                File.Move(_displaced, _profile, overwrite: false);
                throw new ConfigurationConflictException(_profile, "BetterGI 一条龙配置", revision, displaced);
            }

            File.Move(staged, _profile, overwrite: false);
            if (!ReadBytes(_profile, BetterGiConfiguration.MaxConfigBytes).AsSpan().SequenceEqual(bytes))
                throw new IOException("BetterGI 配置回读与写入值不符。");
            File.Delete(_displaced);
        }
        finally
        {
            File.Delete(staged);
        }
    }

    private static void PublishNew(string path, byte[] bytes)
    {
        var staged = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteNew(staged, bytes);
            File.Move(staged, path, overwrite: false);
        }
        finally
        {
            File.Delete(staged);
        }
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool IsKnownRevision(string revision, Recovery recovery) =>
        revision == recovery.OriginalRevision || revision == recovery.TemporaryRevision ||
        revision == recovery.MergeSourceRevision || revision == recovery.MergeTargetRevision;
    private sealed record Recovery(byte[] Original, string OriginalRevision, string TemporaryRevision,
        string? TemporaryAction = null, string? MergeSourceRevision = null, string? MergeTargetRevision = null);
}
