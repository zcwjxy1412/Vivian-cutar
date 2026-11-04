using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using VIvian.Core;
using VIvian.Core.Engines;

namespace VIvian.Infrastructure.Engines;

/// <summary>
/// Read-only readiness check for the three engines, producing the same report shape for each. It never runs an engine,
/// never reads configuration contents, never follows links and never writes. Real execution is always reported as
/// disabled for this build.
/// </summary>
public static partial class EngineReadinessInspector
{
    private static readonly TimeSpan HashTimeout = TimeSpan.FromMinutes(2);

    public static Task<EngineReadinessReport> InspectAsync(GameId game, string? root, CancellationToken ct = default) =>
        InspectAsync(game, root, march7thEvidence: null, TimeProvider.System, ct);

    public static async Task<EngineReadinessReport> InspectAsync(GameId game, string? root,
        March7thReleaseEvidence? march7thEvidence, TimeProvider clock, CancellationToken ct = default)
    {
        var profile = EngineCatalog.For(game);
        return game == GameId.StarRail
            ? await InspectMarch7thAsync(profile, root, march7thEvidence, clock, ct).ConfigureAwait(false)
            : await InspectGenericAsync(profile, root, clock, ct).ConfigureAwait(false);
    }

    /// <summary>True when the dotted numbers of <paramref name="detected"/> start with those of <paramref name="pinned"/>
    /// and any further components are zero ("0.66.0.0" matches "v0.66.0", "0.66.1" does not).</summary>
    public static bool VersionMatches(string pinned, string? detected)
    {
        if (detected is null) return false;
        static int[]? Parse(string text)
        {
            var match = VersionPattern().Match(text);
            if (!match.Success) return null;
            return match.Groups[1].Value.Split('.').Select(int.Parse).ToArray();
        }
        var p = Parse(pinned);
        var d = Parse(detected);
        if (p is null || d is null || d.Length < p.Length) return false;
        return p.SequenceEqual(d.Take(p.Length)) && d.Skip(p.Length).All(x => x == 0);
    }

    [GeneratedRegex(@"^\s*v?(\d{1,6}(?:\.\d{1,6}){1,3})", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    /// <summary>The documented OneDragon path rule (docs: one-dragon.com quickstart; level C).</summary>
    [GeneratedRegex(@"^[a-zA-Z]:\\[a-zA-Z0-9_\-\\]+$", RegexOptions.CultureInvariant)]
    private static partial Regex AsciiPath();

    public static bool IsAsciiPathWithoutSpaces(string path) => AsciiPath().IsMatch(path.TrimEnd('\\'));

    private static EngineReadinessReport Report(EngineProfile profile, CompatibilityClass compatibility, List<ReadinessCheck> checks,
        TimeProvider clock, string? version = null, string? program = null, string? sha = null)
    {
        checks.Add(new("execution", "真实执行", CheckState.Pending, EngineErrorCategory.ExecutionDisabled,
            "此开发版不启动任何真实引擎或游戏。", "等待 Windows 桌面验证（见 docs/M3_DESKTOP_TEST_HANDOFF.md）。"));
        return new(profile.Game, profile.EngineName, profile.PinnedVersion, compatibility, version, program, sha, checks, clock.GetUtcNow());
    }

    private static async Task<(bool Ok, EngineReadinessReport? Failed, string Directory)> CheckRootAsync(
        EngineProfile profile, string? root, List<ReadinessCheck> checks, TimeProvider clock, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            checks.Add(new("path", "安装目录", CheckState.Failed, EngineErrorCategory.NotConfigured, "尚未填写安装目录。", "在“引擎管理”中填写安装目录并保存。"));
            return (false, Report(profile, CompatibilityClass.NotConfigured, checks, clock), "");
        }
        var trimmed = root.Trim().Trim('"');
        if (trimmed.Length > 1024 || trimmed.Any(char.IsControl) || !Path.IsPathFullyQualified(trimmed))
        {
            checks.Add(new("path", "安装目录", CheckState.Failed, EngineErrorCategory.PathInvalid, "路径不是完整的本机路径，或包含无效字符。", "填写类似 D:\\Games\\Engine 的完整路径。"));
            return (false, Report(profile, CompatibilityClass.NotConfigured, checks, clock), "");
        }
        var directory = Path.GetFullPath(trimmed);
        var (exists, link) = await ReadOnlyFileAccess.BoundedAsync(
            () => (Directory.Exists(directory), Directory.Exists(directory) ? ReadOnlyFileAccess.FindReparsePoint(directory) : null),
            ReadOnlyFileAccess.QueryTimeout, ct).ConfigureAwait(false);
        if (!exists)
        {
            checks.Add(new("path", "安装目录", CheckState.Failed, EngineErrorCategory.NotInstalled, "未找到指定的安装目录。", "确认目录存在，或重新选择安装目录。"));
            return (false, Report(profile, CompatibilityClass.NotInstalled, checks, clock), directory);
        }
        if (link is not null)
        {
            checks.Add(new("path", "安装目录", CheckState.Failed, EngineErrorCategory.LinkRejected,
                "安装路径中含有链接（符号链接、目录联接或挂载点），未跟随。", "请选择真实的安装目录，而不是链接。"));
            return (false, Report(profile, CompatibilityClass.UnsafeLayout, checks, clock), directory);
        }
        checks.Add(new("path", "安装目录", CheckState.Passed, EngineErrorCategory.None, "目录存在，路径中没有链接。"));
        return (true, null, directory);
    }

    private static async Task<EngineReadinessReport> InspectGenericAsync(EngineProfile profile, string? root, TimeProvider clock, CancellationToken ct)
    {
        var checks = new List<ReadinessCheck>();
        try
        {
            var (ok, failed, directory) = await CheckRootAsync(profile, root, checks, clock, ct).ConfigureAwait(false);
            if (!ok) return failed!;

            if (profile.Launch.Path == PathConstraint.AsciiWithoutSpaces && !IsAsciiPathWithoutSpaces(directory))
                checks.Add(new("ascii-path", "路径字符", CheckState.Warning, EngineErrorCategory.PathInvalid,
                    "官方文档要求安装路径只含英文字母、数字、下划线和连字符，且没有空格；当前路径不符合（文档要求，未经实测）。",
                    "把引擎移动到类似 D:\\OneDragon 的路径。"));

            string? program = null;
            foreach (var name in profile.ExpectedPrograms)
            {
                var candidate = Path.Combine(directory, name);
                if (await ReadOnlyFileAccess.BoundedAsync(() => File.Exists(candidate), ReadOnlyFileAccess.QueryTimeout, ct).ConfigureAwait(false))
                { program = candidate; break; }
            }
            if (program is null)
            {
                checks.Add(new("program", "程序文件", CheckState.Failed, EngineErrorCategory.NotInstalled,
                    $"目录中没有 {string.Join(" 或 ", profile.ExpectedPrograms)}。", "确认选择的是引擎的根目录。"));
                return Report(profile, CompatibilityClass.NotInstalled, checks, clock);
            }
            var programName = Path.GetFileName(program);
            if (!string.Equals(programName, profile.Launch.EntryProgram, StringComparison.OrdinalIgnoreCase))
                checks.Add(new("entry", "受管入口", CheckState.Warning, EngineErrorCategory.UnsafeLayout,
                    $"只找到 {programName}，没有 {profile.Launch.EntryProgram}。{programName} 会派生独立的工作进程，不作为 VIvian 的受管入口。",
                    $"安装包含 {profile.Launch.EntryProgram} 的官方完整包（WithRuntime-Full）。"));

            var identity = await HashProgramAsync(program, ct).ConfigureAwait(false);
            if (identity.Failure is { } failure)
            {
                checks.Add(failure);
                return Report(profile, failure.Category == EngineErrorCategory.LinkRejected ? CompatibilityClass.UnsafeLayout : CompatibilityClass.Unavailable,
                    checks, clock, program: program);
            }
            checks.Add(new("identity", "程序身份", CheckState.Pending, EngineErrorCategory.EvidencePending,
                $"{programName} SHA-256：{identity.Sha256}。没有官方公布的程序文件摘要可比对，身份尚未证实。",
                "桌面验证阶段用官方发行包核对。"));
            if (identity.HardLinks > 1)
                checks.Add(new("hardlink", "硬链接", CheckState.Warning, EngineErrorCategory.UnsafeLayout,
                    $"{programName} 有 {identity.HardLinks} 个硬链接，同一文件也可从其他路径访问。", "使用独立解压的安装目录。"));

            foreach (var known in profile.KnownPaths.Where(k => !profile.ExpectedPrograms.Contains(k.RelativePath, StringComparer.OrdinalIgnoreCase)))
                checks.Add(await CheckKnownPathAsync(directory, known, ct).ConfigureAwait(false));

            var compatibility = ClassifyVersion(profile, identity.Version, checks);
            return Report(profile, compatibility, checks, clock, identity.Version, program, identity.Sha256);
        }
        catch (TimeoutException)
        {
            checks.Add(new("access", "访问", CheckState.Failed, EngineErrorCategory.Timeout, "访问安装目录超时（可能是网络路径或离线磁盘），没有得出任何结论。", "确认磁盘在线后重试。"));
            return Report(profile, CompatibilityClass.Unavailable, checks, clock);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            checks.Add(new("access", "访问", CheckState.Failed, EngineErrorCategory.AccessDenied, $"无法读取安装目录（{ex.GetType().Name}），没有得出任何结论。", "确认有读取权限且文件未被占用后重试。"));
            return Report(profile, CompatibilityClass.Unavailable, checks, clock);
        }
    }

    private static CompatibilityClass ClassifyVersion(EngineProfile profile, string? version, List<ReadinessCheck> checks)
    {
        if (version is null)
        {
            checks.Add(new("version", "版本", CheckState.Warning, EngineErrorCategory.UnsupportedVersion,
                "无法从程序文件读取版本信息。", $"VIvian 只有 {profile.PinnedVersion} 的研究证据。"));
            return CompatibilityClass.UnknownVersion;
        }
        if (!VersionMatches(profile.PinnedVersion, version))
        {
            checks.Add(new("version", "版本", CheckState.Failed, EngineErrorCategory.UnsupportedVersion,
                $"检测到版本 {version}；VIvian 只有 {profile.PinnedVersion} 的证据，其他版本不支持。",
                $"安装官方 {profile.PinnedVersion}，或等待 VIvian 为该版本收集证据。"));
            return CompatibilityClass.UnsupportedVersion;
        }
        checks.Add(new("version", "版本", CheckState.Passed, EngineErrorCategory.None, $"版本 {version} 与固定版本 {profile.PinnedVersion} 一致。"));
        return CompatibilityClass.PendingValidation;
    }

    private static async Task<ReadinessCheck> CheckKnownPathAsync(string directory, KnownInstallPath known, CancellationToken ct)
    {
        var full = Path.GetFullPath(Path.Combine(directory, known.RelativePath));
        if (!full.StartsWith(directory.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return new("path:" + known.RelativePath, known.RelativePath, CheckState.Failed, EngineErrorCategory.Internal, "内部路径越界，已拒绝。");
        var (exists, link) = await ReadOnlyFileAccess.BoundedAsync(() =>
        {
            var exists = known.IsDirectory ? Directory.Exists(full) : File.Exists(full);
            return (exists, exists ? ReadOnlyFileAccess.FindReparsePoint(full) : null);
        }, ReadOnlyFileAccess.QueryTimeout, ct).ConfigureAwait(false);
        var id = "path:" + known.RelativePath;
        if (exists && link is not null)
            return new(id, known.RelativePath, CheckState.Warning, EngineErrorCategory.LinkRejected, $"{known.Purpose}：是链接，未跟随。");
        if (exists)
            return new(id, known.RelativePath, CheckState.Passed, EngineErrorCategory.None, $"{known.Purpose}：存在。");
        return known.Required
            ? new(id, known.RelativePath, CheckState.Failed, EngineErrorCategory.UnsafeLayout, $"{known.Purpose}：缺失。", "重新解压官方完整包。")
            : new(id, known.RelativePath, CheckState.NotApplicable, EngineErrorCategory.None, $"{known.Purpose}：不存在（可选）。");
    }

    private sealed record ProgramIdentity(string? Sha256, string? Version, uint? HardLinks, ReadinessCheck? Failure);

    private static async Task<ProgramIdentity> HashProgramAsync(string program, CancellationToken ct)
    {
        var link = await ReadOnlyFileAccess.BoundedAsync(() => ReadOnlyFileAccess.FindReparsePoint(program), ReadOnlyFileAccess.QueryTimeout, ct).ConfigureAwait(false);
        if (link is not null)
            return new(null, null, null, new("program", "程序文件", CheckState.Failed, EngineErrorCategory.LinkRejected,
                "程序文件是链接（重解析点），未跟随、未读取。", "请选择真实的安装目录。"));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(HashTimeout);
        try
        {
            await using var stream = ReadOnlyFileAccess.OpenForStableRead(program);
            if (!ReadOnlyFileAccess.HandlePathMatches(stream.SafeFileHandle, program))
                return new(null, null, null, new("program", "程序文件", CheckState.Failed, EngineErrorCategory.LinkRejected,
                    "打开的文件与所选路径不一致（可能经过链接），结果作废。"));
            var links = ReadOnlyFileAccess.LinkCount(stream.SafeFileHandle);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, limit.Token).ConfigureAwait(false)).ToLowerInvariant();
            string? version;
            try { version = FileVersionInfo.GetVersionInfo(program).ProductVersion; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { version = null; }
            return new(hash, string.IsNullOrWhiteSpace(version) ? null : version.Trim(), links, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(null, null, null, new("program", "程序文件", CheckState.Failed, EngineErrorCategory.Timeout, "读取程序文件超时，没有得出任何结论。"));
        }
    }

    private static async Task<EngineReadinessReport> InspectMarch7thAsync(EngineProfile profile, string? root,
        March7thReleaseEvidence? evidence, TimeProvider clock, CancellationToken ct)
    {
        var checks = new List<ReadinessCheck>();
        try
        {
            var (ok, failed, directory) = await CheckRootAsync(profile, root, checks, clock, ct).ConfigureAwait(false);
            if (!ok) return failed!;
            var result = await March7thReadOnlyInspector.InspectAsync(directory, evidence, ct).ConfigureAwait(false);
            var (state, category, compatibility) = result.Compatibility switch
            {
                March7thCompatibility.Verified => (CheckState.Passed, EngineErrorCategory.None, CompatibilityClass.IdentityVerified),
                March7thCompatibility.Unverified => (CheckState.Pending, evidence is null ? EngineErrorCategory.EvidencePending : EngineErrorCategory.IdentityMismatch, CompatibilityClass.PendingValidation),
                March7thCompatibility.UnsupportedVersion => (CheckState.Failed, EngineErrorCategory.UnsupportedVersion, CompatibilityClass.UnsupportedVersion),
                March7thCompatibility.MissingCli => (CheckState.Failed, EngineErrorCategory.NotInstalled, CompatibilityClass.NotInstalled),
                March7thCompatibility.NotInstalled => (CheckState.Failed, EngineErrorCategory.NotInstalled, CompatibilityClass.NotInstalled),
                _ => (CheckState.Failed, EngineErrorCategory.AccessDenied, CompatibilityClass.Unavailable),
            };
            // A mismatch against verified official evidence is a real failure, not merely pending.
            if (result.Compatibility == March7thCompatibility.Unverified && evidence is not null) state = CheckState.Failed;
            var action = result.Compatibility switch
            {
                March7thCompatibility.Unverified when evidence is null => "在“引擎管理”中核对官方 March7thAssistant_full.zip。",
                March7thCompatibility.Unverified => "重新解压官方 v2026.10.31 完整包。",
                March7thCompatibility.UnsupportedVersion => "安装官方 v2026.10.31。",
                March7thCompatibility.MissingCli => "安装包含 m7a.exe 的官方完整包。",
                _ => null,
            };
            checks.Add(new("identity", "版本与程序身份", state, category, result.Detail, action));
            if (result.Compatibility is March7thCompatibility.Verified or March7thCompatibility.Unverified or March7thCompatibility.UnsupportedVersion)
                foreach (var known in profile.KnownPaths.Where(k => k.RelativePath != "m7a.exe"))
                    checks.Add(await CheckKnownPathAsync(directory, known, ct).ConfigureAwait(false));
            return Report(profile, compatibility, checks, clock, result.VersionFile ?? result.FileVersion, result.CliPath, result.Sha256);
        }
        catch (TimeoutException)
        {
            checks.Add(new("access", "访问", CheckState.Failed, EngineErrorCategory.Timeout, "访问安装目录超时，没有得出任何结论。", "确认磁盘在线后重试。"));
            return Report(profile, CompatibilityClass.Unavailable, checks, clock);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            checks.Add(new("access", "访问", CheckState.Failed, EngineErrorCategory.AccessDenied, $"无法读取安装目录（{ex.GetType().Name}）。", "确认有读取权限后重试。"));
            return Report(profile, CompatibilityClass.Unavailable, checks, clock);
        }
    }
}
