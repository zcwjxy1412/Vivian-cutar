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
        Action<GameId, MultiAccountOutcome> finished, CancellationToken cancellationToken,
        Func<CancellationToken, Task<MultiAccountOutcome>>? zenless = null)
    {
        foreach (var (game, run) in new[] { (GameId.StarRail, starRail), (GameId.Genshin, genshin), (GameId.ZenlessZoneZero, zenless) })
        {
            if (run is null || (scope is { } target && game != target)) continue;
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await run(cancellationToken);
            finished(game, outcome);
            if (!outcome.Completed || !outcome.GameCleanupConfirmed || !outcome.HostCleanupConfirmed) return;
        }
    }

}
