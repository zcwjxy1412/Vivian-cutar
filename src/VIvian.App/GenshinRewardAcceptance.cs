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

internal sealed class GenshinRewardActivation(TimeProvider clock)
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private readonly long _started = clock.GetTimestamp();
    private bool _rendered;
    private bool _finished;

    private bool Expired => clock.GetElapsedTime(_started) >= Timeout;

    internal bool TryMarkRendered()
    {
        if (_rendered || _finished || Expired) return false;
        _rendered = true;
        return true;
    }

    internal bool TryStart(bool isActive) => _rendered && isActive && !Expired && StopWaiting();

    internal bool StopWaiting()
    {
        if (_finished) return false;
        _finished = true;
        return true;
    }
}

internal sealed class GenshinRewardAcceptance
{
    private const string RunOption = "--genshin-reward-acceptance";
    private const string DailyOption = "--genshin-daily-acceptance";
    private const string ReviewOption = "--genshin-reward-review";
    internal const string Marker = "VIvian-GenshinRewardAcceptance.marker";
    internal static readonly TimeSpan RunLimit = TimeSpan.FromMinutes(10);
    internal static bool IsCandidateBuild =>
#if VIVIAN_GENSHIN_REWARD_ACCEPTANCE
        true;
#else
        false;
#endif

    internal GenshinRewardAcceptance(Guid attempt, Guid? account, long revision, string profileRoot, bool installedDaily = false)
    {
        Attempt = attempt;
        AccountId = account;
        Revision = revision;
        ProfileDirectory = installedDaily ? profileRoot : Path.Combine(profileRoot, attempt.ToString("N"));
        RewardOnly = !installedDaily;
    }

    internal Guid Attempt { get; }
    internal Guid? AccountId { get; }
    private long Revision { get; }
    internal string ProfileDirectory { get; }
    internal bool ReviewOnly => AccountId is null;
    internal bool RewardOnly { get; private init; } = true;
    internal int ExpectedAccountCount { get; private set; } = 1;
    internal string WorkspaceScope =>
        $"隔离验收 · {(ReviewOnly ? "查阅与恢复" : RewardOnly ? "原神单账号领奖 · 0 树脂" : "原神已保存日常与自动切号")}\n验收编号 {Attempt:D}";

    internal static bool HasCommand(string[] args) => args.Any(arg => arg is RunOption or DailyOption or ReviewOption);

    internal static GenshinRewardAcceptance? Parse(string[] args, bool candidateBuild, bool hasMarker, string localAppData)
    {
        if (!candidateBuild && !hasMarker && !HasCommand(args)) return null;
        if (!candidateBuild || !hasMarker)
            throw new InvalidOperationException("原神领奖验收仅在带有专用标记的合流验收构建中可用。");
        if (!Path.IsPathFullyQualified(localAppData))
            throw new InvalidOperationException("无法确认 Windows 本机数据目录。");
        var profileRoot = Path.Combine(localAppData, "VIvian-M6-GenshinAcceptance");
        if (args is [ReviewOption, var reviewAttempt] && TryId(reviewAttempt, out var prior))
            return new(prior, null, 0, profileRoot);
        if (args is [RunOption or DailyOption, var attempt, var account, var revision] && TryId(attempt, out var runId) &&
            TryId(account, out var accountId) && long.TryParse(revision, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            return new(runId, accountId, value, profileRoot) { RewardOnly = args[0] == RunOption };
        throw new ArgumentException("请提供唯一的验收编号、一个原神账号 GUID 及账号 revision，或用 --genshin-reward-review 查看既有验收。");
    }

    private static bool TryId(string value, out Guid id) => Guid.TryParseExact(value, "D", out id) && id != Guid.Empty;

    internal static int? AutoExitCode(MultiAccountOutcome? outcome, bool passed, bool pendingRecovery,
        Func<bool> confirmDesktopClean) =>
        outcome is { GameCleanupConfirmed: true, HostCleanupConfirmed: true } &&
            !pendingRecovery && confirmDesktopClean() ? (passed ? 0 : 1) : null;

    internal async Task<MultiAccountOutcome> RunAsync(LocalDatabase database, string betterGiRoot, string archivePath,
        string profileDirectory, GenshinDailyVerification verify, Action<BatchAccountProgress>? progress, CancellationToken ct,
        string? recoveryProfileDirectory = null, Action? beforeVerify = null)
    {
        if (AccountId is not { } id || !string.Equals(ProfileDirectory, profileDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("原神领奖验收需要本次请求对应的隔离合流档案。");
        var accounts = await database.LoadAccountsAsync(GameId.Genshin, ct).ConfigureAwait(false);
        var target = accounts.SingleOrDefault(account => account.Id == id);
        if (target is not { Enabled: true, LoginState: "recognized" } || target.Revision != Revision)
            throw new InvalidOperationException("原神验收账号缺失、未识别、已停用或 revision 已变化。");
        var selected = RewardOnly ? [target] : accounts.Where(a => a.Enabled && a.AutoRun)
            .OrderBy(a => a.Order).ThenBy(a => a.Id).ToArray();
        if (selected.Length == 0 || selected[0].Id != id || selected.Any(a => a.LoginState != "recognized"))
            throw new InvalidOperationException("原神日常验收的勾选顺序或登录档案已变化。");
        ExpectedAccountCount = selected.Length;
        ct.ThrowIfCancellationRequested();
        beforeVerify?.Invoke();
        return await verify(database, selected, betterGiRoot, archivePath, recoveryProfileDirectory ?? profileDirectory,
            rewardOnly: RewardOnly, progress, ct).ConfigureAwait(false);
    }
}
