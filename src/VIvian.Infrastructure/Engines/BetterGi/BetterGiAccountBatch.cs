using VIvian.Core;
using VIvian.Core.Accounts;
using VIvian.Core.Engines;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Accounts;
using VIvian.Infrastructure.Processes;

namespace VIvian.Infrastructure.Engines.BetterGi;

/// <summary>The login driver must verify Genshin account identity and cleanup in the same session.</summary>
public interface IGenshinLoginSwitchDriver : ILoginSwitchDriver { }

/// <summary>Freezes each Genshin account's plan before the shared sequencer enters any login state.</summary>
public sealed class BetterGiAccountBatch(LocalDatabase database, IGenshinLoginSwitchDriver loginDriver, IInputHostSession? inputHost = null)
{
    public async Task<MultiAccountOutcome> ExecuteAsync(IReadOnlyList<GameAccount> accounts,
        Func<GameAccount, BetterGiAccountPlan, CancellationToken, Task<BatchTaskResult>> run,
        Action<BatchAccountProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        var (selected, _) = await LoadSelectionAsync(database, accounts, ct).ConfigureAwait(false);
        var plans = new Dictionary<Guid, BetterGiAccountPlan>();
        foreach (var account in selected)
        {
            var settings = await database.LoadAccountSettingsAsync(account.Id, ct).ConfigureAwait(false);
            var plan = BetterGiAccountPlans.Compile(settings.Values);
            if (plan.Tasks.Count == 0)
                throw new InvalidOperationException("所选账号中存在尚未启用任务的档案，批次未启动。");
            plans.Add(account.Id, plan);
        }

        var orchestrator = new MultiAccountOrchestrator(database, loginDriver, inputHost);
        return await orchestrator.ExecuteAsync(GameId.Genshin, selected, AccountConfigurationMode.Independent,
            (account, _) => Task.FromResult(plans[account.Id].Values),
            (account, _, token) => run(account, plans[account.Id], token), progress, ct).ConfigureAwait(false);
    }

    /// <summary>Runs the saved daily reward plan under one desktop lease and one verified game session at a time.</summary>
    public static Task<MultiAccountOutcome> ExecuteSavedDailyAsync(LocalDatabase database,
        IReadOnlyList<GameAccount> accounts, string betterGiRoot, string archivePath, string profileDirectory,
        Action<BatchAccountProgress>? progress, CancellationToken ct) =>
        ExecuteSavedAsync(database, accounts, betterGiRoot, archivePath, profileDirectory, progress, ct, acceptance: false, rewardOnly: false);

    /// <summary>Uses the same account pipeline with separately labelled, per-run acceptance evidence.</summary>
    public static Task<MultiAccountOutcome> VerifySavedDailyAsync(LocalDatabase database,
        IReadOnlyList<GameAccount> accounts, string betterGiRoot, string archivePath, string profileDirectory,
        bool rewardOnly, Action<BatchAccountProgress>? progress, CancellationToken ct) =>
        ExecuteSavedAsync(database, accounts, betterGiRoot, archivePath, profileDirectory, progress, ct, acceptance: true, rewardOnly);

    private static async Task<MultiAccountOutcome> ExecuteSavedAsync(LocalDatabase database,
        IReadOnlyList<GameAccount> accounts, string betterGiRoot, string archivePath, string profileDirectory,
        Action<BatchAccountProgress>? progress, CancellationToken ct, bool acceptance, bool rewardOnly)
    {
        if (!RealExecutionPolicy.IsM5DailyTest || !WindowsProcessSupervisor.IsCurrentProcessElevated())
            throw new InvalidOperationException("此版本或当前 Windows 授权无法运行原神日常。");
        var (selected, profiles) = await LoadSelectionAsync(database, accounts, ct).ConfigureAwait(false);
        var game = await BetterGiConfiguration.FindClientExecutableAsync(betterGiRoot, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("BetterGI 中没有可用的原神国服安装路径。");

        if (rewardOnly)
            throw new InvalidOperationException("请从正式日常入口执行用户保存的完整任务顺序。");
        await BetterGiNativeDaily.ValidateInstallationAsync(betterGiRoot, ct).ConfigureAwait(false);
        using var lease = DesktopOperationLease.Acquire(RecoveryOwner(profileDirectory));
        BetterGiDailyAcceptance.RemoveInactiveRecoveryJournals(lease, ct);
        var revisions = new Dictionary<Guid, string>();
        var parameters = new Dictionary<Guid, IReadOnlyDictionary<string, string>>();
        foreach (var account in selected)
        {
            if (account.Uid is { Length: > 0 } savedUid && profiles[account.Id].ObservedUid is { Length: > 0 } observedUid &&
                savedUid != observedUid)
                throw new InvalidOperationException($"「{account.Name}」的档案 UID 和上次入场 UID 不一致，批次未启动。");
            var source = BetterGiNativeDaily.Profile(betterGiRoot, account.Id);
            if (source.HasPending && source.Restore().State == RestoreState.Blocked)
                throw new InvalidOperationException($"「{account.Name}」的 BetterGI 结束后动作尚未恢复，请核对源档案。");
            var sourcePlan = await BetterGiConfiguration.InspectOneDragonAsync(betterGiRoot,
                BetterGiAccountPlan.SourceProfileName(account.Id), ct).ConfigureAwait(false);
            if (sourcePlan.EnabledTasks.Count == 0)
                throw new InvalidOperationException($"「{account.Name}」的 BetterGI 档案没有启用任务。");
            revisions.Add(account.Id, source.Read().Revision);
            parameters.Add(account.Id, (await database.LoadAccountSettingsAsync(account.Id, ct).ConfigureAwait(false)).Values);
        }

        ct.ThrowIfCancellationRequested();
        BetterGiDailyAcceptance.RequireLegacyLauncherClosed();
        return await GenshinDesktopLoginDriver.WithClientAsync(betterGiRoot, game, lease, stopOwnedClient: true, async (driver, token) =>
        {
            var orchestrator = new MultiAccountOrchestrator(database, driver);
            MultiAccountOutcome outcome;
            try
            {
                outcome = await orchestrator.ExecuteAsync(GameId.Genshin, selected, AccountConfigurationMode.Independent,
                    (_, _) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()),
                    async (account, _, taskToken) =>
                    {
                        var profile = profiles[account.Id];
                        var expectedUid = string.IsNullOrWhiteSpace(account.Uid) ? profile.ObservedUid : account.Uid;
                        var result = await BetterGiNativeDaily.RunAsync(betterGiRoot, account.Id, revisions[account.Id],
                            Path.Combine(profileDirectory, "BetterGi-日常", "reports"), lease,
                            recheckToken => driver.ConfirmWorldAsync(account.Id, profile.NormalizedPhone, expectedUid, recheckToken),
                            taskToken, parameters[account.Id]).ConfigureAwait(false);
                        return await RecordDailyAsync(database, result, acceptance).ConfigureAwait(false);
                    }, progress, token).ConfigureAwait(false);
            }
            catch
            {
                await driver.StopGameAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            if (outcome.Completed) return outcome;

            var cleanup = await driver.StopGameAsync(CancellationToken.None).ConfigureAwait(false);
            return outcome with { GameCleanupConfirmed = cleanup.Ok, Detail = outcome.Detail + " " + cleanup.Detail };
        }, ct).ConfigureAwait(false);
    }

    public static async Task<Guid?> RecoverSavedDailyAsync(string profileDirectory, CancellationToken ct) =>
        await BetterGiNativeDaily.RecoverAsync(RecoveryOwner(profileDirectory), ct).ConfigureAwait(false)
        ?? await BetterGiDailyAcceptance.RecoverPendingForOwnerAsync(RecoveryOwner(profileDirectory), ct).ConfigureAwait(false);

    public static async Task<Guid?> RecoverSavedDailyAsync(string profileDirectory, CancellationToken ct,
        Func<Guid, CancellationToken, Task> beforeRelease) =>
        await BetterGiNativeDaily.RecoverAsync(RecoveryOwner(profileDirectory), ct, beforeRelease).ConfigureAwait(false)
        ?? await BetterGiDailyAcceptance.RecoverPendingForOwnerAsync(RecoveryOwner(profileDirectory), ct, beforeRelease).ConfigureAwait(false);

    private static string RecoveryOwner(string profileDirectory) => Path.Combine(profileDirectory, "BetterGi-日常");

    internal static async Task<BetterGiPlanFiles> ReadSavedDailyAsync(string betterGiRoot, GameAccount account,
        string game, CancellationToken ct, bool allowStockCraft = false, bool rewardOnly = false)
    {
        try
        {
            var source = await BetterGiConfiguration.InspectSavedDailyAsync(betterGiRoot,
                BetterGiAccountPlan.SourceProfileName(account.Id), ct, allowStockCraft).ConfigureAwait(false);
            return rewardOnly ? BetterGiAccountPlans.RenderDailyAcceptance(account.Id, source, game)
                : BetterGiAccountPlans.RenderSavedRun(account.Id, source, game);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"「{account.Name}」{ex.Message}", ex);
        }
    }

    private static async Task<BatchTaskResult> RecordDailyAsync(LocalDatabase database, BetterGiDailyAcceptanceResult result, bool acceptance)
    {
        var cleanup = result.Restored ? result.Cleanup : CleanupState.Unconfirmed;
        var clean = cleanup is CleanupState.Confirmed or CleanupState.NotRequired;
        var scopedSuccess = result.ScopedCompletionConfirmed;
        var outcome = scopedSuccess ? RunOutcome.Success : clean && result.Cancelled ? RunOutcome.Cancelled : result.Outcome;
        var reason = !clean ? ReasonCodes.CleanupUnconfirmed
            : result.Cancelled ? ReasonCodes.UserStop
            : result.TimedOut ? ReasonCodes.TaskTimeout
            : scopedSuccess ? ReasonCodes.Completed
            : result.StartError ?? result.Verdict?.ReasonCode ?? ReasonCodes.NoCompletionEvidence;
        var detail = outcome switch
        {
            RunOutcome.Success when scopedSuccess => "原版 BetterGI 已报告本账号任务流程结束。具体任务结果以上游日志为准；引擎已退出，临时设置已恢复。",
            RunOutcome.Success => "原神日常已完成，BetterGI 已清理并恢复。",
            RunOutcome.Cancelled => "已停止原神日常，BetterGI 已清理并恢复。",
            RunOutcome.Failed => "原神日常失败（" + reason + "）。",
            _ when !clean => "BetterGI 清理或配置恢复未确认，已停止批次。",
            _ when result.DailyRewardClaimUnconfirmed => "BetterGI 结束了流程，但未识别到「今日奖励已领取」。已停止批次。",
            _ => "原神日常结果未确认（" + reason + "），已停止批次。",
        };
        if (outcome == RunOutcome.Indeterminate && result.EvidenceObserved)
            detail += " 已保留与本次引擎进程匹配的日志证据。";
        var at = DateTimeOffset.UtcNow;
        var rule = acceptance ? BetterGiContract.PinnedTag + "/acceptance" : BetterGiContract.PinnedTag;
        await database.PublishAsync(new(result.RunId, result.RunId, GameId.Genshin, rule,
            IsSimulation: false, RunPhase.Finished, outcome, cleanup, reason, at), CancellationToken.None).ConfigureAwait(false);
        await database.AppendEventsAsync([new(result.RunId, at, RunEventKind.Progress, detail + " 运行报告 " + result.ReportPath)],
            CancellationToken.None)
            .ConfigureAwait(false);
        return new(result.RunId, outcome == RunOutcome.Success, clean, result.Restored, detail, outcome);
    }

    private static async Task<(GameAccount[] Accounts, IReadOnlyDictionary<Guid, LocalDatabase.AccountLoginProfile> Profiles)>
        LoadSelectionAsync(LocalDatabase database, IReadOnlyList<GameAccount> accounts, CancellationToken ct)
    {
        var selected = accounts.ToArray();
        if (selected.Length == 0 || selected.Select(account => account.Id).Distinct().Count() != selected.Length
            || selected.Any(account => account.Game != GameId.Genshin || !account.Enabled))
            throw new ArgumentException("请选择不同的、已启用的原神账号。", nameof(accounts));

        var current = (await database.LoadAccountsAsync(GameId.Genshin, ct).ConfigureAwait(false))
            .ToDictionary(account => account.Id);
        if (selected.Any(account => !current.TryGetValue(account.Id, out var saved) || !saved.Enabled))
            throw new InvalidOperationException("选中的原神账号已停用或删除，批次未启动。");
        selected = selected.Select(account => current[account.Id]).ToArray();

        var loginProfiles = await database.LoadLoginProfilesAsync(GameId.Genshin, ct).ConfigureAwait(false);
        var unique = loginProfiles.GroupBy(profile => profile.NormalizedPhone, StringComparer.Ordinal)
            .Where(group => group.Count() == 1).Select(group => group.Single()).ToDictionary(profile => profile.AccountId);
        if (selected.Any(account => !unique.TryGetValue(account.Id, out var profile) ||
                GenshinLoginScreen.NormalizeExactMask(profile.MaskedPhone) != profile.NormalizedPhone ||
                string.IsNullOrWhiteSpace(profile.NormalizedPhone)))
            throw new InvalidOperationException("原神账号须先绑定各自唯一的已保存登录标识。");
        return (selected, unique);
    }
}
