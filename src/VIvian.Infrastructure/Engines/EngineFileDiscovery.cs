using System.Diagnostics;
using VIvian.Core;

namespace VIvian.Infrastructure.Engines;

public enum DiscoveryState { NotInstalled, Found, Unavailable }

/// <summary>What a read-only look at an engine folder found. Finding a program never makes it runnable.</summary>
public sealed record EngineDiscovery(DiscoveryState State, string? ProgramPath, string? FileVersion, string Detail);

/// <summary>
/// Finds the expected program of engines that VIvian does not integrate yet (BetterGI, OneDragon). Reads only file
/// metadata, never executes, never follows links. Expected names come from the upstream research in docs/research.
/// </summary>
public static class EngineFileDiscovery
{
    public static IReadOnlyList<string> ExpectedPrograms(GameId game) => game switch
    {
        GameId.Genshin => ["BetterGI.exe"],
        // The integrated launcher is preferred; the plain launcher leaves python.exe running after it exits.
        GameId.ZenlessZoneZero => ["OneDragon-RuntimeLauncher.exe", "OneDragon-Launcher.exe"],
        GameId.StarRail => ["m7a.exe"],
        _ => [],
    };

    public static async Task<EngineDiscovery> InspectAsync(GameId game, string? root, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            return new(DiscoveryState.NotInstalled, null, null, "请填写安装目录的完整路径。");
        var directory = Path.GetFullPath(root);
        try
        {
            return await ReadOnlyFileAccess.BoundedAsync(() =>
            {
                if (!Directory.Exists(directory))
                    return new EngineDiscovery(DiscoveryState.NotInstalled, null, null, "未找到指定的安装目录。");
                foreach (var name in ExpectedPrograms(game))
                {
                    var program = Path.Combine(directory, name);
                    if (!File.Exists(program)) continue;
                    if (ReadOnlyFileAccess.FindReparsePoint(program) is { } link)
                        return new EngineDiscovery(DiscoveryState.Unavailable, program, null,
                            $"路径中的“{link}”是链接（重解析点），未跟随。");
                    string? version;
                    try { version = FileVersionInfo.GetVersionInfo(program).ProductVersion; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                    { version = null; }
                    return new EngineDiscovery(DiscoveryState.Found, program, version, $"找到 {name}（文件版本：{version ?? "未知"}）。");
                }
                return new EngineDiscovery(DiscoveryState.NotInstalled, null, null,
                    $"目录中没有 {string.Join(" 或 ", ExpectedPrograms(game))}。");
            }, ReadOnlyFileAccess.QueryTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new(DiscoveryState.Unavailable, null, null, "访问安装目录超时（可能是网络路径或离线磁盘）。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(DiscoveryState.Unavailable, null, null, $"无法读取安装目录（{ex.GetType().Name}）。");
        }
    }
}
