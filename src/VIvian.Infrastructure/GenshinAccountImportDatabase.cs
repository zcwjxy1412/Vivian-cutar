using VIvian.Core;
using VIvian.Core.Accounts;

namespace VIvian.Infrastructure;

public sealed partial class LocalDatabase
{
    internal async Task<(GameAccount? Account, bool Created)> RegisterGenshinPhotoAccountAsync(
        LoginAccountObservation account, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        // Reserve the writer before lookup so two imports cannot both see an absent identity.
        using var transaction = connection.BeginTransaction(deferred: false);
        using var lookup = connection.CreateCommand();
        lookup.Transaction = transaction;
        lookup.CommandText = """
            SELECT a.account_id
            FROM account_login_profiles p JOIN game_accounts a ON a.account_id = p.account_id
            WHERE a.game = $game AND p.normalized_phone = $identity
            LIMIT 2;
            """;
        lookup.Parameters.AddWithValue("$game", (int)GameId.Genshin);
        lookup.Parameters.AddWithValue("$identity", account.NormalizedPhone);

        Guid? existingId = null;
        await using (var reader = await lookup.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var storedId = reader.GetString(0);
                existingId = storedId == "legacy-genshin" ? LegacyId(GameId.Genshin)
                    : Guid.TryParse(storedId, out var parsed) ? parsed : null;
                if (existingId is null || await reader.ReadAsync(ct).ConfigureAwait(false)) return (null, false);
            }
        }

        var id = existingId ?? Guid.NewGuid();
        if (existingId is null)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO game_accounts(account_id, game, display_name, sort_order, login_state)
                    SELECT $id, $game, '原神账号 ' || next_order, next_order, 'recognized'
                    FROM (SELECT MAX(1, COALESCE(MAX(sort_order), 0) + 1) AS next_order FROM game_accounts WHERE game = $game);
                INSERT INTO account_settings(account_id) VALUES($id);
                INSERT INTO account_task_plans(account_id) VALUES($id);
                INSERT INTO account_login_profiles(account_id, masked_phone, normalized_phone, row_order,
                    confidence, last_scan_at, detail) VALUES($id, $mask, $identity, $row, $confidence, $at, '导入原神登录照片');
                """;
            insert.Parameters.AddWithValue("$id", DatabaseId(id));
            insert.Parameters.AddWithValue("$game", (int)GameId.Genshin);
            insert.Parameters.AddWithValue("$mask", account.MaskedPhone);
            insert.Parameters.AddWithValue("$identity", account.NormalizedPhone);
            insert.Parameters.AddWithValue("$row", account.RowOrder);
            insert.Parameters.AddWithValue("$confidence", account.Confidence);
            insert.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        transaction.Commit();
        var saved = (await LoadAccountsAsync(GameId.Genshin, ct).ConfigureAwait(false)).SingleOrDefault(row => row.Id == id);
        return (saved, existingId is null);
    }
}
