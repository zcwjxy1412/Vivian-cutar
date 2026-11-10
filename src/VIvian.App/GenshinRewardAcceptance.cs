using System.Globalization;
using System.IO;
using VIvian.Core;
using VIvian.Core.Accounts;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Accounts;

namespace VIvian.App;

internal delegate Task<MultiAccountOutcome> GenshinDailyVerification(LocalDatabase database, IReadOnlyList<GameAccount> accounts,
    string betterGiRoot, string archivePath, string profileDirectory, bool rewardOnly,
    Action<BatchAccountProgress>? progress, CancellationToken cancellationToken);

internal sealed class GenshinRewardAcceptance
{
    private const string RunOption = "--genshin-reward-acceptance";
    private const string ReviewOption = "--genshin-reward-review";
    internal const string Marker = "VIvian-GenshinRewardAcceptance.marker";
    internal static readonly TimeSpan RunLimit = TimeSpan.FromMinutes(10);
    internal static bool IsCandidateBuild =>
#if VIVIAN_GENSHIN_REWARD_ACCEPTANCE
        true;
#else
        false;
#endif

    private GenshinRewardAcceptance(Guid attempt, Guid? account, long revision, string localAppData)
    {
        Attempt = attempt;
        AccountId = account;
        Revision = revision;
        ProfileDirectory = Path.Combine(localAppData, "VIvian-M6-GenshinAcceptance", attempt.ToString("N"));
    }

    internal Guid Attempt { get; }
    private Guid? AccountId { get; }
    private long Revision { get; }
    internal string ProfileDirectory { get; }
    internal bool ReviewOnly => AccountId is null;

    internal static bool HasCommand(string[] args) => args.Any(arg => arg is RunOption or ReviewOption);

    internal static GenshinRewardAcceptance? Parse(string[] args, bool candidateBuild, bool hasMarker, string localAppData)
    {
        if (!candidateBuild && !hasMarker && !HasCommand(args)) return null;
        if (!candidateBuild || !hasMarker)
            throw new InvalidOperationException("原神领奖验收仅在带有专用标记的合流验收构建中可用。");
        if (!Path.IsPathFullyQualified(localAppData))
            throw new InvalidOperationException("无法确认 Windows 本机数据目录。");
        if (args is [ReviewOption, var reviewAttempt] && TryId(reviewAttempt, out var prior))
            return new(prior, null, 0, localAppData);
        if (args is [RunOption, var attempt, var account, var revision] && TryId(attempt, out var runId) &&
            TryId(account, out var accountId) && long.TryParse(revision, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            return new(runId, accountId, value, localAppData);
        throw new ArgumentException("请提供唯一的验收编号、一个原神账号 GUID 及账号 revision，或用 --genshin-reward-review 查看既有验收。");
    }

    private static bool TryId(string value, out Guid id) => Guid.TryParseExact(value, "D", out id) && id != Guid.Empty;

    internal async Task<MultiAccountOutcome> RunAsync(LocalDatabase database, string betterGiRoot, string archivePath,
        string profileDirectory, GenshinDailyVerification verify, Action<BatchAccountProgress>? progress, CancellationToken ct)
    {
        if (AccountId is not { } id || !string.Equals(ProfileDirectory, profileDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("原神领奖验收需要本次请求对应的隔离合流档案。");
        var accounts = await database.LoadAccountsAsync(GameId.Genshin, ct).ConfigureAwait(false);
        var target = accounts.SingleOrDefault(account => account.Id == id);
        if (target is not { Enabled: true, LoginState: "recognized" } || target.Revision != Revision)
            throw new InvalidOperationException("原神验收账号缺失、未识别、已停用或 revision 已变化。");
        ct.ThrowIfCancellationRequested();
        return await verify(database, [target], betterGiRoot, archivePath, profileDirectory,
            rewardOnly: true, progress, ct).ConfigureAwait(false);
    }
}
