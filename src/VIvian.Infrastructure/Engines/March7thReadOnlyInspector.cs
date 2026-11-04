using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace VIvian.Infrastructure.Engines;

public enum March7thCompatibility
{
    /// <summary>No usable installation folder at the given path.</summary>
    NotInstalled,
    /// <summary>The folder has no m7a.exe: this layout is not supported.</summary>
    MissingCli,
    /// <summary>The version file names a release VIvian has no evidence for.</summary>
    UnsupportedVersion,
    /// <summary>Files were read, but nothing ties them to a verified official release.</summary>
    Unverified,
    /// <summary>m7a.exe is byte-identical to the one inside a verified official release archive.</summary>
    Verified,
    /// <summary>The path timed out, is unreadable, is or contains a link, or changed while being read.</summary>
    Unavailable,
}

/// <summary>
/// Observed local files only. Even <see cref="March7thCompatibility.Verified"/> proves file identity, not behaviour:
/// it never authorizes launching an upstream executable (CLI queries and tasks stay disabled).
/// </summary>
public sealed record March7thInstallation(
    March7thCompatibility Compatibility, string? Root, string? CliPath,
    string? FileVersion, string? Sha256, string Detail, string? VersionFile = null)
{
    public bool CanQueryStatus => false;
    public bool CanQueryTasks => false;
}

/// <summary>
/// Inspects a user-selected installation without executing files, reading configuration, or changing anything.
/// </summary>
public static class March7thReadOnlyInspector
{
    /// <summary>Releases VIvian has evidence for (see <see cref="March7thReleaseVerifier.OfficialAssets"/>).</summary>
    public static IReadOnlySet<string> KnownVersions { get; } =
        March7thReleaseVerifier.OfficialAssets.Select(a => a.Version).ToHashSet(StringComparer.Ordinal);

    private static readonly TimeSpan HashTimeout = TimeSpan.FromMinutes(2);

    public static Task<March7thInstallation> InspectAsync(string? root, CancellationToken ct = default) =>
        InspectAsync(root, evidence: null, afterRead: null, ct);

    public static Task<March7thInstallation> InspectAsync(string? root, March7thReleaseEvidence? evidence, CancellationToken ct = default) =>
        InspectAsync(root, evidence, afterRead: null, ct);

    /// <param name="afterRead">Test seam: runs after the file was read and released, before the change check.</param>
    internal static Task<March7thInstallation> InspectAsync(string? root, Func<string, Task>? afterRead, CancellationToken ct) =>
        InspectAsync(root, evidence: null, afterRead, ct);

    internal static async Task<March7thInstallation> InspectAsync(string? root, March7thReleaseEvidence? evidence,
        Func<string, Task>? afterRead, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            return new(March7thCompatibility.NotInstalled, null, null, null, null, "请填写安装目录的完整路径。");
        var directory = Path.GetFullPath(root);
        var cli = Path.Combine(directory, "m7a.exe");
        var versionPath = Path.Combine(directory, "assets", "config", "version.txt");
        try
        {
            // Every metadata query is bounded: an offline network share must not hang the check.
            var (dirExists, cliExists, link) = await ReadOnlyFileAccess.BoundedAsync(() =>
                (Directory.Exists(directory), File.Exists(cli), ReadOnlyFileAccess.FindReparsePoint(cli)),
                ReadOnlyFileAccess.QueryTimeout, ct).ConfigureAwait(false);
            if (!dirExists)
                return new(March7thCompatibility.NotInstalled, null, null, null, null, "未找到指定的安装目录。");
            if (!cliExists)
                return new(March7thCompatibility.MissingCli, directory, null, null, null,
                    "安装目录中没有 m7a.exe：这种安装形式暂不支持。");
            if (link is not null)
                return new(March7thCompatibility.Unavailable, directory, cli, null, null,
                    $"路径中的“{link}”是链接（重解析点），未跟随、未读取；实际读取的文件无法确认，请选择真实的安装目录。");

            var versionFile = await ReadVersionFileAsync(versionPath, ct).ConfigureAwait(false);

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(HashTimeout);
            string hash;
            string? version;
            (uint, ulong)? identity;
            long length;
            DateTime written;
            // Writers are denied while the file is open: the hash and the version metadata describe one content.
            await using (var stream = ReadOnlyFileAccess.OpenForStableRead(cli))
            {
                if (!ReadOnlyFileAccess.HandlePathMatches(stream.SafeFileHandle, cli))
                    return new(March7thCompatibility.Unavailable, directory, cli, null, null,
                        "打开的文件与所选路径不一致（可能经过链接），结果作废。");
                identity = ReadOnlyFileAccess.Identity(stream.SafeFileHandle);
                length = stream.Length;
                written = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
                hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, limit.Token).ConfigureAwait(false)).ToLowerInvariant();
                try { version = FileVersionInfo.GetVersionInfo(cli).ProductVersion; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                { version = null; }
            }
            if (afterRead is not null) await afterRead(cli).ConfigureAwait(false);

            // The same file is still there: same identity (volume + file index), same size, same write time.
            var unchanged = await ReadOnlyFileAccess.BoundedAsync(() =>
            {
                using var again = new FileStream(cli, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return Equals(ReadOnlyFileAccess.Identity(again.SafeFileHandle), identity) && again.Length == length
                    && File.GetLastWriteTimeUtc(again.SafeFileHandle) == written;
            }, ReadOnlyFileAccess.QueryTimeout, ct).ConfigureAwait(false);
            if (!unchanged)
                return new(March7thCompatibility.Unavailable, directory, cli, null, null, "读取期间 CLI 文件发生变化，结果作废，请重新检查。");

            var facts = $"文件版本：{version ?? "未知"} · 版本文件：{versionFile ?? "未找到"} · m7a.exe SHA-256：{hash}。";
            if (versionFile is null || !KnownVersions.Contains(versionFile))
                return new(March7thCompatibility.UnsupportedVersion, directory, cli, version, hash,
                    facts + $"VIvian 只有 {string.Join("、", KnownVersions)} 的发行包证据；这个版本暂不支持。", versionFile);
            if (evidence is null)
                return new(March7thCompatibility.Unverified, directory, cli, version, hash,
                    facts + "尚未用官方发行包核对：请在“引擎设置”中选择下载的官方 March7thAssistant_full.zip 进行验证。", versionFile);
            if (evidence.Version != versionFile || evidence.CliSha256 != hash)
                return new(March7thCompatibility.Unverified, directory, cli, version, hash,
                    facts + $"与已验证的官方 {evidence.Version} 发行包中的 m7a.exe 不一致：安装可能被修改、来自其他版本或其他来源。", versionFile);
            return new(March7thCompatibility.Verified, directory, cli, version, hash,
                facts + $"与已验证的官方 {evidence.AssetName}（{evidence.Version}）中的 m7a.exe 完全一致。" +
                "这只证明文件来源；VIvian 仍不运行它的查询或任务。", versionFile);
        }
        catch (TimeoutException)
        {
            return new(March7thCompatibility.Unavailable, directory, null, null, null, "访问安装目录超时（可能是网络路径或离线磁盘），没有得出任何结论。");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(March7thCompatibility.Unavailable, directory, cli, null, null, "读取 m7a.exe 超时，没有得出任何结论。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked, unreadable, or removed while checking: report conservatively, never guess.
            return new(March7thCompatibility.Unavailable, directory, cli, null, null,
                $"无法读取 CLI 文件（{ex.GetType().Name}），没有得出任何结论。请确认文件未被占用且有读取权限后重试。");
        }
    }

    /// <summary>The release tag from assets/config/version.txt (read-only, at most 64 bytes), or null.</summary>
    private static async Task<string?> ReadVersionFileAsync(string path, CancellationToken ct)
    {
        var usable = await ReadOnlyFileAccess.BoundedAsync(
            () => File.Exists(path) && ReadOnlyFileAccess.FindReparsePoint(path) is null && new FileInfo(path).Length <= 64,
            ReadOnlyFileAccess.QueryTimeout, ct).ConfigureAwait(false);
        if (!usable) return null;
        try
        {
            await using var stream = ReadOnlyFileAccess.OpenForStableRead(path);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = (await reader.ReadToEndAsync(ct).ConfigureAwait(false)).Trim();
            return text.Length is > 0 and <= 32 ? text : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
