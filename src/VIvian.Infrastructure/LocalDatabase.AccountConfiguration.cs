using System.Text.Json;
using VIvian.Core;
using VIvian.Core.Engines;

namespace VIvian.Infrastructure;

public sealed partial class LocalDatabase
{
    internal Task<long> SaveVerifiedAccountConfigurationAsync(GameAccount account, AccountSettingsSnapshot previous,
        IReadOnlyDictionary<string, string> values, Guid proposalId, Func<bool> stillAllowed, CancellationToken ct)
    {
        if (account.Game != GameId.StarRail || account.Id == LegacyId(GameId.StarRail) || account.Id != previous.AccountId)
            throw new InvalidOperationException("配置写入要求独立星铁账号。");
        ValidateStarRailAccountSettings(values);
        return WriteVerifiedConfigurationAsync(account, previous, values, proposalId, stillAllowed, ct);
    }

    internal Task<long> SaveVerifiedGenshinConfigurationAsync(GameAccount account, AccountSettingsSnapshot previous,
        IReadOnlyDictionary<string, string> validatedValues, Guid proposalId, Func<bool> stillAllowed, CancellationToken ct)
    {
        if (account.Game != GameId.Genshin || account.Id != previous.AccountId || proposalId == Guid.Empty)
            throw new InvalidOperationException("配置写入要求匹配的原神账号档案。");
        return WriteVerifiedConfigurationAsync(account, previous, validatedValues, proposalId, stillAllowed, ct);
    }

    private async Task<long> WriteVerifiedConfigurationAsync(GameAccount account, AccountSettingsSnapshot previous,
        IReadOnlyDictionary<string, string> values, Guid proposalId, Func<bool> stillAllowed, CancellationToken ct)
    {
        var updated = JsonSerializer.Serialize(values);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$proposal", proposalId.ToString("D"));
        command.Parameters.AddWithValue("$game", (int)account.Game);
        command.Parameters.AddWithValue("$account", DatabaseId(account.Id));
        command.Parameters.AddWithValue("$account_revision", account.Revision);
        command.Parameters.AddWithValue("$revision", previous.Revision);
        command.Parameters.AddWithValue("$updated", updated);
        command.Parameters.AddWithValue("$applied", DateTimeOffset.UtcNow.ToString("O"));

        if (!stillAllowed()) throw new InvalidOperationException("账号或运行状态已变化，未写入。");
        command.CommandText = """
            INSERT INTO account_configuration_audit(proposal_id,game,account_id,before_revision,after_revision,before_json,after_json,applied_at)
            SELECT $proposal,a.game,a.account_id,s.revision,s.revision+1,s.settings_json,$updated,$applied
            FROM account_settings s JOIN game_accounts a ON a.account_id=s.account_id
            WHERE a.account_id=$account AND a.game=$game AND a.revision=$account_revision AND a.enabled=1 AND s.revision=$revision
            AND NOT EXISTS (SELECT 1 FROM account_configuration_audit WHERE proposal_id=$proposal);
            """;
        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) throw new SettingsConflictException();

        command.CommandText = """
            UPDATE account_settings SET settings_json=$updated,revision=revision+1
            WHERE account_id=$account AND revision=$revision;
            """;
        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) throw new SettingsConflictException();

        command.CommandText = "SELECT settings_json FROM account_settings WHERE account_id=$account AND revision=$revision+1;";
        if ((string?)await command.ExecuteScalarAsync(ct).ConfigureAwait(false) != updated)
            throw new IOException("写后回读未通过，配置与审计均已回滚。");

        ct.ThrowIfCancellationRequested();
        if (!stillAllowed()) throw new InvalidOperationException("目标账号、配置来源或运行状态已变化，写入已回滚。");
        transaction.Commit();
        return previous.Revision + 1;
    }
}
