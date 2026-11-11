using System.Globalization;
using System.Text.Json;
using VIvian.Core;
using VIvian.Core.Accounts;
using VIvian.Infrastructure.Engines.OneDragon;

namespace VIvian.Infrastructure;

public sealed partial class LocalDatabase
{
    public async Task SaveOneDragonBindingAsync(GameAccount account, OneDragonProfileBinding binding, string masked,
        CancellationToken ct = default)
    {
        if (account.Game != GameId.ZenlessZoneZero || account.Id == LegacyId(GameId.ZenlessZoneZero))
            throw new InvalidOperationException("请选择一个绝区零账号档案。");
        ValidateAccount(account.Name, account.Region, account.Uid);
        masked = masked.Trim();
        var normalized = MaskedPhoneParser.Normalize(masked);
        if (normalized.Length == 0 || masked.Length > 20 || !masked.All(ch => char.IsAsciiDigit(ch) || ch is '*' or '＊'))
            throw new InvalidOperationException("请输入登录画面上的脱敏手机号，例如 138****1234。");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(binding.Root));
        if (binding.Instance is < 1 or > 99) throw new InvalidOperationException("请选择有效的 OneDragon 实例。");

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", DatabaseId(account.Id));
        command.Parameters.AddWithValue("$game", (int)GameId.ZenlessZoneZero);
        command.CommandText = """
            SELECT s.settings_json, p.normalized_phone FROM game_accounts a
            JOIN account_settings s ON a.account_id=s.account_id
            LEFT JOIN account_login_profiles p ON a.account_id=p.account_id
            WHERE a.game=$game AND a.account_id<>$id;
            """;
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var other = OneDragonProfileBinding.FromSettings(JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(0)) ?? []);
                if (other?.Instance == binding.Instance && string.Equals(other.Root, root, StringComparison.OrdinalIgnoreCase)
                    || !reader.IsDBNull(1) && reader.GetString(1) == normalized)
                    throw new InvalidOperationException("此源实例或脱敏登录标识已绑定另一个账号。");
            }
        }
        command.Parameters.AddWithValue("$revision", account.Revision);
        command.Parameters.AddWithValue("$name", account.Name.Trim());
        command.Parameters.AddWithValue("$autorun", account.AutoRun ? 1 : 0);
        command.CommandText = """
            UPDATE game_accounts SET login_state='recognized', display_name=$name, auto_run=$autorun, revision=revision+1
            WHERE game=$game AND account_id=$id AND revision=$revision;
            """;
        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("账号已被修改，请刷新后重试。");
        command.Parameters.AddWithValue("$settings", JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [OneDragonProfileBinding.RootKey] = root,
            [OneDragonProfileBinding.InstanceKey] = binding.Instance.ToString(CultureInfo.InvariantCulture)
        }));
        command.Parameters.AddWithValue("$masked", normalized);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.CommandText = """
            UPDATE account_settings SET settings_json=$settings,revision=revision+1 WHERE account_id=$id;
            INSERT INTO account_login_profiles(account_id,masked_phone,normalized_phone,row_order,confidence,observed_uid,last_scan_at,detail)
            VALUES($id,$masked,$masked,0,1,NULL,$at,'由用户绑定，执行时核对登录画面')
            ON CONFLICT(account_id) DO UPDATE SET masked_phone=$masked,normalized_phone=$masked,observed_uid=NULL,last_scan_at=$at,detail=excluded.detail;
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        transaction.Commit();
    }
}
