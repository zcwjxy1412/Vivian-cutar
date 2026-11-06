using VIvian.Core;
using VIvian.Core.Engines;

namespace VIvian.Infrastructure.Engines.OneDragon;

/// <summary>
/// Read-only check of a OneDragon integrated-runtime installation. Runs the shared
/// <see cref="EngineReadinessInspector"/> and tightens it for execution: only the integrated launcher is accepted as
/// the managed entry (the plain launcher spawns a Python process that can outlive it), the documented ASCII path rule
/// is enforced instead of only warned about, and the src / .runtime folders must exist. It never runs OneDragon, never
/// reads its configuration and never writes.
/// </summary>
public static class OneDragonInstallationInspector
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

        if (!string.Equals(Path.GetFileName(report.ProgramPath), OneDragonContract.EntryProgram, StringComparison.OrdinalIgnoreCase))
        {
            extra.Add(new("od:entry", "受管入口", CheckState.Failed, EngineErrorCategory.UnsafeLayout,
                $"没有 {OneDragonContract.EntryProgram}。{OneDragonContract.RawLauncherProgram} 会派生独立的 Python 进程，可能在启动器退出后继续运行，VIvian 不以它作为入口。",
                "安装官方 WithRuntime-Full 完整包。"));
            compatibility = CompatibilityClass.UnsafeLayout;
        }
        if (!EngineReadinessInspector.IsAsciiPathWithoutSpaces(directory))
        {
            extra.Add(new("od:path", "安装路径", CheckState.Failed, EngineErrorCategory.PathInvalid,
                "上游文档要求安装路径只含英文字母、数字、下划线和连字符且没有空格（级别 C，固定源码中未发现检查）。VIvian 在此路径下不启动 OneDragon。",
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

        var checks = report.Checks.Where(c => c.Id != "execution").Concat(extra).Concat(report.Checks.Where(c => c.Id == "execution")).ToList();
        return report with { Checks = checks, Compatibility = compatibility };
    }
}
