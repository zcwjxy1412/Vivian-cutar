using System.Security.Cryptography;
using System.Text;
using VIvian.Core;
using VIvian.Core.Engines;

namespace VIvian.Infrastructure.Assistant;

/// <summary>Changes only VIvian's independent account overrides, using the same upstream defaults as the Star Rail batch.</summary>
public sealed class StarRailAccountConfigurationProvider(LocalDatabase database, IConfigurationControl configuration,
    Func<string> upstreamInstall, Func<bool> runtimeBlocked, Func<IDisposable>? acquireReadLease = null) : IAccountConfigurationProvider
{
    public GameId Game => GameId.StarRail;
    private const string LegacyReadOnly = "原有账号使用星铁共享配置，助手只提供读取。请选择已登记的独立星铁账号。";
    private sealed record Current(GameAccount Account, AccountSettingsSnapshot Settings, ConfigurationSnapshot Effective,
        AccountConfigurationView View);

    public async Task<AccountConfigurationView> ReadAsync(AccountConfigurationTarget target, CancellationToken ct)
    {
        using var desktop = acquireReadLease?.Invoke();
        return (await LoadAsync(target, ct).ConfigureAwait(false)).View;
    }

    public async Task<AccountConfigurationProposal> ProposeAsync(AccountConfigurationTarget target, string revision,
        IReadOnlyDictionary<string, string> changes, CancellationToken ct)
    {
        using var desktop = acquireReadLease?.Invoke();
        var current = await LoadAsync(target, ct).ConfigureAwait(false);
        RequireWritable(current, revision);
        var proposal = configuration.ProposeChanges(current.Effective, changes);
        var validation = configuration.ValidateChanges(proposal, runtimeBlocked());
        if (validation.Deferred) validation = new(false, "引擎正在运行或配置恢复待确认，结束后重新读取并生成提案。");
        return new(Guid.NewGuid(), target, revision, proposal.Changes, validation);
    }

    public async Task<AccountConfigurationApplied> ApplyAsync(AccountConfigurationProposal proposal,
        Func<bool> stillAllowed, CancellationToken ct)
    {
        if (!proposal.Validation.Valid || !stillAllowed())
            throw new InvalidOperationException("未通过确认或运行检查，未写入配置。");
        var current = await LoadAsync(proposal.Target, ct).ConfigureAwait(false);
        RequireWritable(current, proposal.Revision);
        var candidate = configuration.ProposeChanges(current.Effective, proposal.Changes.ToDictionary(c => c.Path, c => c.NewValue));
        if (!candidate.Changes.SequenceEqual(proposal.Changes)) throw new SettingsConflictException();
        var validation = configuration.ValidateChanges(candidate, runtimeBlocked());
        if (!validation.Valid) throw new InvalidOperationException("目标配置当前无法写入，请重新读取。");

        var values = new Dictionary<string, string>(current.Settings.Values);
        foreach (var change in candidate.Changes) values[change.Path] = change.NewValue;
        bool CanCommit()
        {
            if (!stillAllowed() || runtimeBlocked()) return false;
            var upstream = configuration.ReadConfiguration(Game, upstreamInstall());
            return upstream.Path == current.Effective.Path && upstream.Revision == current.Effective.Revision
                && configuration.ValidateChanges(candidate, engineRunning: false).Valid;
        }

        var revision = await database.SaveVerifiedAccountConfigurationAsync(current.Account, current.Settings,
            values, proposal.Id, CanCommit, ct).ConfigureAwait(false);
        return new(Revision(current.Account, revision, current.Effective), proposal.Id);
    }

    private async Task<Current> LoadAsync(AccountConfigurationTarget target, CancellationToken ct)
    {
        if (target.Game != Game || target.AccountId == Guid.Empty)
            throw new ArgumentException("目标游戏和账号不匹配。");
        if (runtimeBlocked()) throw new InvalidOperationException("引擎正在运行或配置恢复待确认，请稍后读取。");
        var account = (await database.LoadAccountsAsync(Game, ct).ConfigureAwait(false)).SingleOrDefault(a => a.Id == target.AccountId)
            ?? throw new InvalidOperationException("这个游戏没有该账号。");
        var settings = await database.LoadAccountSettingsAsync(account.Id, ct).ConfigureAwait(false);
        var install = upstreamInstall();
        if (string.IsNullOrWhiteSpace(install)) throw new InvalidOperationException("请在引擎与环境中配置星铁助手目录。");
        var upstream = configuration.ReadConfiguration(Game, install);
        ct.ThrowIfCancellationRequested();
        var legacy = account.Id == LocalDatabase.LegacyId(Game);
        var effective = legacy ? upstream : upstream with
        {
            Values = upstream.Values.Select(v => v.Schema.Access == ParameterAccess.VerifiedReadWrite && v.Value is not null
                && settings.Values.TryGetValue(v.Schema.Path, out var saved) && ParameterRules.IsValid(v.Schema, saved)
                ? v with { Value = saved, State = "账号独立设置" } : v).ToArray(),
        };
        var reason = legacy ? LegacyReadOnly : !account.Enabled ? "该账号已停用。"
            : effective.Values.Any(v => v.Value is not null && v.Schema.Access == ParameterAccess.VerifiedReadWrite)
                ? null : "固定版本配置中没有已核对的可写字段。";
        var view = new AccountConfigurationView(target, account.Name, Revision(account, settings.Revision, upstream), effective.Values, reason);
        return new(account, settings, effective, view);
    }

    private static void RequireWritable(Current current, string revision)
    {
        if (current.View.Revision != revision) throw new SettingsConflictException();
        if (current.View.WriteUnavailableReason is { } reason) throw new InvalidOperationException(reason);
    }

    private static string Revision(GameAccount account, long revision, ConfigurationSnapshot upstream) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{account.Id:D}:{account.Revision}:{revision}:{upstream.Path}:{upstream.Revision}")));
}
