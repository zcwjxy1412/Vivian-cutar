using System.Diagnostics;
using VIvian.Core;
using VIvian.Core.Engines;

namespace VIvian.Infrastructure.Engines.BetterGi;

/// <summary>
/// Read-only BetterGI installation check. It runs the shared <see cref="EngineReadinessInspector"/> (path, links,
/// program identity, version, known paths) and adds what the pinned source says BetterGI needs to start:
/// the Assets and GameTask folders (missing ones make BetterGI exit with 0xFFFF) and, when the program file carries a
/// source revision, that it matches the pinned commit. It never runs BetterGI, never reads configuration contents and
/// never writes.
/// </summary>
public static class BetterGiInstallationInspector
{
    public static Task<EngineReadinessReport> InspectAsync(string? root, CancellationToken ct = default) =>
        InspectAsync(root, TimeProvider.System, ct);

    public static async Task<EngineReadinessReport> InspectAsync(string? root, TimeProvider clock, CancellationToken ct = default)
    {
        var report = await EngineReadinessInspector.InspectAsync(GameId.Genshin, root, march7thEvidence: null, clock, ct)
            .ConfigureAwait(false);
        // Only an installation the shared check accepted gets the BetterGI-specific checks.
        if (report.Compatibility is not (CompatibilityClass.PendingValidation or CompatibilityClass.UnknownVersion)
            || report.ProgramPath is null)
            return report;

        var directory = Path.GetDirectoryName(report.ProgramPath)!;
        var extra = new List<ReadinessCheck>();
        var compatibility = report.Compatibility;
        try
        {
            foreach (var folder in BetterGiContract.RequiredFolders)
            {
                var full = Path.Combine(directory, folder);
                var (exists, link) = await ReadOnlyFileAccess.BoundedAsync(
                    () => (Directory.Exists(full), Directory.Exists(full) ? ReadOnlyFileAccess.FindReparsePoint(full) : null),
                    ReadOnlyFileAccess.QueryTimeout, ct).ConfigureAwait(false);
                if (!exists)
                {
                    extra.Add(new("bgi:" + folder, folder, CheckState.Failed, EngineErrorCategory.UnsafeLayout,
                        $"缺少 {folder} 文件夹：BetterGI 启动时会因关键文件缺失直接退出（固定源码 RuntimeHelper.CheckIntegration，级别 B）。",
                        "重新解压 BetterGI 官方发行包，不要只复制 BetterGI.exe。"));
                    compatibility = CompatibilityClass.UnsafeLayout;
                }
                else if (link is not null)
                {
                    extra.Add(new("bgi:" + folder, folder, CheckState.Failed, EngineErrorCategory.LinkRejected,
                        $"{folder} 是链接（重解析点），未跟随。", "请使用独立解压的安装目录。"));
                    compatibility = CompatibilityClass.UnsafeLayout;
                }
                else
                {
                    extra.Add(new("bgi:" + folder, folder, CheckState.Passed, EngineErrorCategory.None, $"{folder} 文件夹存在。"));
                }
            }

            var revision = await ReadOnlyFileAccess.BoundedAsync(() => SourceRevision(report.ProgramPath),
                ReadOnlyFileAccess.QueryTimeout, ct).ConfigureAwait(false);
            extra.Add(ClassifyRevision(revision));
        }
        catch (TimeoutException)
        {
            extra.Add(new("bgi:access", "访问", CheckState.Failed, EngineErrorCategory.Timeout, "检查 BetterGI 目录超时，没有得出任何结论。"));
            compatibility = CompatibilityClass.Unavailable;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            extra.Add(new("bgi:access", "访问", CheckState.Failed, EngineErrorCategory.AccessDenied, $"无法读取 BetterGI 目录（{ex.GetType().Name}）。"));
            compatibility = CompatibilityClass.Unavailable;
        }

        // The shared report ends with the "execution disabled" check; keep it last.
        var checks = report.Checks.Where(c => c.Id != "execution").Concat(extra).Concat(report.Checks.Where(c => c.Id == "execution")).ToList();
        return report with { Checks = checks, Compatibility = compatibility };
    }

    /// <summary>The "+revision" suffix of the program's informational (product) version, if it has one.</summary>
    internal static string? SourceRevision(string program)
    {
        try
        {
            var product = FileVersionInfo.GetVersionInfo(program).ProductVersion;
            return ParseRevision(product);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    internal static string? ParseRevision(string? productVersion)
    {
        if (string.IsNullOrWhiteSpace(productVersion)) return null;
        var plus = productVersion.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0) return null;
        var revision = productVersion[(plus + 1)..].Trim();
        return revision.Length is >= 7 and <= 64 && revision.All(Uri.IsHexDigit) ? revision.ToLowerInvariant() : null;
    }

    internal static ReadinessCheck ClassifyRevision(string? revision)
    {
        if (revision is null)
            return new("bgi:revision", "构建提交", CheckState.NotApplicable, EngineErrorCategory.None,
                "程序文件没有记录源码提交号，无法据此比对构建来源。");
        var pinned = BetterGiContract.PinnedCommit;
        var matches = pinned.StartsWith(revision, StringComparison.Ordinal) || revision.StartsWith(pinned, StringComparison.Ordinal);
        return matches
            ? new("bgi:revision", "构建提交", CheckState.Pending, EngineErrorCategory.EvidencePending,
                $"程序文件记录的提交号 {revision[..7]} 与固定提交 {pinned[..7]} 一致。这只是文件中的元数据，不能证明发行包身份。")
            : new("bgi:revision", "构建提交", CheckState.Warning, EngineErrorCategory.IdentityMismatch,
                $"程序文件记录的提交号 {revision[..Math.Min(7, revision.Length)]} 与固定提交 {pinned[..7]} 不一致，可能是其他构建。",
                $"安装官方 {BetterGiContract.PinnedTag} 发行包。");
    }
}
