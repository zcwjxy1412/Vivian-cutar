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
    private readonly IInputHost? _input;
    private readonly ILoginSwitchDriver? _driver;

    public AccountRecognitionService(LocalDatabase database, LoginScreenReader reader, IInputHost input,
        ILoginSwitchDriver driver) : this(database, reader, reader.ImportAsync, input)
    {
        _driver = driver;
    }

    public AccountRecognitionService(LocalDatabase database, LoginScreenReader reader)
        : this(database, reader, reader.ImportAsync)
    {
    }

    internal AccountRecognitionService(LocalDatabase database, ILoginScreenSource source,
        Func<string, CancellationToken, Task<LoginScreenView>> import, IInputHost? input = null)
    {
        _database = database;
        _source = source;
        _import = import;
        _input = input;
    }

    public sealed record RegistrationResult(bool Success, string Detail, LoginScreenView? View,
        IReadOnlyList<GameAccount> Registered, IReadOnlyList<string> Warnings);

    public async Task<RegistrationResult> ScanAndRegisterAsync(CancellationToken cancellationToken, Action? beforeDesktopMutation = null)
    {
        LoginScreenView view;
        try
        {
            if (_driver is not null)
            {
                if (!Processes.WindowsProcessSupervisor.IsCurrentProcessElevated())
                    return new(false, "请先以管理员身份打开测试版完成授权，再自动扫描。", null, [], []);
                beforeDesktopMutation?.Invoke();
                var ready = await _driver.EnsureLoginAsync(new SwitchPolicy(), cancellationToken);
                if (!ready.Ok) return new(false, ready.Detail, null, [], []);
            }
            view = await _source.ScanAsync(cancellationToken);
            if (_input is not null && view.Window is { } window &&
                view.Result.Observation is { AccountListExpanded: false, Accounts.Count: 1 } card)
            {
                var row = card.Accounts[0];
                beforeDesktopMutation?.Invoke();
                var clicked = await _input.ClickAsync(window, (int)row.CenterX, (int)row.CenterY, cancellationToken);
                if (!clicked.Ok) return new(false, "无法展开保存账号列表：" + clicked.Error, view, [], []);
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    await Task.Delay(400, cancellationToken);
                    view = await _source.ScanAsync(cancellationToken);
                    if (view.Result.Observation is { AccountListExpanded: true }) break;
                }
                if (view.Result.Observation is not { AccountListExpanded: true })
                    return new(false, "未确认保存账号列表已展开，未把当前卡片当成全部账号。", view, [], []);
                if (view.Panel is { } panel)
                    view = view with { Result = LoginScreenAnalyzer.Analyze(view.Result.Observation.WindowWidth,
                        view.Result.Observation.WindowHeight, view.Lines,
                        new ScreenRegion(panel.X, panel.Y, panel.Width, panel.Height), row) };
            }
        }
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
        {
            // A failed scan is still worth recording: the window identity and ownership check explain why.
            if (view.Evidence is { } failedEvidence)
                await _database.RecordScanSessionAsync(GameId.StarRail, source, failedEvidence.ClientWidth,
                    failedEvidence.ClientHeight, 0, "失败：" + view.Result.Detail + " | " + failedEvidence.Describe(),
                    cancellationToken);
            return new(false, view.Result.Detail, view, [], []);
        }

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
            (warnings.Count == 0 ? "识别完成。" : string.Join(" ", warnings)) +
            (view.Evidence is { } evidence ? " | " + evidence.Describe() : ""), cancellationToken);

        var detail = registered.Count == 0
            ? "识别到账号，但没有新建档案。" + (warnings.Count > 0 ? " " + string.Join(" ", warnings) : "")
            : $"已登记 {registered.Count} 个账号。";
        return new(true, detail, view, registered, warnings);
    }
}
