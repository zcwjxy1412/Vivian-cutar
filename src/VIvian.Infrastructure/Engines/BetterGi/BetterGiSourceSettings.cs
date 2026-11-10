using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using VIvian.Core.Engines;
using VIvian.Infrastructure.Processes;

namespace VIvian.Infrastructure.Engines.BetterGi;

public sealed record BetterGiSourceSettingsSnapshot(
    string ProfileRevision, string SharedDomainRevision,
    IReadOnlyDictionary<string, string> ProfileValues, IReadOnlyDictionary<string, string> SharedDomainValues,
    bool WeeklyDomainEnabled, bool SharedSpecifyResinUse, bool SharedAutoArtifactSalvage);

public sealed class BetterGiSourceSettingsConflictException(string path, string? preservedPath = null)
    : IOException(preservedPath is null
        ? "BetterGI 原设置已变化。请重新读取后再保存。"
        : $"BetterGI 设置保存期间发生冲突。替换前的原文件保存在 {preservedPath}。")
{
    public string Path { get; } = path;
    public string? PreservedPath { get; } = preservedPath;
}

/// <summary>Edits registered fields in an explicitly selected existing BetterGI 0.66.0 source profile.</summary>
public sealed class BetterGiSourceSettings
{
    private const int MaxProfiles = 256;
    private const string CurrentParty = "当前队伍";
    private const string NoDomain = "未设置";
    private const string DomainGroup = "autoDomainConfig";
    private static readonly (string Key, string Name)[] Tasks =
    [
        (BetterGiAccountPlans.Daily, "领取每日奖励"),
        (BetterGiAccountPlans.Craft, "合成树脂"),
        (BetterGiAccountPlans.Domain, "自动秘境"),
    ];
    private static readonly string[] ProfileFields =
    [
        "AdventurersGuildCountry", "DailyRewardPartyName", "CraftingBenchCountry",
        "MinResinToKeep", "DomainName", "PartyName",
    ];
    private static readonly string[] SharedFields = [BetterGiAccountPlans.Original20, BetterGiAccountPlans.Original40, BetterGiAccountPlans.Condensed];
    private static readonly Dictionary<string, EngineParameterDefinition> Parameters =
        BetterGiAccountPlans.Parameters.ToDictionary(parameter => parameter.Path, StringComparer.Ordinal);
    private static readonly JsonDocumentOptions ReadJson = new()
    {
        AllowDuplicateProperties = false,
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };
    private static readonly JsonSerializerOptions WriteJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IBetterGiProcessProbe _processes;
    private readonly string _lockPath;
    private readonly Func<string, bool> _supportsVersion;

    private BetterGiSourceSettings(string installRoot, string profileName, IBetterGiProcessProbe processes,
        string lockPath, Func<string, bool> supportsVersion)
    {
        InstallRoot = installRoot;
        ProfileName = profileName;
        ProgramPath = Path.Combine(installRoot, BetterGiContract.ProgramName);
        ProfilePath = Path.Combine(installRoot, "User", "OneDragon", profileName + ".json");
        SharedConfigPath = Path.Combine(installRoot, "User", "config.json");
        _processes = processes;
        _lockPath = lockPath;
        _supportsVersion = supportsVersion;
    }

    public string InstallRoot { get; }
    public string ProfileName { get; }
    public string ProfilePath { get; }
    public string SharedConfigPath { get; }
    public string ProgramPath { get; }

    public static Task<BetterGiSourceSettings> BindAsync(string installRoot, string profileName, CancellationToken ct = default) =>
        BindAsync(installRoot, profileName, new BetterGiProcessProbe(), DesktopOperationLease.LockPath, IsPinnedVersion, ct);

    public static Task<BetterGiSourceSettings?> FindAccountProfileAsync(string installRoot, Guid accountId,
        CancellationToken ct = default) =>
        FindAccountProfileAsync(installRoot, accountId, new BetterGiProcessProbe(), DesktopOperationLease.LockPath, IsPinnedVersion, ct);

    public static Task<BetterGiSourceSettings> ConnectAccountProfileAsync(string installRoot, Guid accountId,
        IReadOnlyDictionary<string, string> savedSettings, CancellationToken ct = default) =>
        ConnectAccountProfileAsync(installRoot, accountId, BetterGiAccountPlans.Compile(savedSettings),
            new BetterGiProcessProbe(), DesktopOperationLease.LockPath, IsPinnedVersion, ct);

    internal static Task<BetterGiSourceSettings> BindAsync(string installRoot, string profileName,
        IBetterGiProcessProbe processes, string lockPath, Func<string, bool> supportsVersion, CancellationToken ct = default)
    {
        var binding = CreateBinding(installRoot, profileName, processes, lockPath, supportsVersion);
        return ReadOnlyFileAccess.BoundedAsync(() =>
        {
            binding.EnsureVersion();
            binding.ResolveProfile(ct);
            return binding;
        }, ReadOnlyFileAccess.QueryTimeout, ct);
    }

    internal static Task<BetterGiSourceSettings?> FindAccountProfileAsync(string installRoot, Guid accountId,
        IBetterGiProcessProbe processes, string lockPath, Func<string, bool> supportsVersion, CancellationToken ct = default)
    {
        var binding = CreateBinding(installRoot, BetterGiAccountPlan.SourceProfileName(accountId), processes, lockPath, supportsVersion);
        return ReadOnlyFileAccess.BoundedAsync(() =>
        {
            binding.EnsureVersion();
            return binding.FindProfile(ct) is null ? null : binding;
        }, ReadOnlyFileAccess.QueryTimeout, ct);
    }

    internal static Task<BetterGiSourceSettings> ConnectAccountProfileAsync(string installRoot, Guid accountId,
        BetterGiAccountPlan savedPlan, IBetterGiProcessProbe processes, string lockPath, Func<string, bool> supportsVersion,
        CancellationToken ct = default)
    {
        var binding = CreateBinding(installRoot, BetterGiAccountPlan.SourceProfileName(accountId), processes, lockPath, supportsVersion);
        return Task.Run(() =>
        {
            binding.CreateAccountProfile(accountId, savedPlan, ct);
            return binding;
        }, ct);
    }

    private static BetterGiSourceSettings CreateBinding(string installRoot, string profileName,
        IBetterGiProcessProbe processes, string lockPath, Func<string, bool> supportsVersion)
    {
        if (!Path.IsPathFullyQualified(installRoot) || BetterGiArguments.ValidateName(profileName) is not null)
            throw new ArgumentException("需要 BetterGI 安装绝对路径及明确的一条龙配置名称。");
        return new(Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot)), profileName, processes, lockPath, supportsVersion);
    }

    private void CreateAccountProfile(Guid accountId, BetterGiAccountPlan savedPlan, CancellationToken ct)
    {
        if (new DriveInfo(Path.GetPathRoot(InstallRoot)!).DriveType != DriveType.Fixed)
            throw new InvalidOperationException("BetterGI 档案只支持本地固定磁盘。");
        using var lease = DesktopOperationLease.AcquireAt(_lockPath, InstallRoot, requireExisting: true);
        EnsureVersion();
        var shared = ReadSource(SharedConfigPath);
        if (FindProfile(ct) is { } existing)
        {
            _ = Snapshot(existing, shared);
            return;
        }
        EnsureIdle();
        var bytes = Encoding.UTF8.GetBytes(savedPlan.RenderSourceProfile(accountId));
        _ = Snapshot(Parse(bytes), shared);
        var folder = Path.GetDirectoryName(ProfilePath)!;
        if (Directory.EnumerateFiles(folder, "*.json").Take(MaxProfiles).Count() == MaxProfiles)
            throw new InvalidDataException("BetterGI 一条龙档案已达到可核对的数量上限。");

        var staged = Path.Combine(folder, ".vivian-connect-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            EnsureIdle();
            if (FindProfile(ct) is null)
                File.Move(staged, ProfilePath, overwrite: false);
            _ = Snapshot(ResolveProfile(ct), shared);
        }
        finally
        {
            File.Delete(staged);
        }
    }

    public Task<BetterGiSourceSettingsSnapshot> ReadAsync(CancellationToken ct = default) =>
        ReadOnlyFileAccess.BoundedAsync(() =>
        {
            EnsureVersion();
            return Snapshot(ResolveProfile(ct), ReadSource(SharedConfigPath));
        }, ReadOnlyFileAccess.QueryTimeout, ct);

    public Task<BetterGiSourceSettingsSnapshot> UpdateProfileAsync(string expectedRevision,
        IReadOnlyDictionary<string, string> changes, CancellationToken ct = default) =>
        UpdateAsync(expectedRevision, changes, shared: false, ct);

    /// <summary>Updates installation-wide domain counts after the caller explicitly chose the shared scope.</summary>
    public Task<BetterGiSourceSettingsSnapshot> UpdateSharedDomainAsync(string expectedRevision,
        IReadOnlyDictionary<string, string> changes, CancellationToken ct = default) =>
        UpdateAsync(expectedRevision, changes, shared: true, ct);

    private Task<BetterGiSourceSettingsSnapshot> UpdateAsync(string expectedRevision,
        IReadOnlyDictionary<string, string> changes, bool shared, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRevision);
        ArgumentNullException.ThrowIfNull(changes);
        var proposal = changes.ToArray();
        return Task.Run(() =>
        {
            if (new DriveInfo(Path.GetPathRoot(InstallRoot)!).DriveType != DriveType.Fixed)
                throw new InvalidOperationException("BetterGI 设置写入只支持本地固定磁盘。");
            using var lease = DesktopOperationLease.AcquireAt(_lockPath, InstallRoot, requireExisting: true);
            EnsureVersion();
            EnsureIdle();
            var profile = ResolveProfile(ct);
            var global = ReadSource(SharedConfigPath);
            var before = Snapshot(profile, global);
            var target = shared ? global : profile;
            var path = shared ? SharedConfigPath : ProfilePath;
            var current = shared ? before.SharedDomainValues : before.ProfileValues;
            if (target.Revision != expectedRevision)
                throw new BetterGiSourceSettingsConflictException(path);

            var changed = false;
            foreach (var (key, raw) in proposal)
            {
                if (!current.TryGetValue(key, out var existing) || raw is null)
                    throw new ArgumentException("此 BetterGI 设置未登记、目标任务缺失或任务名称有歧义。", nameof(changes));
                var parameter = Parameters[key];
                var value = ParameterRules.Normalize(parameter, raw);
                if (value == existing) continue;
                if (!ParameterRules.IsValid(parameter, value))
                    throw new ArgumentException($"“{parameter.Title}”的设置无效。{ParameterRules.Describe(parameter)}。", nameof(changes));
                changed = true;
                if (shared)
                {
                    var domain = ObjectMember(target.Json, DomainGroup, ignoreCase: false);
                    if (domain is null)
                        target.Json[DomainGroup] = domain = new JsonObject();
                    domain[DomainField(key)] = int.Parse(value, CultureInfo.InvariantCulture);
                }
                else
                {
                    SetProfileValue(target.Json, key, value, before.WeeklyDomainEnabled);
                }
            }

            if (!changed) return before;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(target.Json, WriteJson);
            if (bytes.Length > BetterGiConfiguration.MaxConfigBytes)
                throw new InvalidDataException("写入后的 BetterGI 设置超过大小限制。");
            var updated = Parse(bytes);
            var after = shared ? Snapshot(profile, updated) : Snapshot(updated, global);

            ct.ThrowIfCancellationRequested();
            ReplaceUnchanged(path, target.Revision, bytes);
            return after;
        }, ct);
    }

    private void ReplaceUnchanged(string path, string revision, byte[] bytes)
    {
        var staged = path + ".vivian-sync-" + Guid.NewGuid().ToString("N") + ".tmp";
        var backup = Path.ChangeExtension(staged, ".backup");
        try
        {
            using (var file = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }

            EnsureIdle();
            // BetterGI saves in place. Keep its writers excluded while allowing an atomic Windows replacement.
            using var current = OpenSource(path, FileShare.Read | FileShare.Delete);
            if (Hash(ReadBytes(current)) != revision)
                throw new BetterGiSourceSettingsConflictException(path);
            File.Replace(staged, path, backup, ignoreMetadataErrors: false);

            if (ReadSource(backup).Revision != revision || ReadSource(path).Revision != Hash(bytes))
                throw new BetterGiSourceSettingsConflictException(path, backup);
            File.Delete(backup);
        }
        finally
        {
            File.Delete(staged);
        }
    }

    private void EnsureIdle()
    {
        if (_processes.IsBusy(ProgramPath))
            throw new InvalidOperationException("BetterGI 正在运行。退出原软件后才能写入源设置。");
        if (HasFile(ProfilePath + ".vivian-recovery") || HasFile(ProfilePath + ".vivian-displaced")
            || HasFile(Path.Combine(InstallRoot, "protocol-recovery.json"))
            || (Path.GetDirectoryName(InstallRoot) is { } parent && HasFile(Path.Combine(parent, "protocol-recovery.json")))
            || HasSyncBackup(ProfilePath) || HasSyncBackup(SharedConfigPath))
            throw new InvalidOperationException("BetterGI 有待核对的恢复记录，暂时不能写入源设置。");
    }

    private static bool HasFile(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static bool HasSyncBackup(string path) =>
        Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".vivian-sync-*.backup").Any();

    private void EnsureVersion()
    {
        using var program = OpenSource(ProgramPath, FileShare.Read, boundedConfig: false);
        if (!_supportsVersion(ProgramPath))
            throw new InvalidOperationException($"源设置同步只支持 BetterGI {BetterGiContract.PinnedTag}。");
    }

    private static bool IsPinnedVersion(string program)
    {
        var info = FileVersionInfo.GetVersionInfo(program);
        return info.FileVersion == BetterGiContract.PinnedTag + ".0"
            && (info.ProductVersion == BetterGiContract.PinnedTag
                || info.ProductVersion?.StartsWith(BetterGiContract.PinnedTag + "+", StringComparison.Ordinal) == true
                && BetterGiInstallationInspector.SourceRevision(program) is { } revision
                && BetterGiContract.PinnedCommit.StartsWith(revision, StringComparison.Ordinal));
    }

    private SourceFile ResolveProfile(CancellationToken ct) => FindProfile(ct)
        ?? throw new InvalidDataException("BetterGI 找不到明确绑定的一条龙配置。");

    private SourceFile? FindProfile(CancellationToken ct)
    {
        var folder = Path.GetDirectoryName(ProfilePath)!;
        if (ReadOnlyFileAccess.FindReparsePoint(folder) is not null)
            throw new InvalidDataException("BetterGI 一条龙配置目录含链接。");
        SourceFile? match = null;
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            if (++count > MaxProfiles)
                throw new InvalidDataException("BetterGI 一条龙配置超过 256 份，无法确认目标唯一。");
            var source = ReadSource(path);
            var samePath = string.Equals(path, ProfilePath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            var sameName = StringMember(source.Json, "Name") == ProfileName;
            if (!samePath && !sameName) continue;
            if (match is not null || !samePath || !sameName)
                throw new InvalidDataException("BetterGI 一条龙配置名称不唯一，或与文件名不一致。");
            match = source;
        }
        return match;
    }

    private static BetterGiSourceSettingsSnapshot Snapshot(SourceFile profile, SourceFile global)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var enabled = ObjectMember(profile.Json, "TaskEnabledList");
        var taskIds = TaskIds(profile.Json);
        foreach (var (key, id) in taskIds)
            values[key] = Boolean(enabled![id], id) ? "true" : "false";
        foreach (var field in ProfileFields)
            values[field] = ProfileValue(profile.Json, field);

        var domain = ObjectMember(global.Json, DomainGroup, ignoreCase: false);
        var shared = SharedFields.ToDictionary(key => key, key =>
            Integer(domain?[DomainField(key)], DomainField(key)).ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal);
        return new(profile.Revision, global.Revision, new ReadOnlyDictionary<string, string>(values),
            new ReadOnlyDictionary<string, string>(shared), Boolean(Member(profile.Json, "WeeklyDomainEnabled"), "WeeklyDomainEnabled"),
            Boolean(domain?["specifyResinUse"], "specifyResinUse"), Boolean(domain?["autoArtifactSalvage"], "autoArtifactSalvage"));
    }

    private static string ProfileValue(JsonObject profile, string field)
    {
        if (field == "MinResinToKeep")
            return Integer(Member(profile, field), field).ToString(CultureInfo.InvariantCulture);
        var value = StringMember(profile, field);
        return string.IsNullOrEmpty(value) && field is ("DailyRewardPartyName" or "PartyName" or "DomainName")
            ? field == "DomainName" ? NoDomain : CurrentParty
            : value ?? Parameters[field].DefaultValue;
    }

    private static void SetProfileValue(JsonObject profile, string key, string value, bool weekly)
    {
        if (Tasks.Any(task => task.Key == key))
        {
            var id = TaskIds(profile)[key];
            ObjectMember(profile, "TaskEnabledList")![id] = value == "true";
            return;
        }
        if (weekly && key is ("PartyName" or "DomainName"))
            throw new InvalidOperationException("已启用按星期配置，请在 BetterGI 高级设置中编辑秘境和队伍。");
        if ((key == "DomainName" && value == NoDomain) || (key is ("DailyRewardPartyName" or "PartyName") && value == CurrentParty))
            value = "";
        profile[MemberKey(profile, key)] = key == "MinResinToKeep"
            ? JsonValue.Create(int.Parse(value, CultureInfo.InvariantCulture))
            : JsonValue.Create(value);
    }

    private static Dictionary<string, string> TaskIds(JsonObject profile)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var enabled = ObjectMember(profile, "TaskEnabledList");
        if (enabled is null) return result;
        var definitions = ObjectMember(profile, "TaskDefinitions");
        var configuredOrder = Member(profile, "TaskOrder");
        var order = configuredOrder is null ? null : configuredOrder.AsArray();
        var ids = order is { Count: > 0 } ? order.Select(entry => Text(entry, "TaskOrder")!) : enabled.Select(entry => entry.Key);
        var byName = ids.Where(enabled.ContainsKey).Select(id =>
            (Id: id, Name: definitions is { Count: > 0 } ? Text(definitions[id], "TaskDefinitions") : id)).ToArray();
        foreach (var (key, name) in Tasks)
        {
            var matches = byName.Where(task => task.Name == name).Take(2).ToArray();
            if (matches.Length == 1) result[key] = matches[0].Id;
        }
        return result;
    }

    private static string DomainField(string key) => key[(DomainGroup.Length + 1)..];

    private static string MemberKey(JsonObject obj, string key)
    {
        var matches = obj.Select(property => property.Key)
            .Where(name => name.Equals(key, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (matches.Length > 1)
            throw new InvalidDataException($"BetterGI 设置包含重复字段 {key}。");
        return matches.FirstOrDefault() ?? key;
    }

    private static JsonNode? Member(JsonObject obj, string key) => obj[MemberKey(obj, key)];
    private static JsonObject? ObjectMember(JsonObject obj, string key, bool ignoreCase = true)
    {
        var value = ignoreCase ? Member(obj, key) : obj[key];
        return value is null ? null : value.AsObject();
    }
    private static string? StringMember(JsonObject obj, string key) => Text(Member(obj, key), key);
    private static string? Text(JsonNode? value, string key) => value is null ? null
        : value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text
        : throw new InvalidDataException($"BetterGI 设置 {key} 需要字符串。");
    private static bool Boolean(JsonNode? value, string key) => value is null ? false
        : value is JsonValue scalar && scalar.TryGetValue<bool>(out var flag) ? flag
        : throw new InvalidDataException($"BetterGI 设置 {key} 需要布尔值。");
    private static int Integer(JsonNode? value, string key) => value is null ? 0
        : value is JsonValue scalar && scalar.TryGetValue<int>(out var number) ? number
        : throw new InvalidDataException($"BetterGI 设置 {key} 需要整数。");

    private static SourceFile ReadSource(string path)
    {
        using var stream = OpenSource(path, FileShare.Read);
        return Parse(ReadBytes(stream));
    }

    private static FileStream OpenSource(string path, FileShare share, bool boundedConfig = true)
    {
        if (ReadOnlyFileAccess.FindReparsePoint(path) is not null)
            throw new InvalidDataException("BetterGI 源设置路径含链接。");
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, share);
        if (ReadOnlyFileAccess.HandlePathMatches(stream.SafeFileHandle, path)
            && (!OperatingSystem.IsWindows() || ReadOnlyFileAccess.LinkCount(stream.SafeFileHandle) == 1)
            && (!boundedConfig || stream.Length <= BetterGiConfiguration.MaxConfigBytes))
            return stream;
        stream.Dispose();
        throw new InvalidDataException("BetterGI 源文件的实际路径、链接数或大小不符。");
    }

    private static byte[] ReadBytes(FileStream stream)
    {
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static SourceFile Parse(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var json = JsonNode.Parse(reader.ReadToEnd(), documentOptions: ReadJson) as JsonObject
            ?? throw new InvalidDataException("BetterGI 源设置需要 JSON 对象。");
        return new(Hash(bytes), json);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private sealed record SourceFile(string Revision, JsonObject Json);
}
