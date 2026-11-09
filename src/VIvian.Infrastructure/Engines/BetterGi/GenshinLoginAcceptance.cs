using VIvian.Core;
using VIvian.Core.Accounts;
using VIvian.Infrastructure.Accounts;

namespace VIvian.Infrastructure.Engines.BetterGi;

public sealed record GenshinLoginCheck(Guid AccountId, bool Success, string Detail);

/// <summary>Exercises saved-account switching without starting BetterGI or running daily tasks.</summary>
public static class GenshinLoginAcceptance
{
    public static async Task<IReadOnlyList<GenshinLoginCheck>> VerifyAsync(LocalDatabase database,
        IReadOnlyList<Guid> accountIds, string betterGiRoot, string gameExecutable, string profileDirectory,
        CancellationToken ct)
    {
        var selected = accountIds.ToArray();
        if (selected.Length is < 1 or > 2 || selected.Distinct().Count() != selected.Length)
            throw new ArgumentException("请选择一个或两个不同的原神账号。", nameof(accountIds));
        var enabled = (await database.LoadAccountsAsync(GameId.Genshin, ct).ConfigureAwait(false))
            .Where(account => account.Enabled).Select(account => account.Id).ToHashSet();
        var profiles = (await database.LoadLoginProfilesAsync(GameId.Genshin, ct).ConfigureAwait(false))
            .GroupBy(profile => profile.NormalizedPhone, StringComparer.Ordinal).Where(group => group.Count() == 1)
            .Select(group => group.Single()).ToDictionary(profile => profile.AccountId);
        if (selected.Any(id => !enabled.Contains(id) || !profiles.TryGetValue(id, out var profile) ||
            GenshinLoginScreen.NormalizeExactMask(profile.MaskedPhone) != profile.NormalizedPhone))
            throw new InvalidOperationException("所选原神档案须已启用，并各自绑定唯一的脱敏登录标识。");

        return await GenshinDesktopLoginDriver.WithForegroundClientAsync(betterGiRoot, gameExecutable, profileDirectory,
            async (driver, token) =>
            {
                var results = new List<GenshinLoginCheck>(selected.Length);
                var policy = new SwitchPolicy();
                string? previousMasked = null, previousUid = null;
                for (var i = 0; i < selected.Length; i++)
                {
                    var id = selected[i];
                    var ready = i == 0 ? await driver.EnsureLoginAsync(policy, token).ConfigureAwait(false)
                        : await driver.RestartToLoginAsync(policy, token).ConfigureAwait(false);
                    if (!ready.Ok)
                    {
                        results.Add(new(id, false, ready.Detail));
                        break;
                    }
                    var profile = profiles[id];
                    var target = new LoginTarget(profile.NormalizedPhone, profile.MaskedPhone, profile.RowOrder,
                        profile.ObservedUid, previousMasked, previousUid);
                    var entered = await driver.SwitchAsync(target, policy, token).ConfigureAwait(false);
                    results.Add(new(id, entered.Ok, entered.Detail));
                    if (!entered.Ok) break;
                    if (entered.Uid is { } uid)
                        await database.SaveObservedUidAsync(id, uid, token).ConfigureAwait(false);
                    previousMasked = target.MaskedPhone;
                    previousUid = entered.Uid;
                }
                return (IReadOnlyList<GenshinLoginCheck>)results;
            }, ct).ConfigureAwait(false);
    }
}
