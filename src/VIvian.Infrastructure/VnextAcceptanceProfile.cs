using VIvian.Infrastructure.Engines;
using VIvian.Infrastructure.Processes;

namespace VIvian.Infrastructure;

/// <summary>Explicit, isolated acceptance profiles; the formal profile is only a read-only snapshot source.</summary>
public static class VnextAcceptanceProfile
{
    public const string OpenArgument = "--vnext-acceptance";
    public const string PrepareArgument = "--prepare-vnext-acceptance";
    public static string ParentDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VIvian-vNext-Acceptance");

    public static string ValidateDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("验收目录必须为绝对路径。");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!string.Equals(Path.GetDirectoryName(full), ParentDirectory, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(full), "N", out var id) || id == Guid.Empty)
            throw new ArgumentException("验收目录必须位于专用目录且使用本次唯一编号。");
        if (ReadOnlyFileAccess.FindReparsePoint(full) is not null)
            throw new IOException("验收目录不能含链接。");
        return full;
    }

    public static string? ParseOpen(IReadOnlyList<string> args) => Parse(args, OpenArgument);
    public static string? ParsePrepare(IReadOnlyList<string> args) => Parse(args, PrepareArgument);

    private static string? Parse(IReadOnlyList<string> args, string argument)
    {
        if (!args.Contains(argument)) return null;
        if (args.Count != 2 || args[0] != argument) throw new ArgumentException("验收启动参数无效。");
        return ValidateDirectory(args[1]);
    }

    public static async Task PrepareAsync(string destination, CancellationToken ct)
    {
        var output = ValidateDirectory(destination);
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("验收目录已存在，请为本次创建新编号。");
        var source = UnifiedProfilePreparation.InstalledDirectory;
        using var instance = SingleInstanceGuard.Acquire(source);
        if (!instance.IsOwner) throw new InvalidOperationException("请先关闭正式程序，再创建隔离验收副本。");
        using var desktop = DesktopOperationLease.Acquire(source);
        if (File.Exists(Path.Combine(source, "configuration-recovery", "temporary-config-recovery.json")) ||
            File.Exists(Path.Combine(source, "protocol-recovery.json")))
            throw new InvalidOperationException("正式档案仍有配置待恢复，暂不能创建验收副本。");
        Directory.CreateDirectory(ParentDirectory);
        await UnifiedProfilePreparation.CopyPreparedProfileAsync(source, output, ct).ConfigureAwait(false);
    }
}
