using VIvian.Core;
using VIvian.Core.Accounts;

namespace VIvian.Infrastructure.Accounts;

public sealed record BatchTaskResult(Guid? RunId, bool Success, bool CleanupConfirmed, bool ConfigurationRestored,
    string Detail);

public delegate Task<BatchTaskResult> BatchTaskRunner(GameAccount account,
    IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken);

public sealed record MultiAccountOutcome(Guid BatchId, bool Completed, int Succeeded, int Failed,
    string Detail);

/// <summary>
/// Serialises real account work: recognise the login screen, select the target account, start one upstream task,
/// wait for cleanup, then return the game to the login screen before the next account is touched.
/// This type never clicks by itself; the input host and login driver own native input.
/// </summary>
public sealed class MultiAccountOrchestrator
{
    private readonly LocalDatabase _database;
    private readonly ILoginSwitchDriver _driver;

    public MultiAccountOrchestrator(LocalDatabase database, ILoginSwitchDriver driver)
    {
        _database = database;
        _driver = driver;
    }

    public async Task<MultiAccountOutcome> ExecuteAsync(GameId game, IReadOnlyList<GameAccount> accounts,
        AccountConfigurationMode mode, Func<GameAccount, CancellationToken, Task<IReadOnlyDictionary<string, string>>> settings,
        BatchTaskRunner runner, Action<BatchAccountProgress>? progress, CancellationToken cancellationToken)
    {
        if (accounts.Count == 0) throw new ArgumentException("至少选择一个账号。", nameof(accounts));
        if (accounts.Select(a => a.Id).Distinct().Count() != accounts.Count)
            throw new ArgumentException("账号列表包含重复项。", nameof(accounts));
        if (accounts.Any(a => a.Game != game)) throw new ArgumentException("账号与游戏不匹配。", nameof(accounts));

        var policy = new SwitchPolicy();
        var batch = await _database.BeginBatchAsync(game, mode == AccountConfigurationMode.Unified
            ? "unified" : "independent", cancellationToken);
        var firstSettings = await settings(accounts[0], cancellationToken);
        if (mode == AccountConfigurationMode.Unified && firstSettings.Count == 0)
            throw new InvalidOperationException("第一个账号没有已保存的配置快照，无法作为统一配置。");
        var profiles = (await _database.LoadLoginProfilesAsync(game, cancellationToken))
            .ToDictionary(p => p.AccountId);

        var succeeded = 0;
        var failed = 0;
        var aborted = false;
        var cancelled = false;
        // Start from a known login screen. This also handles a game left in-world by the user or a previous
        // interrupted batch; the account currently logged in is never assumed to be the first target.
        var prepared = await _driver.RestartToLoginAsync(policy, cancellationToken);
        if (!prepared.Ok)
        {
            await RecordAsync(batch, accounts[0], "login_screen_failed", prepared.Detail, cancellationToken);
            batch = batch with { UpdatedAt = DateTimeOffset.UtcNow, CurrentAccountId = null, State = "failed",
                Detail = "无法把游戏启动到登录界面：" + prepared.Detail };
            await _database.SaveBatchAsync(batch, cancellationToken);
            return new(batch.Id, false, 0, 0, batch.Detail);
        }
        try
        {
        for (var index = 0; index < accounts.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var account = accounts[index];
            var resolved = mode == AccountConfigurationMode.Unified
                ? BatchConfigurationResolver.Resolve(firstSettings, firstSettings, AccountConfigurationMode.Unified)
                : BatchConfigurationResolver.Resolve(firstSettings,
                    await settings(account, cancellationToken), AccountConfigurationMode.Independent);

            if (!profiles.TryGetValue(account.Id, out var profile))
            {
                failed++;
                await RecordAsync(batch, account, "login_profile_missing",
                    "此账号尚未绑定登录界面上的已保存账号；请先扫描登录账号。", cancellationToken);
                progress?.Invoke(P(account, BatchRunState.Failed, "尚未绑定登录界面"));
                continue;
            }

            batch = await StageAsync(batch, account, BatchRunState.Waiting, $"{index + 1}/{accounts.Count}：准备账号",
                progress, cancellationToken);
            var ensured = await _driver.EnsureLoginAsync(policy, cancellationToken);
            if (!ensured.Ok)
            {
                failed++;
                await RecordAsync(batch, account, "login_screen_failed", ensured.Detail, cancellationToken);
                progress?.Invoke(P(account, BatchRunState.Failed, ensured.Detail));
                if (!await TryReturnToLoginAsync(policy, progress, account, cancellationToken)) aborted = true;
                if (aborted) break;
                continue;
            }

            batch = await StageAsync(batch, account, BatchRunState.SelectingAccount, "正在选择账号", progress, cancellationToken);
            var target = new LoginTarget(profile.NormalizedPhone, profile.MaskedPhone, profile.RowOrder,
                profile.ObservedUid);
            var switched = await _driver.SwitchAsync(target, policy, cancellationToken);
            if (!switched.Ok)
            {
                failed++;
                var reauth = switched.Stage == LoginSwitchStage.WaitingForLogin &&
                    switched.Detail.Contains("授权", StringComparison.Ordinal);
                await RecordAsync(batch, account, reauth ? "reauth_required" : "switch_failed", switched.Detail,
                    cancellationToken);
                await _database.RecordAccountSwitchAsync(index == 0 ? null : accounts[index - 1].Id, account.Id,
                    reauth ? "reauth_required" : "switch_failed", switched.Detail, cancellationToken);
                progress?.Invoke(P(account, reauth ? BatchRunState.ReauthorizationRequired : BatchRunState.Failed, switched.Detail));
                if (!await TryReturnToLoginAsync(policy, progress, account, cancellationToken)) { aborted = true; break; }
                continue;
            }

            if (!string.IsNullOrWhiteSpace(switched.Uid))
                await _database.SaveObservedUidAsync(account.Id, switched.Uid, cancellationToken);
            await _database.RecordAccountSwitchAsync(index == 0 ? null : accounts[index - 1].Id, account.Id,
                "logged_in", "已通过登录界面进入游戏。", cancellationToken);

            batch = await StageAsync(batch, account, BatchRunState.LoadingConfiguration,
                $"已登录{(string.IsNullOrWhiteSpace(switched.Uid) ? "" : "（UID " + switched.Uid + "）")}；加载配置",
                progress, cancellationToken);
            batch = await StageAsync(batch, account, BatchRunState.RunningTasks, "正在执行星铁日常", progress, cancellationToken);
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
                break;
            }

            batch = await StageAsync(batch, account, BatchRunState.StoppingTasks, "正在确认清理并返回登录界面",
                progress, cancellationToken);
            if (!await TryReturnToLoginAsync(policy, progress, account, cancellationToken)) { aborted = true; break; }
        }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            try { await _driver.StopGameAsync(CancellationToken.None); } catch { /* user stop must still finish */ }
        }

        var completed = !aborted && !cancelled;
        batch = batch with
        {
            UpdatedAt = DateTimeOffset.UtcNow, CurrentAccountId = null,
            State = completed ? "finished" : "stopped",
            Detail = $"完成 {succeeded}，失败 {failed}。" +
                     (cancelled ? " 用户停止了批次。" : aborted ? " 批次因资源清理或登录状态不明确而停止。" : "")
        };
        // A user stop has already cancelled this token; the final bookkeeping must still be written.
        await _database.SaveBatchAsync(batch, CancellationToken.None);
        return new(batch.Id, completed, succeeded, failed, batch.Detail);
    }

    private async Task<bool> TryReturnToLoginAsync(SwitchPolicy policy, Action<BatchAccountProgress>? progress,
        GameAccount account, CancellationToken cancellationToken)
    {
        progress?.Invoke(P(account, BatchRunState.SwitchingAccount, "正在返回登录界面"));
        var returned = await _driver.RestartToLoginAsync(policy, cancellationToken);
        if (returned.Ok) return true;
        progress?.Invoke(P(account, BatchRunState.Failed, "无法确认已返回登录界面：" + returned.Detail));
        return false;
    }

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
