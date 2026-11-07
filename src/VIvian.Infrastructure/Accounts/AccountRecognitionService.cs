using VIvian.Core;
using VIvian.Core.Accounts;

namespace VIvian.Infrastructure.Accounts;

/// <summary>
/// Turns a login-screen observation into VIvian account profiles. Recognition never performs a click and never
/// reads game memory or credentials; it only records what an existing local OCR frame shows.
/// </summary>
public sealed class AccountRecognitionService
{
    private readonly LocalDatabase _database;
    private readonly ILoginScreenSource _source;
    private readonly Func<string, CancellationToken, Task<LoginScreenView>> _import;

    public AccountRecognitionService(LocalDatabase database, LoginScreenReader reader)
        : this(database, reader, reader.ImportAsync)
    {
    }

    internal AccountRecognitionService(LocalDatabase database, ILoginScreenSource source,
        Func<string, CancellationToken, Task<LoginScreenView>> import)
    {
        _database = database;
        _source = source;
        _import = import;
    }

    public sealed record RegistrationResult(bool Success, string Detail, LoginScreenView? View,
        IReadOnlyList<GameAccount> Registered, IReadOnlyList<string> Warnings);

    public async Task<RegistrationResult> ScanAndRegisterAsync(CancellationToken cancellationToken)
    {
        LoginScreenView view;
        try { view = await _source.ScanAsync(cancellationToken); }
        catch (Exception ex) { return new(false, "扫描登录界面失败：" + ex.Message, null, [], []); }
        return await RegisterAsync(view, "live", cancellationToken);
    }

    public async Task<RegistrationResult> ImportAndRegisterAsync(string path, CancellationToken cancellationToken)
    {
        LoginScreenView view;
        try { view = await _import(path, cancellationToken); }
        catch (Exception ex) { return new(false, "读取截图失败：" + ex.Message, null, [], []); }
        return await RegisterAsync(view, "import", cancellationToken);
    }

    private async Task<RegistrationResult> RegisterAsync(LoginScreenView view, string source,
        CancellationToken cancellationToken)
    {
        var observation = view.Result.Observation;
        if (!view.Result.Success || observation is null)
            return new(false, view.Result.Detail, view, [], []);

        var warnings = new List<string>();
        var registered = new List<GameAccount>();
        var existing = await _database.LoadLoginProfilesAsync(GameId.StarRail, cancellationToken);
        var byPhone = existing.GroupBy(p => p.NormalizedPhone, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var createdPhones = new HashSet<string>(StringComparer.Ordinal);

        foreach (var account in observation.Accounts.OrderBy(a => a.RowOrder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!createdPhones.Add(account.NormalizedPhone))
            {
                warnings.Add($"第 {account.RowOrder + 1} 行与另一账号的脱敏手机号相同；为避免误登录，未重复建档。");
                continue;
            }

            if (byPhone.TryGetValue(account.NormalizedPhone, out var matches))
            {
                if (matches.Length == 1)
                {
                    var match = matches[0];
                    await _database.SaveLoginProfileAsync(match.AccountId, account.MaskedPhone,
                        account.NormalizedPhone, account.RowOrder, account.Confidence, null,
                        $"扫描来源：{source}；第 {account.RowOrder + 1} 个选择项。", cancellationToken);
                    var refreshed = (await _database.LoadAccountsAsync(GameId.StarRail, cancellationToken))
                        .FirstOrDefault(a => a.Id == match.AccountId);
                    if (refreshed is not null) registered.Add(refreshed);
                    continue;
                }

                warnings.Add($"脱敏手机号 {account.MaskedPhone} 对应多个档案；请先手动区分，系统不会自动选择。");
                continue;
            }

            var name = $"星铁 {account.MaskedPhone}";
            var added = await _database.AddAccountAsync(GameId.StarRail, name, "", null, "",
                ct: cancellationToken);
            await _database.SaveLoginProfileAsync(added.Id, account.MaskedPhone, account.NormalizedPhone,
                account.RowOrder, account.Confidence, null,
                $"扫描来源：{source}；第 {account.RowOrder + 1} 个选择项。", cancellationToken);
            var accountRecord = (await _database.LoadAccountsAsync(GameId.StarRail, cancellationToken))
                .FirstOrDefault(a => a.Id == added.Id) ?? added;
            registered.Add(accountRecord);
            byPhone[account.NormalizedPhone] = [new LocalDatabase.AccountLoginProfile(added.Id, account.MaskedPhone,
                account.NormalizedPhone, account.RowOrder, account.Confidence, null, DateTimeOffset.UtcNow, "")];
        }

        await _database.RecordScanSessionAsync(GameId.StarRail, source, observation.WindowWidth,
            observation.WindowHeight, observation.Accounts.Count,
            warnings.Count == 0 ? "识别完成。" : string.Join(" ", warnings), cancellationToken);

        var detail = registered.Count == 0
            ? "识别到账号，但没有新建档案。" + (warnings.Count > 0 ? " " + string.Join(" ", warnings) : "")
            : $"已登记 {registered.Count} 个账号。";
        return new(true, detail, view, registered, warnings);
    }
}
