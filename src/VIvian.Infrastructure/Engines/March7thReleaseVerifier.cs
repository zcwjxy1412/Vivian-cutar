using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace VIvian.Infrastructure.Engines;

/// <summary>An official March7thAssistant release asset whose SHA-256 the publisher recorded.</summary>
public sealed record March7thOfficialAsset(string Version, string FileName, long Length, string Sha256, bool ContainsInstallation);

public enum ReleaseCheck
{
    /// <summary>The archive is byte-identical to a pinned official asset and its m7a.exe was hashed from inside it.</summary>
    Verified,
    /// <summary>The archive is an official asset, but its contents cannot be read here (7z) or are not an installation.</summary>
    OfficialContentsUnavailable,
    /// <summary>Not byte-identical to any pinned official asset.</summary>
    NotOfficial,
    /// <summary>The official ZIP's layout is not what the pinned release defines.</summary>
    UnexpectedLayout,
    /// <summary>The file could not be read, is a link, timed out, or changed while being read.</summary>
    Unavailable,
}

/// <summary>
/// What a verified official archive proves: the SHA-256 of the m7a.exe it contains and the version file it carries.
/// It says nothing about how the program behaves, or whether a task run would be safe.
/// </summary>
public sealed record March7thReleaseEvidence(string Version, string AssetName, string ArchiveSha256, string CliSha256);

public sealed record March7thReleaseResult(ReleaseCheck Check, March7thReleaseEvidence? Evidence, string Detail);

/// <summary>
/// Checks a release archive the user downloaded from the official March7thAssistant GitHub release against the
/// SHA-256 digests the release publishes. Reads only: nothing is extracted to disk and nothing is executed.
/// </summary>
public static class March7thReleaseVerifier
{
    private const string Root = "March7thAssistant_full/";
    private const string CliEntry = Root + "m7a.exe";
    private const string VersionEntry = Root + "assets/config/version.txt";
    private static readonly string[] RequiredEntries = [CliEntry, Root + "March7th Assistant.exe", VersionEntry];

    /// <summary>
    /// Official assets of the pinned release (GitHub release metadata for tag v2026.10.31, commit 1e77d59; see
    /// docs/evidence/M3_MARCH7TH_RELEASE_ASSET_AUDIT.md). Only these exact bytes can produce Verified.
    /// </summary>
    public static IReadOnlyList<March7thOfficialAsset> OfficialAssets { get; } =
    [
        new("v2026.10.31", "March7thAssistant_full.zip", 719711875, "e03aaad4ea27c70f30bf5241ab3bfac8894de7675ddd30a9aef7f1e551af9985", true),
        new("v2026.10.31", "March7thAssistant_full.7z", 474333248, "1fcaa1934e3d30d598ce7863756c49b9e9fb4f4fbb759066a62f52541a39dee0", true),
        new("v2026.10.31", "update.7z", 163523697, "4f9ba139ee485aac770e7498551885aa1ae821c3db019962c0c3e4dc84e097c6", false),
    ];

    public static Task<March7thReleaseResult> VerifyAsync(string? archivePath, CancellationToken ct = default) =>
        VerifyAsync(archivePath, OfficialAssets, ct);

    /// <param name="assets">Test seam: the pinned official assets to compare with.</param>
    internal static async Task<March7thReleaseResult> VerifyAsync(string? archivePath,
        IReadOnlyList<March7thOfficialAsset> assets, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !Path.IsPathFullyQualified(archivePath))
            return new(ReleaseCheck.Unavailable, null, "请选择下载到本机的官方发行包文件（完整路径）。");
        var path = Path.GetFullPath(archivePath);
        try
        {
            var (exists, link, length) = await ReadOnlyFileAccess.BoundedAsync(() =>
            {
                var info = new FileInfo(path);
                return (info.Exists, ReadOnlyFileAccess.FindReparsePoint(path), info.Exists ? info.Length : 0L);
            }, ReadOnlyFileAccess.QueryTimeout, ct).ConfigureAwait(false);
            if (!exists) return new(ReleaseCheck.Unavailable, null, "文件不存在。");
            if (link is not null)
                return new(ReleaseCheck.Unavailable, null, $"路径中的“{link}”是链接（重解析点），未跟随、未读取。");
            var candidates = assets.Where(a => a.Length == length).ToList();
            if (candidates.Count == 0)
                return new(ReleaseCheck.NotOfficial, null, "文件大小与已记录的任何官方发行包都不一致。");

            await using var stream = ReadOnlyFileAccess.OpenForStableRead(path);
            if (!ReadOnlyFileAccess.HandlePathMatches(stream.SafeFileHandle, path))
                return new(ReleaseCheck.Unavailable, null, "打开的文件与所选路径不一致（可能经过链接），未继续。");
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
            var asset = candidates.FirstOrDefault(a => a.Sha256 == digest);
            if (asset is null)
                return new(ReleaseCheck.NotOfficial, null, $"SHA-256 {digest} 与官方发布的摘要不一致。不要使用这个文件。");
            if (!asset.ContainsInstallation || !asset.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return new(ReleaseCheck.OfficialContentsUnavailable, null,
                    $"与官方 {asset.Version} 的 {asset.FileName} 完全一致；但{(asset.ContainsInstallation ? "VIvian 无法读取 7z 内容" : "这是更新包，不含完整安装")}，" +
                    "无法据此核对安装目录中的文件。请改用官方 March7thAssistant_full.zip。");

            // The same open handle (writers denied since it was opened) is now read as a ZIP: the bytes inspected
            // are the bytes that were hashed.
            stream.Position = 0;
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Any(e => e.FullName.StartsWith('/') || e.FullName.Contains("..", StringComparison.Ordinal)
                || e.FullName.Contains('\\', StringComparison.Ordinal)))
                return new(ReleaseCheck.UnexpectedLayout, null, "压缩包含有异常路径，已拒绝（未解压）。");
            var names = zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.Ordinal);
            if (RequiredEntries.Any(e => !names.Contains(e)))
                return new(ReleaseCheck.UnexpectedLayout, null, "官方压缩包中缺少预期的 m7a.exe、主程序或版本文件。");

            string version;
            var versionEntry = zip.GetEntry(VersionEntry)!;
            if (versionEntry.Length > 64) return new(ReleaseCheck.UnexpectedLayout, null, "版本文件异常（过大）。");
            using (var reader = new StreamReader(versionEntry.Open(), Encoding.UTF8))
                version = (await reader.ReadToEndAsync(ct).ConfigureAwait(false)).Trim();
            if (version != asset.Version)
                return new(ReleaseCheck.UnexpectedLayout, null, $"压缩包中的版本文件为“{version}”，与发布版本 {asset.Version} 不一致。");

            string cliHash;
            await using (var cli = zip.GetEntry(CliEntry)!.Open())
                cliHash = Convert.ToHexString(await SHA256.HashDataAsync(cli, ct).ConfigureAwait(false)).ToLowerInvariant();
            return new(ReleaseCheck.Verified, new(asset.Version, asset.FileName, digest, cliHash),
                $"与官方 {asset.Version} 的 {asset.FileName} 完全一致；已从包内读取 m7a.exe 的 SHA-256：{cliHash}。未解压、未运行任何文件。");
        }
        catch (TimeoutException)
        {
            return new(ReleaseCheck.Unavailable, null, "读取文件信息超时（可能是网络路径或离线磁盘）。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new(ReleaseCheck.Unavailable, null, $"无法读取文件（{ex.GetType().Name}），没有得出任何结论。");
        }
    }
}
