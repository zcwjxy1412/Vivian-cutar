using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Processes;

namespace VIvian.Infrastructure.Engines.BetterGi;

/// <summary>Prepares the pinned BetterGI acceptance installation and its single-account daily plan.</summary>
public sealed class BetterGiAcceptanceStage
{
    private const string StageFolder = "VIvian M6 BetterGI Acceptance";
    private const string ArchiveMarker = "verified-archive.txt";
    private const string ArchiveDigest = "28D4756E55F32637538BF84427F5F31AFD5B153BF996530C5975BD9C8C0B9343";
    private const string ProgramDigest = "73732F440BC8FF3B0272F2ADBBF72ACADCB952D45BF7F5A62CF0E3573DBD7D73";
    private const long ArchiveBytes = 465128660;
    private const int MaxPlanBytes = 64 * 1024;
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemAccount = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier OwnerRights = new("S-1-3-4");
    private static readonly JsonDocumentOptions PlanJson = new() { AllowDuplicateProperties = false };

    private BetterGiAcceptanceStage(string stagePath, string profileName)
    {
        StagePath = stagePath;
        InstallRoot = Path.Combine(stagePath, "BetterGI");
        ProgramPath = Path.Combine(InstallRoot, BetterGiContract.ProgramName);
        ProfileName = profileName;
    }

    public string StagePath { get; }
    public string InstallRoot { get; }
    public string ProgramPath { get; }
    public string ProfileName { get; }

    public static async Task<BetterGiAcceptanceStage> PrepareAsync(string archivePath, Guid accountId,
        string gameExecutable, BetterGiPlanFiles plan, CancellationToken ct = default)
    {
        if (!WindowsProcessSupervisor.IsCurrentProcessElevated())
            throw new InvalidOperationException("请从原 VIvian 入口以管理员身份运行，才能准备 BetterGI 隔离目录。");
        ValidatePlan(accountId, gameExecutable, plan);
        if (!File.Exists(gameExecutable))
            throw new InvalidOperationException("原神客户端路径不存在，未准备隔离目录。");
        ct.ThrowIfCancellationRequested();

        var parent = new DirectoryInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StageFolder));
        if (ReadOnlyFileAccess.FindReparsePoint(parent.FullName) is not null)
            throw new InvalidOperationException("BetterGI 隔离目录路径含链接。");
        if (parent.Exists)
        {
            RequireTrusted(parent, requireProtected: true);
            if (await FindUnusedAsync(parent, plan, ct).ConfigureAwait(false) is { } existing)
                return existing;
        }

        if (!Path.IsPathFullyQualified(archivePath) || !File.Exists(archivePath))
            throw new InvalidOperationException("缺少官方 BetterGI 0.66.0 发行包，未准备隔离目录。");
        await using var archive = OpenVerified(archivePath, ArchiveBytes);
        if (archive.Length != ArchiveBytes || await DigestAsync(archive, ct).ConfigureAwait(false) != ArchiveDigest)
            throw new InvalidDataException("BetterGI 0.66.0 官方发行包摘要不符，未准备隔离目录。");
        archive.Position = 0;

        var security = ProtectedDirectorySecurity();
        if (!parent.Exists) parent.Create(security);
        RequireTrusted(parent, requireProtected: true);
        var directory = new DirectoryInfo(Path.Combine(parent.FullName, Guid.NewGuid().ToString("N")));
        directory.Create(security);
        RequireTrusted(directory, requireProtected: true);
        var stage = new BetterGiAcceptanceStage(directory.FullName, plan.ProfileName);

        var localArchive = Path.Combine(stage.StagePath, "official.7z");
        await using (var local = new FileStream(localArchive, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await archive.CopyToAsync(local, ct).ConfigureAwait(false);
            local.Flush(flushToDisk: true);
        }
        using (var verified = OpenVerified(localArchive, ArchiveBytes))
        {
            if (await DigestAsync(verified, ct).ConfigureAwait(false) != ArchiveDigest)
                throw new InvalidDataException("隔离目录中的发行包与官方摘要不符。");
            await ExtractAsync(localArchive, stage.StagePath, ct).ConfigureAwait(false);
        }
        File.Delete(localArchive);

        await VerifyProgramAsync(stage, ct).ConfigureAwait(false);
        await WritePlanAsync(stage.StagePath, accountId, gameExecutable, plan, ct).ConfigureAwait(false);
        CheckTree(new DirectoryInfo(stage.InstallRoot), assignOwner: true, ct);
        var marker = Path.Combine(stage.StagePath, ArchiveMarker);
        await WriteNewAsync(marker, ArchiveDigest, ct).ConfigureAwait(false);
        RequireTrusted(new FileInfo(marker), assignOwner: true);
        return stage;
    }

    internal static async Task WritePlanAsync(string stagePath, Guid accountId, string gameExecutable,
        BetterGiPlanFiles plan, CancellationToken ct)
    {
        ValidatePlan(accountId, gameExecutable, plan);
        var stage = new BetterGiAcceptanceStage(stagePath, plan.ProfileName);
        var (profile, settings) = stage.PlanPaths();
        if (ReadOnlyFileAccess.FindReparsePoint(profile) is not null
            || ReadOnlyFileAccess.FindReparsePoint(settings) is not null)
            throw new InvalidDataException("隔离任务设置路径含链接。");
        if (!Directory.Exists(Path.GetDirectoryName(settings)) || Exists(profile) || Exists(settings)
            || Exists(Path.Combine(stagePath, ArchiveMarker)))
            throw new InvalidOperationException("隔离目录已有任务设置或不完整，未覆盖任何文件。");
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
        await WriteNewAsync(profile, plan.ProfileJson, ct).ConfigureAwait(false);
        await WriteNewAsync(settings, plan.SettingsJson, ct).ConfigureAwait(false);
        if (!await stage.PlanMatchesAsync(plan, ct).ConfigureAwait(false))
            throw new InvalidDataException("隔离任务设置写入后无法按原计划回读。");
    }

    private static void ValidatePlan(Guid accountId, string gameExecutable, BetterGiPlanFiles plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (accountId == Guid.Empty || plan.ProfileName != BetterGiAccountPlan.SourceProfileName(accountId)
            || Encoding.UTF8.GetByteCount(plan.ProfileJson) > MaxPlanBytes || Encoding.UTF8.GetByteCount(plan.SettingsJson) > MaxPlanBytes)
            throw new InvalidDataException("隔离任务计划与指定原神账号不符或超过大小限制。");

        using var profile = JsonDocument.Parse(plan.ProfileJson, PlanJson);
        using var settings = JsonDocument.Parse(plan.SettingsJson, PlanJson);
        var expected = BetterGiAccountPlans.RebuildSavedRun(accountId, gameExecutable, profile.RootElement);
        using var expectedProfile = JsonDocument.Parse(expected.ProfileJson);
        using var expectedSettings = JsonDocument.Parse(expected.SettingsJson);
        if (!JsonElement.DeepEquals(profile.RootElement, expectedProfile.RootElement)
            || !JsonElement.DeepEquals(settings.RootElement, expectedSettings.RootElement))
            throw new InvalidDataException("隔离目录只接受与当前客户端相符的固定原神任务计划。");
    }

    private static async Task<BetterGiAcceptanceStage?> FindUnusedAsync(DirectoryInfo parent, BetterGiPlanFiles plan, CancellationToken ct)
    {
        foreach (var directory in parent.EnumerateDirectories().Take(128))
        {
            ct.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(directory.Name, "N", out _)) continue;
            try
            {
                RequireTrusted(directory);
                if (directory.EnumerateFileSystemInfos().Any(entry => entry.Name is not ("BetterGI" or ArchiveMarker)))
                    continue;
                var marker = new FileInfo(Path.Combine(directory.FullName, ArchiveMarker));
                RequireTrusted(marker);
                if ((await ReadTextAsync(marker.FullName, 128, ct).ConfigureAwait(false)).Trim() != ArchiveDigest) continue;
                var stage = new BetterGiAcceptanceStage(directory.FullName, plan.ProfileName);
                if (Exists(Path.Combine(stage.InstallRoot, BetterGiContract.LogFolder))
                    || !await stage.PlanMatchesAsync(plan, ct).ConfigureAwait(false)) continue;
                await VerifyProgramAsync(stage, ct).ConfigureAwait(false);
                CheckTree(new DirectoryInfo(stage.InstallRoot), assignOwner: false, ct);
                return stage;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                // An inaccessible, incomplete or changed stage is left intact.
            }
        }
        return null;
    }

    private (string Profile, string Settings) PlanPaths()
    {
        var user = Path.Combine(InstallRoot, "User");
        return (Path.Combine(user, "OneDragon", ProfileName + ".json"), Path.Combine(user, "config.json"));
    }

    private async Task<bool> PlanMatchesAsync(BetterGiPlanFiles plan, CancellationToken ct)
    {
        var (profile, settings) = PlanPaths();
        return await ReadTextAsync(profile, MaxPlanBytes, ct).ConfigureAwait(false) == plan.ProfileJson
            && await ReadTextAsync(settings, MaxPlanBytes, ct).ConfigureAwait(false) == plan.SettingsJson;
    }

    private static async Task VerifyProgramAsync(BetterGiAcceptanceStage stage, CancellationToken ct)
    {
        if (!BetterGiContract.RequiredFolders.All(folder => Directory.Exists(Path.Combine(stage.InstallRoot, folder))))
            throw new InvalidDataException("隔离 BetterGI 安装缺少必要目录。");
        await using var program = OpenVerified(stage.ProgramPath, 128 * 1024 * 1024);
        if (await DigestAsync(program, ct).ConfigureAwait(false) != ProgramDigest)
            throw new InvalidDataException("隔离 BetterGI.exe 与固定官方版本不一致。");
    }

    private static async Task ExtractAsync(string archive, string stagePath, CancellationToken ct)
    {
        var spec = new ProcessLaunchSpec(Path.Combine(Environment.SystemDirectory, "tar.exe"),
            ["--no-acls", "--no-same-permissions", "-xf", archive, "-C", stagePath], stagePath);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        await using var group = await new WindowsProcessSupervisor().StartAsync(spec, Guid.NewGuid(), deadline.Token)
            .ConfigureAwait(false);
        try
        {
            var exitCode = await group.WaitForRootExitAsync(deadline.Token).ConfigureAwait(false);
            var activeCount = group.ActiveProcessCount;
            if (exitCode != 0 || activeCount != 0)
            {
                var failure = new IOException("官方 BetterGI 发行包解压失败或解压进程未完全退出。");
                failure.Data["Operation"] = $"BetterGI extraction: exit={exitCode}, active={activeCount}";
                throw failure;
            }
        }
        catch (OperationCanceledException)
        {
            await group.TerminateAllAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException("官方 BetterGI 发行包解压超时。");
        }
    }

    private static DirectorySecurity ProtectedDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        // Before the owner is normalized, a newly extracted file must not grant its creator implicit WRITE_DAC.
        security.AddAccessRule(new FileSystemAccessRule(OwnerRights, FileSystemRights.ChangePermissions,
            inherit, PropagationFlags.None, AccessControlType.Deny));
        foreach (var identity in new[] { Administrators, SystemAccount })
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
                inherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private static void CheckTree(DirectoryInfo directory, bool assignOwner, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequireTrusted(directory, assignOwner: assignOwner);
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            if (entry is DirectoryInfo child)
                CheckTree(child, assignOwner, ct);
            else
            {
                RequireTrusted(entry, assignOwner: assignOwner);
                using var file = OpenVerified(entry.FullName, long.MaxValue);
            }
        }
    }

    private static void RequireTrusted(FileSystemInfo item, bool requireProtected = false, bool assignOwner = false)
    {
        item.Refresh();
        if (!item.Exists || (item.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("隔离 BetterGI 文件缺失或路径含链接。");
        FileSystemSecurity security = item is DirectoryInfo folder ? folder.GetAccessControl() : ((FileInfo)item).GetAccessControl();
        var allows = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow).ToArray();
        if ((requireProtected && !security.AreAccessRulesProtected) || allows.Length == 0
            || allows.Any(rule => !TrustedIdentity(rule.IdentityReference)))
            throw new InvalidDataException("隔离 BetterGI 安装没有管理员专用权限。");
        if (TrustedIdentity(security.GetOwner(typeof(SecurityIdentifier)))) return;
        if (!assignOwner)
            throw new InvalidDataException("隔离 BetterGI 文件不属于管理员。");
        security.SetOwner(Administrators);
        if (item is DirectoryInfo directory)
            directory.SetAccessControl((DirectorySecurity)security);
        else
            ((FileInfo)item).SetAccessControl((FileSecurity)security);
    }

    private static bool TrustedIdentity(IdentityReference? identity) => Administrators.Equals(identity) || SystemAccount.Equals(identity);

    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static FileStream OpenVerified(string path, long maxBytes)
    {
        if (ReadOnlyFileAccess.FindReparsePoint(path) is not null)
            throw new InvalidDataException("隔离 BetterGI 文件路径含链接。");
        var file = ReadOnlyFileAccess.OpenForStableRead(path);
        if (file.Length <= maxBytes && ReadOnlyFileAccess.HandlePathMatches(file.SafeFileHandle, path)
            && (!OperatingSystem.IsWindows() || ReadOnlyFileAccess.LinkCount(file.SafeFileHandle) == 1))
            return file;
        file.Dispose();
        throw new InvalidDataException("隔离 BetterGI 文件身份、链接数或大小不符合要求。");
    }

    private static async Task<string> ReadTextAsync(string path, int maxBytes, CancellationToken ct)
    {
        await using var file = OpenVerified(path, maxBytes);
        using var reader = new StreamReader(file);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    private static async Task WriteNewAsync(string path, string text, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await file.WriteAsync(Encoding.UTF8.GetBytes(text), ct).ConfigureAwait(false);
        file.Flush(flushToDisk: true);
    }

    private static async Task<string> DigestAsync(FileStream file, CancellationToken ct) =>
        Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false));
}
