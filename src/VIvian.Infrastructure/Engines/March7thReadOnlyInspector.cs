using System.Diagnostics;
using System.Security.Cryptography;

namespace VIvian.Infrastructure.Engines;

public enum March7thCompatibility { NotInstalled, MissingCli, Unverified }

/// <summary>Observed local files only. Unverified never authorizes launching an upstream executable.</summary>
public sealed record March7thInstallation(
    March7thCompatibility Compatibility, string? Root, string? CliPath,
    string? FileVersion, string? Sha256, string Detail)
{
    public bool CanQueryStatus => false;
    public bool CanQueryTasks => false;
}

/// <summary>
/// Inspects a user-selected installation without executing files, reading configuration, or changing the game.
/// CLI status/task queries are gated until a precise release asset and its read-only behavior are verified.
/// </summary>
public static class March7thReadOnlyInspector
{
    public static async Task<March7thInstallation> InspectAsync(string? root, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || !Directory.Exists(root))
            return new(March7thCompatibility.NotInstalled, null, null, null, null, "未找到指定的安装目录。");

        var directory = Path.GetFullPath(root);
        var cli = Path.Combine(directory, "m7a.exe");
        if (!File.Exists(cli))
            return new(March7thCompatibility.MissingCli, directory, null, null, null,
                "安装目录中没有 m7a.exe；发行版接口待确认。");

        // An exe path may be a link. Refuse links until its target and ownership are explicitly reviewed.
        var info = new FileInfo(cli);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            return new(March7thCompatibility.Unverified, directory, cli, null, null,
                "CLI 路径是重解析点，目标未验证。");

        var initialLength = info.Length;
        var initialWrite = info.LastWriteTimeUtc;
        await using var stream = new FileStream(cli, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
        info.Refresh();
        if (info.Length != initialLength || info.LastWriteTimeUtc != initialWrite)
            return new(March7thCompatibility.Unverified, directory, cli, null, null,
                "读取期间 CLI 文件发生变化，请重新检查。");

        string? version;
        try { version = FileVersionInfo.GetVersionInfo(cli).ProductVersion; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { version = null; }

        return new(March7thCompatibility.Unverified, directory, cli, version, hash,
            "已记录本地 CLI 文件哈希；缺少发行包哈希和实际只读命令验收，不执行状态或任务查询。");
    }
}
