using VIvian.Core;
using VIvian.Core.Accounts;
using VIvian.Infrastructure.Accounts;

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
        var bound = loginProfiles.Where(profile => selected.Any(account => account.Id == profile.AccountId)).ToArray();
        if (bound.Length != selected.Length || bound.Any(profile => string.IsNullOrWhiteSpace(profile.NormalizedPhone))
            || bound.Select(profile => profile.NormalizedPhone).Distinct(StringComparer.Ordinal).Count() != selected.Length)
            throw new InvalidOperationException("原神账号须先绑定各自唯一的已保存登录标识。");

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
}
