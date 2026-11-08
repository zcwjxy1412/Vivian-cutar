using VIvian.Core;
using VIvian.Core.Accounts;

namespace VIvian.Infrastructure.Accounts;

public sealed record BatchTaskResult(Guid? RunId, bool Success, bool CleanupConfirmed, bool ConfigurationRestored,
    string Detail);

public delegate Task<BatchTaskResult> BatchTaskRunner(GameAccount account,
    IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken);

/// <param name="HostLaunches">How many times the elevated host was started (= UAC prompts) in this batch.</param>
/// <param name="HostCleanupConfirmed">False when the elevated host exit could not be proven at batch end.</param>
public sealed record MultiAccountOutcome(Guid BatchId, bool Completed, int Succeeded, int Failed,
    string Detail, int HostLaunches = 0, bool HostCleanupConfirmed = true);

/// <summary>
/// Serialises real account work: reach the saved-account list, select and verify the target account, start one
/// upstream task, wait for cleanup and configuration restore, then return to the account list before the next account
/// is touched. The batch stops — it never continues on an uncertain account — when identity cannot be proven, the user
/// must complete authentication, the screen does not react, cleanup is unconfirmed, or the elevated host is refused or
/// lost. This type never clicks by itself; the login driver and input host own native input.
/// </summary>
public sealed class MultiAccountOrchestrator
{
    private readonly LocalDatabase _database;
    private readonly ILoginSwitchDriver _driver;
    private readonly IInputHostSession? _host;
    private readonly SwitchPolicy _policy;

    public MultiAccountOrchestrator(LocalDatabase database, ILoginSwitchDriver driver)
        : this(database, driver, null)
    {
    }

    public MultiAccountOrchestrator(LocalDatabase database, ILoginSwitchDriver driver, IInputHostSession? host,
        SwitchPolicy? policy = null)
    {
        _database = database;
        _driver = driver;
        _host = host;
        _policy = policy ?? new SwitchPolicy();
    }

    public async Task<MultiAccountOutcome> ExecuteAsync(GameId game, IReadOnlyList<GameAccount> accounts,
        AccountConfigurationMode mode, Func<GameAccount, CancellationToken, Task<IReadOnlyDictionary<string, string>>> settings,
        BatchTaskRunner runner, Action<BatchAccountProgress>? progress, CancellationToken cancellationToken)
    {
        if (accounts.Count == 0) throw new ArgumentException("至少选择一个账号。", nameof(accounts));
        if (accounts.Select(a => a.Id).Distinct().Count() != accounts.Count)
            throw new ArgumentException("账号列表包含重复项。", nameof(accounts));
        if (accounts.Any(a => a.Game != game)) throw new ArgumentException("账号与游戏不匹配。", nameof(accounts));

        var policy = _policy;
        var batch = await _database.BeginBatchAsync(game, mode == AccountConfigurationMode.Unified
            ? "unified" : "independent", cancellationToken);
        _host?.BeginBatch();
        var succeeded = 0;
        var failed = 0;
        var aborted = false;
        var cancelled = false;
        string? stopReason = null;
        GameAccount? previous = null;
        string? previousUid = null;
        string? previousMasked = null;
        try
        {
            var firstSettings = await settings(accounts[0], cancellationToken);
            if (mode == AccountConfigurationMode.Unified && firstSettings.Count == 0)
                throw new InvalidOperationException("第一个账号没有已保存的配置快照，无法作为统一配置。");
            var profiles = (await _database.LoadLoginProfilesAsync(game, cancellationToken)).ToDictionary(p => p.AccountId);
            var snapshots = new Dictionary<Guid, IReadOnlyDictionary<string, string>>();
            foreach (var account in accounts)
                snapshots[account.Id] = BatchConfigurationResolver.Resolve(firstSettings,
                    mode == AccountConfigurationMode.Unified ? firstSettings : await settings(account, cancellationToken), mode);
            var identities = accounts.Where(a => profiles.ContainsKey(a.Id)).Select(a => profiles[a.Id].NormalizedPhone).ToArray();
            if (identities.Any(string.IsNullOrWhiteSpace) || identities.Distinct(StringComparer.Ordinal).Count() != identities.Length)
                throw new InvalidOperationException("执行队列包含无效或重复的登录标识，未启动游戏。");
            // Start from the saved-account list. A game left in-world by the user or an interrupted batch is restarted
            // and logged out; the account currently logged in is never assumed to be the first target.
            var prepared = await _driver.RestartToLoginAsync(policy, cancellationToken);
            if (!prepared.Ok)
            {
                await RecordAsync(batch, accounts[0], prepared.NeedsUser ? "needs_user" : "login_screen_failed",
                    prepared.Detail, cancellationToken);
                return await FinishAsync(batch with { State = "failed",
                    Detail = "无法把游戏带到登录界面（已保存账号选择）：" + prepared.Detail }, false, 0, 0, cancellationToken);
            }

            for (var index = 0; index < accounts.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var account = accounts[index];
                var resolved = snapshots[account.Id];

                if (!profiles.TryGetValue(account.Id, out var profile))
                {
                    failed++;
                    await RecordAsync(batch, account, "login_profile_missing",
                        "此账号尚未绑定登录界面上的已保存账号；请先扫描登录账号。", cancellationToken);
                    progress?.Invoke(P(account, BatchRunState.Failed, "尚未绑定登录界面"));
                    continue;
                }

                batch = await StageAsync(batch, account, BatchRunState.RecognizingLogin,
                    $"{index + 1}/{accounts.Count}：确认账号选择界面", progress, cancellationToken);
                var ensured = await _driver.EnsureLoginAsync(policy, cancellationToken);
                if (!ensured.Ok)
                {
                    failed++;
                    await RecordAsync(batch, account, ensured.NeedsUser ? "needs_user" : "login_screen_failed",
                        ensured.Detail, cancellationToken);
                    progress?.Invoke(P(account, ensured.NeedsUser ? BatchRunState.ReauthorizationRequired
                        : BatchRunState.Failed, ensured.Detail));
                    aborted = true;
                    stopReason = ensured.Detail;
                    break;
                }

                batch = await StageAsync(batch, account, BatchRunState.SelectingAccount, "正在选择并核对账号",
                    progress, cancellationToken);
                var target = new LoginTarget(profile.NormalizedPhone, profile.MaskedPhone, profile.RowOrder,
                    profile.ObservedUid, previousMasked, previousUid);
                var switched = await _driver.SwitchAsync(target, policy, cancellationToken);
                var evidenceJson = switched.Evidence?.ToJson() ?? "";
                if (!switched.Ok)
                {
                    failed++;
                    var state = switched.NeedsUser ? "needs_user" : switched.Fatal ? "identity_uncertain" : "switch_failed";
                    await RecordAsync(batch, account, state, switched.Detail, cancellationToken);
                    await _database.RecordAccountSwitchAsync(previous?.Id, account.Id, state,
                        Combine(switched.Detail, evidenceJson), cancellationToken);
                    progress?.Invoke(P(account, switched.NeedsUser ? BatchRunState.ReauthorizationRequired
                        : BatchRunState.Failed, switched.Detail));
                    if (switched.Fatal)
                    {
                        aborted = true;
                        stopReason = switched.Detail;
                        break;
                    }
                    // Not fatal: nothing was entered, the game is still on the account list.
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(switched.Uid))
                    await _database.SaveObservedUidAsync(account.Id, switched.Uid, cancellationToken);
                await _database.RecordAccountSwitchAsync(previous?.Id, account.Id, "logged_in",
                    Combine(switched.Detail, evidenceJson), cancellationToken);

                batch = await StageAsync(batch, account, BatchRunState.LoadingConfiguration,
                    switched.Detail + " 加载配置", progress, cancellationToken);
                batch = await StageAsync(batch, account, BatchRunState.RunningTasks, "正在执行星铁日常", progress,
                    cancellationToken);
                BatchTaskResult result;
                try
                {
                    result = await runner(account, resolved, cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failed++;
                    await RecordAsync(batch, account, "failed", ex.Message, cancellationToken);
                    progress?.Invoke(P(account, BatchRunState.Failed, ex.Message));
                    aborted = true;
                    stopReason = "任务异常：" + ex.Message;
                    break;
                }

                if (result.Success && result.CleanupConfirmed && result.ConfigurationRestored)
                {
                    succeeded++;
                    await RecordAsync(batch, account, "success", result.Detail, cancellationToken, result.RunId);
                    progress?.Invoke(P(account, BatchRunState.Completed, result.Detail));
                }
                else
                {
                    failed++;
                    await RecordAsync(batch, account, "failed", result.Detail, cancellationToken, result.RunId);
                    progress?.Invoke(P(account, BatchRunState.Failed, result.Detail));
                }

                if (!result.CleanupConfirmed || !result.ConfigurationRestored)
                {
                    aborted = true;
                    stopReason = !result.CleanupConfirmed
                        ? "上一账号的任务进程清理未确认，已阻断下一个账号。"
                        : "上一账号的临时配置未恢复，已阻断下一个账号。";
                    break;
                }

                previous = account;
                previousMasked = profile.MaskedPhone;
                previousUid = string.IsNullOrWhiteSpace(switched.Uid) ? null : switched.Uid;

                if (index + 1 < accounts.Count)
                {
                    batch = await StageAsync(batch, account, BatchRunState.SwitchingAccount,
                        "正在退出当前账号并返回账号选择界面", progress, cancellationToken);
                    var returned = await _driver.RestartToLoginAsync(policy, cancellationToken);
                    if (!returned.Ok)
                    {
                        progress?.Invoke(P(account, BatchRunState.Failed, "无法确认已返回账号选择界面：" + returned.Detail));
                        aborted = true;
                        stopReason = "无法返回账号选择界面：" + returned.Detail;
                        break;
                    }
                }
            }
            if (!aborted)
            {
                var stopped = await _driver.StopGameAsync(cancellationToken);
                if (!stopped.Ok) { aborted = true; stopReason = "批次末尾游戏清理未确认：" + stopped.Detail; }
            }
        }
        catch (InputHostUnavailableException ex)
        {
            aborted = true;
            stopReason = ex.Message;
            var current = batch.CurrentAccountId is { } id ? accounts.FirstOrDefault(a => a.Id == id) ?? accounts[0] : accounts[0];
            await RecordAsync(batch, current, ex.Kind == InputHostFailureKind.Refused ? "uac_refused" : "input_host_unavailable",
                ex.Message, CancellationToken.None);
            progress?.Invoke(P(current, BatchRunState.ReauthorizationRequired, ex.Message));
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            try { await _driver.StopGameAsync(CancellationToken.None); } catch { /* user stop must still finish */ }
        }
        catch (Exception ex)
        {
            aborted = true;
            stopReason = "批次异常，未继续操作游戏：" + ex.Message;
            var current = accounts.FirstOrDefault(a => a.Id == batch.CurrentAccountId) ?? accounts[0];
            await RecordAsync(batch, current, "batch_exception", stopReason, CancellationToken.None);
        }

        var completed = !aborted && !cancelled;
        return await FinishAsync(batch with
        {
            State = completed ? "finished" : "stopped",
            Detail = $"完成 {succeeded}，失败 {failed}。" +
                     (cancelled ? " 用户停止了批次。" : aborted ? " 批次已停止：" + stopReason : "")
        }, completed, succeeded, failed, CancellationToken.None);
    }

    private async Task<MultiAccountOutcome> FinishAsync(AccountBatch batch, bool completed, int succeeded, int failed,
        CancellationToken cancellationToken)
    {
        var launches = _host?.LaunchCount ?? 0;
        var hostClean = true;
        if (_host is not null)
        {
            try { hostClean = await _host.EndBatchAsync(CancellationToken.None); }
            catch { hostClean = false; }
        }
        var detail = batch.Detail + $" 本批次管理员授权请求 {launches} 次。";
        if (!hostClean)
        {
            completed = false;
            detail += " 管理员输入宿主退出未能确认，请在任务管理器检查残留的 VIvian 进程。";
        }
        var final = batch with
        {
            UpdatedAt = DateTimeOffset.UtcNow, CurrentAccountId = null,
            State = batch.State == "finished" && !completed ? "stopped" : batch.State,
            Detail = detail,
        };
        // A user stop has already cancelled the batch token; the final bookkeeping must still be written.
        await _database.SaveBatchAsync(final, cancellationToken);
        return new(final.Id, completed, succeeded, failed, final.Detail, launches, hostClean);
    }

    private static string Combine(string detail, string evidence) =>
        string.IsNullOrEmpty(evidence) ? detail : detail + "\n证据：" + evidence;

    private static BatchAccountProgress P(GameAccount account, BatchRunState state, string detail) =>
        new(account.Id, account.Name, state, detail, 1, DateTimeOffset.UtcNow);

    private async Task<AccountBatch> StageAsync(AccountBatch batch, GameAccount account, BatchRunState state,
        string detail, Action<BatchAccountProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Invoke(P(account, state, detail));
        var updated = batch with
        {
            UpdatedAt = DateTimeOffset.UtcNow, CurrentAccountId = account.Id,
            Detail = $"{account.Name}：{BatchProgressText.Describe(state)}"
        };
        await _database.SaveBatchAsync(updated, cancellationToken);
        return updated;
    }

    private async Task RecordAsync(AccountBatch batch, GameAccount account, string result, string detail,
        CancellationToken cancellationToken, Guid? runId = null)
    {
        await _database.RecordAccountResultAsync(batch.Id, account.Id, runId, result, detail, cancellationToken);
    }
}
