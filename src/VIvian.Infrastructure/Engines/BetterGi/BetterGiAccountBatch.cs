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
    public static async Task<MultiAccountOutcome> ExecuteSavedDailyAsync(LocalDatabase database,
        IReadOnlyList<GameAccount> accounts, string betterGiRoot, string archivePath, string profileDirectory,
        Action<BatchAccountProgress>? progress, CancellationToken ct)
    {
        if (!RealExecutionPolicy.IsM5DailyTest || !WindowsProcessSupervisor.IsCurrentProcessElevated())
            throw new InvalidOperationException("此版本或当前 Windows 授权无法运行原神日常。");
        var (selected, profiles) = await LoadSelectionAsync(database, accounts, ct).ConfigureAwait(false);
        var game = await BetterGiConfiguration.FindClientExecutableAsync(betterGiRoot, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("BetterGI 中没有可用的原神国服安装路径。");

        var plans = new Dictionary<Guid, BetterGiPlanFiles>();
        foreach (var account in selected)
        {
            if (account.Uid is { Length: > 0 } savedUid && profiles[account.Id].ObservedUid is { Length: > 0 } observedUid &&
                savedUid != observedUid)
                throw new InvalidOperationException($"「{account.Name}」的档案 UID 和上次入场 UID 不一致，批次未启动。");
            plans.Add(account.Id, await ReadSavedDailyAsync(betterGiRoot, account, game, ct).ConfigureAwait(false));
        }

        using var lease = DesktopOperationLease.Acquire(RecoveryOwner(profileDirectory));
        var stages = new Dictionary<Guid, string>();
        foreach (var account in selected)
        {
            var stage = await BetterGiAcceptanceStage.PrepareAsync(archivePath, account.Id, game, plans[account.Id], ct)
                .ConfigureAwait(false);
            stages.Add(account.Id, stage.StagePath);
        }

        return await GenshinDesktopLoginDriver.WithClientAsync(betterGiRoot, game, lease, (driver, token) =>
        {
            var orchestrator = new MultiAccountOrchestrator(database, driver);
            return orchestrator.ExecuteAsync(GameId.Genshin, selected, AccountConfigurationMode.Independent,
                (_, _) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()),
                async (account, _, taskToken) =>
                {
                    var plan = plans[account.Id];
                    if (await ReadSavedDailyAsync(betterGiRoot, account, game, taskToken).ConfigureAwait(false) != plan)
                        throw new InvalidOperationException("BetterGI 源档案在批次准备后发生变化，任务未启动。");
                    var profile = profiles[account.Id];
                    var expectedUid = string.IsNullOrWhiteSpace(account.Uid) ? profile.ObservedUid : account.Uid;
                    var result = await BetterGiDailyAcceptance.RunAsync(stages[account.Id], account.Id, plan, lease,
                        recheckToken => driver.ConfirmWorldAsync(account.Id, profile.NormalizedPhone, expectedUid, recheckToken),
                        taskToken).ConfigureAwait(false);
                    return await RecordDailyAsync(database, result).ConfigureAwait(false);
                }, progress, token);
        }, ct).ConfigureAwait(false);
    }

    public static Task<Guid?> RecoverSavedDailyAsync(string profileDirectory, CancellationToken ct) =>
        BetterGiDailyAcceptance.RecoverPendingForOwnerAsync(RecoveryOwner(profileDirectory), ct);

    private static string RecoveryOwner(string profileDirectory) => Path.Combine(profileDirectory, "BetterGi-日常");

    internal static async Task<BetterGiPlanFiles> ReadSavedDailyAsync(string betterGiRoot, GameAccount account,
        string game, CancellationToken ct)
    {
        try
        {
            var source = await BetterGiConfiguration.InspectSavedDailyAsync(betterGiRoot,
                BetterGiAccountPlan.SourceProfileName(account.Id), ct).ConfigureAwait(false);
            if (!source.EnabledTasks.SequenceEqual([BetterGiMarkers.DailyRewardTask]))
                throw new InvalidOperationException("源档案的执行顺序或任务标识不包含有效的领取每日奖励。");
            return BetterGiAccountPlans.RenderDailyAcceptance(account.Id, source, game);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"「{account.Name}」{ex.Message}", ex);
        }
    }

    private static async Task<BatchTaskResult> RecordDailyAsync(LocalDatabase database, BetterGiDailyAcceptanceResult result)
    {
        var cleanup = result.Restored ? result.Cleanup : CleanupState.Unconfirmed;
        var clean = cleanup is CleanupState.Confirmed or CleanupState.NotRequired;
        var outcome = clean && result.Cancelled ? RunOutcome.Cancelled : result.Outcome;
        var reason = !clean ? ReasonCodes.CleanupUnconfirmed
            : result.Cancelled ? ReasonCodes.UserStop
            : result.TimedOut ? ReasonCodes.TaskTimeout
            : result.StartError ?? result.Verdict?.ReasonCode ?? ReasonCodes.NoCompletionEvidence;
        var detail = outcome switch
        {
            RunOutcome.Success => "原神日常已完成，BetterGI 已清理并恢复。",
            RunOutcome.Cancelled => "已停止原神日常，BetterGI 已清理并恢复。",
            RunOutcome.Failed => "原神日常失败（" + reason + "）。",
            _ when !clean => "BetterGI 清理或配置恢复未确认，已停止批次。",
            _ => "原神日常结果未确认（" + reason + "），已停止批次。",
        };
        if (outcome == RunOutcome.Indeterminate && result.EvidenceObserved)
            detail += " 已保留与本次引擎进程匹配的日志证据。";
        var at = DateTimeOffset.UtcNow;
        await database.PublishAsync(new(result.RunId, result.RunId, GameId.Genshin, BetterGiContract.PinnedTag,
            IsSimulation: false, RunPhase.Finished, outcome, cleanup, reason, at), CancellationToken.None).ConfigureAwait(false);
        await database.AppendEventsAsync([new(result.RunId, at, RunEventKind.Progress, detail + " 验收报告 " + result.ReportPath)],
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
