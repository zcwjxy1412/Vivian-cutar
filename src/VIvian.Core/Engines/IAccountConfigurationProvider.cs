namespace VIvian.Core.Engines;

public sealed record AccountConfigurationTarget(GameId Game, Guid AccountId);

public sealed record AccountConfigurationView(AccountConfigurationTarget Target, string AccountName, string Revision,
    IReadOnlyList<ConfigurationValue> Values, string? WriteUnavailableReason);

public sealed record AccountConfigurationProposal(Guid Id, AccountConfigurationTarget Target, string Revision,
    IReadOnlyList<ConfigurationChange> Changes, ConfigurationValidation Validation);

public sealed record AccountConfigurationApplied(string Revision, Guid AuditId);

/// <summary>An account-scoped configuration capability. The host owns confirmation and execution admission.</summary>
public interface IAccountConfigurationProvider
{
    GameId Game { get; }
    Task<AccountConfigurationView> ReadAsync(AccountConfigurationTarget target, CancellationToken ct);
    Task<AccountConfigurationProposal> ProposeAsync(AccountConfigurationTarget target, string revision,
        IReadOnlyDictionary<string, string> changes, CancellationToken ct);

    /// <summary>Revalidate the target, revision and engine state before a verified, audited write.</summary>
    Task<AccountConfigurationApplied> ApplyAsync(AccountConfigurationProposal proposal, Func<bool> stillAllowed, CancellationToken ct);
}
