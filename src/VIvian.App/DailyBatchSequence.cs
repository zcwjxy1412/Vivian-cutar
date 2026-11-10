using System.IO;
using VIvian.Core;
using VIvian.Core.Accounts;
using VIvian.Core.Engines;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Accounts;

namespace VIvian.App;

public delegate Task<MultiAccountOutcome> GenshinDailyBatch(LocalDatabase database, IReadOnlyList<GameAccount> accounts,
    string betterGiRoot, string archivePath, string profileDirectory, Action<BatchAccountProgress>? progress,
    CancellationToken cancellationToken);
public delegate Task<Guid?> GenshinDailyRecovery(string profileDirectory, CancellationToken cancellationToken);

internal static class DailyBatchSequence
{
    internal static async Task RunAsync(GameId? scope,
        Func<CancellationToken, Task<MultiAccountOutcome>>? starRail,
        Func<CancellationToken, Task<MultiAccountOutcome>>? genshin,
        Action<GameId, MultiAccountOutcome> finished, CancellationToken cancellationToken)
    {
        foreach (var (game, run) in new[] { (GameId.StarRail, starRail), (GameId.Genshin, genshin) })
        {
            if (run is null || (scope is { } target && game != target)) continue;
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await run(cancellationToken);
            finished(game, outcome);
            if (!outcome.Completed || !outcome.GameCleanupConfirmed || !outcome.HostCleanupConfirmed) return;
        }
    }

    internal static string FindGenshinArchive(string programDirectory)
    {
        var name = EngineCatalog.For(GameId.Genshin).OfficialAssets.Single().FileName;
        var path = Path.GetFullPath(Path.Combine(programDirectory, "..", "M4D-downloads", name));
        if (!File.Exists(path))
            throw new FileNotFoundException($"找不到 BetterGI 固定发行包。请确认文件存在：{path}", path);
        return path;
    }
}
