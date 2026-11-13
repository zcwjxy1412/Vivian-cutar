using System.Text.RegularExpressions;
using VIvian.Core;
using VIvian.Core.Engines;

namespace VIvian.Infrastructure.Engines.OneDragon;

/// <summary>
/// Read-only inspection of the integrated launcher and its initialization prerequisites.
/// Identity covers the launcher executable. Code, resources, elevation and the game run need separate evidence.
/// </summary>
public static partial class OneDragonInstallationInspector
{
    public static Task<EngineReadinessReport> InspectAsync(string? root, CancellationToken ct = default) =>
        InspectAsync(root, TimeProvider.System, ct);

    public static async Task<EngineReadinessReport> InspectAsync(string? root, TimeProvider clock, CancellationToken ct = default)
    {
        var report = await EngineReadinessInspector.InspectAsync(GameId.ZenlessZoneZero, root, march7thEvidence: null, clock, ct)
            .ConfigureAwait(false);
        if (report.ProgramPath is null
            || report.Compatibility is CompatibilityClass.NotConfigured or CompatibilityClass.NotInstalled or CompatibilityClass.Unavailable)
            return report;

        var directory = Path.GetDirectoryName(report.ProgramPath)!;
        var extra = new List<ReadinessCheck>();
        var compatibility = report.Compatibility;

        var integrated = string.Equals(Path.GetFileName(report.ProgramPath), OneDragonContract.EntryProgram,
            StringComparison.OrdinalIgnoreCase);
        if (!integrated)
        {
            extra.Add(new("od:entry", "受管入口", CheckState.Failed, EngineErrorCategory.UnsafeLayout,
                $"没有 {OneDragonContract.EntryProgram}。{OneDragonContract.RawLauncherProgram} 会派生独立的 Python 进程，可能在启动器退出后继续运行，VIvian 不以它作为入口。",
                "安装官方 WithRuntime-Full 完整包。"));
            compatibility = CompatibilityClass.UnsafeLayout;
        }
        var supportedPath = IsSupportedInstallPath(directory);
        if (!supportedPath)
        {
            extra.Add(new("od:path", "安装路径", CheckState.Failed, EngineErrorCategory.PathInvalid,
                "请选择无空格的本机英文路径。目录名支持版本号中的点号。",
                "把 OneDragon 移动到类似 D:\\OneDragon 的路径。"));
            compatibility = CompatibilityClass.UnsafeLayout;
        }
        try
        {
            foreach (var folder in OneDragonContract.RequiredFolders)
            {
                var full = Path.Combine(directory, folder);
                var (exists, link) = await ReadOnlyFileAccess.BoundedAsync(
                    () => (Directory.Exists(full), Directory.Exists(full) ? ReadOnlyFileAccess.FindReparsePoint(full) : null),
                    ReadOnlyFileAccess.QueryTimeout, ct).ConfigureAwait(false);
                if (!exists || link is not null)
                {
                    extra.Add(new("od:" + folder, folder, CheckState.Failed, link is null ? EngineErrorCategory.UnsafeLayout : EngineErrorCategory.LinkRejected,
                        link is null ? $"缺少 {folder} 文件夹（集成启动器缺少 src 时会弹出错误框并退出，级别 B）。" : $"{folder} 是链接（重解析点），未跟随。",
                        "重新解压官方 WithRuntime-Full 完整包。"));
                    compatibility = CompatibilityClass.UnsafeLayout;
                }
                else
                {
                    extra.Add(new("od:" + folder, folder, CheckState.Passed, EngineErrorCategory.None, $"{folder} 文件夹存在。"));
                }
            }
        }
        catch (TimeoutException)
        {
            extra.Add(new("od:access", "访问", CheckState.Failed, EngineErrorCategory.Timeout, "检查 OneDragon 目录超时，没有得出任何结论。"));
            compatibility = CompatibilityClass.Unavailable;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            extra.Add(new("od:access", "访问", CheckState.Failed, EngineErrorCategory.AccessDenied, $"无法读取 OneDragon 目录（{ex.GetType().Name}）。"));
            compatibility = CompatibilityClass.Unavailable;
        }

        var knownLauncher = integrated && string.Equals(report.ProgramSha256, OneDragonContract.LauncherSha256,
            StringComparison.OrdinalIgnoreCase);
        if (integrated)
        {
            extra.Add(knownLauncher
                ? new("od:identity", "启动器身份", CheckState.Passed, EngineErrorCategory.None,
                    $"启动器与官方 {OneDragonContract.PinnedTag} WithRuntime-Full 发行包中的文件一致。")
                : new("od:identity", "启动器身份", CheckState.Failed, EngineErrorCategory.IdentityMismatch,
                    "启动器文件与固定版不同，可能已被 OneDragon 更新替换。", "从已核对的 v2.6.0 WithRuntime-Full 原包一起恢复启动器和运行时。"));
            if (knownLauncher && compatibility is CompatibilityClass.UnknownVersion or CompatibilityClass.PendingValidation)
                compatibility = CompatibilityClass.IdentityVerified;
            else if (!knownLauncher && compatibility == CompatibilityClass.PendingValidation)
                compatibility = CompatibilityClass.UnsafeLayout;

            extra.Add(await OneDragonBootDiagnostics.CheckoutAsync(directory, ct).ConfigureAwait(false));
            extra.Add(await OneDragonBootDiagnostics.UpdatesAsync(directory, ct).ConfigureAwait(false));
            var catalog = await OneDragonInstanceCatalog.ReadAsync(directory, ct).ConfigureAwait(false);
            extra.Add(OneDragonBootDiagnostics.Instances(catalog));
        }

        var checks = report.Checks.Where(c => c.Id != "execution"
            && !(supportedPath && c.Id == "ascii-path")
            && !(integrated && c.Id == "identity")
            && !(knownLauncher && c.Id == "version" && report.DetectedVersion is null))
            .Concat(extra).Concat(report.Checks.Where(c => c.Id == "execution")).ToList();
        return report with
        {
            Checks = checks,
            Compatibility = compatibility,
            DetectedVersion = knownLauncher ? OneDragonContract.PinnedTag : report.DetectedVersion,
        };
    }

    // Keep local-drive and ASCII constraints. Allow only dots internal to a component.
    [GeneratedRegex(@"\A[A-Za-z]:\\[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)*(?:\\[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)*)*\\?\z")]
    private static partial Regex SupportedInstallPath();

    internal static bool IsSupportedInstallPath(string path) => SupportedInstallPath().IsMatch(path);
}
