using System.Text.Json;
using VIvian.Core;
using VIvian.Core.Engines;

namespace VIvian.Infrastructure.Engines.BetterGi;

/// <summary>Stores BetterGI plan parameters on the local VIvian account. Upstream configuration belongs to execution.</summary>
public sealed class GenshinAccountConfigurationProvider(LocalDatabase database) : IAccountConfigurationProvider
{
    public GameId Game => GameId.Genshin;

    private static readonly IReadOnlyDictionary<string, EngineParameterDefinition> Fields =
        BetterGiAccountPlans.Parameters.ToDictionary(field => field.Path, StringComparer.Ordinal);

    private sealed record Current(GameAccount Account, AccountSettingsSnapshot Settings, AccountConfigurationView View);

    public async Task<AccountConfigurationView> ReadAsync(AccountConfigurationTarget target, CancellationToken ct) =>
        (await LoadAsync(target, ct).ConfigureAwait(false)).View;

    public async Task<AccountConfigurationProposal> ProposeAsync(AccountConfigurationTarget target, string revision,
        IReadOnlyDictionary<string, string> changes, CancellationToken ct)
    {
        var current = await LoadAsync(target, ct).ConfigureAwait(false);
        return BuildProposal(current, revision, changes).Proposal;
    }

    public async Task<AccountConfigurationApplied> ApplyAsync(AccountConfigurationProposal proposal,
        Func<bool> stillAllowed, CancellationToken ct)
    {
        if (!proposal.Validation.Valid || !stillAllowed())
            throw new InvalidOperationException("原神档案或编辑状态已变化，未保存。");
        var current = await LoadAsync(proposal.Target, ct).ConfigureAwait(false);
        var (rechecked, plan) = BuildProposal(current, proposal.Revision, proposal.Changes.ToDictionary(c => c.Path, c => c.NewValue));
        if (plan is null || !rechecked.Validation.Valid || !rechecked.Changes.SequenceEqual(proposal.Changes))
            throw new SettingsConflictException();

        var revision = await database.SaveVerifiedGenshinConfigurationAsync(current.Account, current.Settings, plan,
            proposal.Id, stillAllowed, ct).ConfigureAwait(false);
        return new(Revision(current.Account, revision), proposal.Id);
    }

    private async Task<Current> LoadAsync(AccountConfigurationTarget target, CancellationToken ct)
    {
        if (target.Game != Game || target.AccountId == Guid.Empty)
            throw new ArgumentException("请选择原神账号档案。");
        var account = (await database.LoadAccountsAsync(Game, ct).ConfigureAwait(false)).SingleOrDefault(a => a.Id == target.AccountId)
            ?? throw new ArgumentException("未找到指定的原神账号档案。");
        AccountSettingsSnapshot settings;
        try { settings = await database.LoadAccountSettingsAsync(account.Id, ct).ConfigureAwait(false); }
        catch (JsonException ex) { throw new InvalidOperationException("该原神档案的配置无法读取，请核对档案。", ex); }
        if (settings.Values.Any(pair => !Fields.ContainsKey(pair.Key) || pair.Value is null))
            throw new InvalidOperationException("该原神档案的配置无法读取，请核对档案中的旧设置。");

        var values = BetterGiAccountPlans.Parameters.Select(field => new ConfigurationValue(
            account.Enabled ? field : field with { Access = ParameterAccess.VerifiedReadOnly },
            settings.Values.GetValueOrDefault(field.Path, field.DefaultValue), "VIvian 独立原神档案")).ToArray();
        return new(account, settings, new(target, account.Name, Revision(account, settings.Revision), values,
            account.Enabled ? null : "该原神账号档案已停用。"));
    }

    private static (AccountConfigurationProposal Proposal, BetterGiAccountPlan? Plan) BuildProposal(
        Current current, string revision, IReadOnlyDictionary<string, string> changes)
    {
        if (current.View.Revision != revision) throw new SettingsConflictException();
        if (current.View.WriteUnavailableReason is { } reason) throw new InvalidOperationException(reason);
        var desired = current.View.Values.ToDictionary(v => v.Schema.Path, v => v.Value!, StringComparer.Ordinal);
        var planned = new List<ConfigurationChange>();
        foreach (var (key, value) in changes)
        {
            if (!Fields.TryGetValue(key, out var field) || field.Access != ParameterAccess.VerifiedReadWrite)
                throw new ArgumentException("原神计划包含未登记的可写字段。");
            var updated = ParameterRules.Normalize(field, value);
            if (desired[key] == updated) continue;
            planned.Add(new(key, desired[key], updated));
            desired[key] = updated;
        }
        var proposal = new AccountConfigurationProposal(Guid.NewGuid(), current.View.Target, revision, planned,
            new(false, "配置没有修改。"));
        if (planned.Count == 0) return (proposal, null);
        try
        {
            return (proposal with { Validation = new(true, "计划参数已通过校验。") }, BetterGiAccountPlans.Compile(desired));
        }
        catch (ArgumentException ex)
        {
            return (proposal with { Validation = new(false, ex.Message) }, null);
        }
    }

    private static string Revision(GameAccount account, long settingsRevision) =>
        FormattableString.Invariant($"bettergi-account-plan-v1:{account.Id:D}:{account.Revision}:{settingsRevision}");
}
