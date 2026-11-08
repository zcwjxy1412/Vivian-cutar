using System.Text;
using VIvian.Core.Accounts;
using VIvian.Infrastructure.Accounts;

Console.OutputEncoding = Encoding.UTF8;
var reader = new LoginScreenReader();

if (args.Length == 2 && args[0] == "--reference-flow")
{
    var files = Directory.GetFiles(args[1], "*.png").OrderBy(p =>
        System.Text.RegularExpressions.Regex.Replace(Path.GetFileName(p), @"\d+", m => m.Value.PadLeft(12, '0')));
    LoginAccountObservation? priorCard = null;
    foreach (var file in files)
    {
        var reference = await reader.ImportAsync(file, CancellationToken.None);
        var selected = reference.Frame is not null && LoginRetentionDetector.IsSelected(reference.Frame, reference.Lines);
        var region = reference.Panel is { } p ? new ScreenRegion(p.X - 8, p.Y - 8, p.Width + 16, p.Height + 16) : null;
        var state = LoginStateClassifier.Classify(reference.Lines, reference.Panel is not null, region,
            preserveLoginSelected: selected);
        var anchoredRows = LoginScreenAnalyzer.SavedRows(reference.Lines, region, priorCard);
        if (state.State == LoginScreenState.LoginCard) priorCard = state.Accounts.Single();
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { File = Path.GetFileName(file),
            State = state.State.ToString(), SavedRows = state.State is LoginScreenState.AccountList or LoginScreenState.LoginCard
                ? anchoredRows.Count : 0,
            PreserveSelected = selected, Action = state.ActionTarget?.Text }));
    }
    return 0;
}

if (args.Length > 0 && args[0] == "--front")
{
    var host = args.Length > 1 ? args[1] : null;
    var window = StarRailWindow.Find();
    if (window is null) { Console.WriteLine("star-rail-window-not-found"); return 2; }
    await using var input = new ElevatedInputHost(host);
    var response = await input.ForegroundAsync(window, CancellationToken.None);
    Console.WriteLine($"foreground ok={response.Ok} error={response.Error} detail={response.Detail}");
    Console.WriteLine($"window={window.Handle} pid={window.ProcessId} client={window.ClientWidth}x{window.ClientHeight}");
    return response.Ok ? 0 : 5;
}

if (args.Length > 0 && args[0] == "--stop")
{
    var host = args.Length > 1 ? args[1] : null;
    var window = StarRailWindow.Find();
    if (window is null) { Console.WriteLine("star-rail-window-not-found"); return 2; }
    await using var input = new ElevatedInputHost(host);
    var response = await input.KillAsync(window, CancellationToken.None);
    Console.WriteLine($"stop ok={response.Ok} error={response.Error} detail={response.Detail}");
    return response.Ok ? 0 : 5;
}

if (args.Length > 0 && args[0] == "--click")
{
    var host = args.Length > 1 ? args[1] : null;
    var x = args.Length > 2 ? int.Parse(args[2]) : 0;
    var y = args.Length > 3 ? int.Parse(args[3]) : 0;
    var window = StarRailWindow.Find();
    if (window is null) { Console.WriteLine("star-rail-window-not-found"); return 2; }
    await using var input = new ElevatedInputHost(host);
    var response = await input.ClickAsync(window, x, y, CancellationToken.None);
    Console.WriteLine($"click ok={response.Ok} error={response.Error} detail={response.Detail}");
    return response.Ok ? 0 : 5;
}

if (args.Length > 0 && args[0] == "--restart")
{
    var host = args.Length > 1 ? args[1] : null;
    var gamePath = args.Length > 2 ? args[2] : @"E:\Star Rail Game\StarRail.exe";
    var policy = new SwitchPolicy();
    var verifier = new StarRailSessionVerifier(reader);
    await using var input = new ElevatedInputHost(host);
    var driver = new StarRailLoginDriver(reader, verifier, input, () => gamePath,
        message => Console.WriteLine("  driver: " + message));
    var restarted = await driver.RestartToLoginAsync(policy, CancellationToken.None);
    Console.WriteLine($"ok={restarted.Ok} stage={restarted.Stage} detail={restarted.Detail}");
    return restarted.Ok ? 0 : 5;
}

if (args.Length > 0 && args[0] == "--switch")
{
    var host = args.Length > 1 ? args[1] : null;
    var gamePath = args.Length > 2 ? args[2] : @"E:\Star Rail Game\StarRail.exe";
    var phone = args.Length > 3 ? args[3] : null;
    var policy = new SwitchPolicy();
    var verifier = new StarRailSessionVerifier(reader);
    await using var input = new ElevatedInputHost(host);
    var driver = new StarRailLoginDriver(reader, verifier, input, () => gamePath,
        message => Console.WriteLine("  driver: " + message));

    Console.WriteLine("== ensure login ==");
    var ensured = await driver.EnsureLoginAsync(policy, CancellationToken.None);
    Console.WriteLine($"ok={ensured.Ok} stage={ensured.Stage} detail={ensured.Detail}");
    if (!ensured.Ok) return 2;

    var scan = await reader.ScanAsync(CancellationToken.None);
    if (scan.Result.Observation is not { } observation || observation.Accounts.Count == 0)
    {
        Console.WriteLine("没有识别到已保存账号：" + scan.Result.Detail);
        return 3;
    }
    var target = phone is null
        ? observation.Accounts[0]
        : observation.Accounts.FirstOrDefault(a =>
            a.NormalizedPhone == MaskedPhoneParser.Normalize(phone));
    if (target is null)
    {
        Console.WriteLine("登录界面没有找到目标账号：" + phone);
        return 4;
    }
    Console.WriteLine($"target={target.MaskedPhone} row={target.RowOrder} at ({target.CenterX:0},{target.CenterY:0})");

    Console.WriteLine("== switch ==");
    var switched = await driver.SwitchAsync(
        new LoginTarget(target.NormalizedPhone, target.MaskedPhone, target.RowOrder, null), policy,
        CancellationToken.None);
    Console.WriteLine($"ok={switched.Ok} stage={switched.Stage} uid={switched.Uid} detail={switched.Detail}");
    if (!switched.Ok) return 5;

    var verified = await verifier.VerifyAsync(CancellationToken.None);
    Console.WriteLine($"inGame={verified.InGame} uid={verified.Uid} confidence={verified.Confidence} " +
                      $"detail={verified.Detail}");
    return verified.InGame ? 0 : 6;
}

var view = args.Length == 0 || args[0] == "--live"
    ? await reader.ScanAsync(CancellationToken.None)
    : await reader.ImportAsync(args[0], CancellationToken.None);

Console.WriteLine($"success={view.Result.Success} failure={view.Result.Failure}");
Console.WriteLine($"detail={view.Result.Detail}");
Console.WriteLine($"frame={view.Frame?.Width}x{view.Frame?.Height} panel={view.Panel}");
if (view.Result.Observation is { } screen)
{
    Console.WriteLine($"dialog={screen.LoginDialogVisible} expanded={screen.AccountListExpanded} " +
                      $"confidence={screen.Confidence} accounts={screen.Accounts.Count}");
    foreach (var account in screen.Accounts)
        Console.WriteLine($"  [{account.RowOrder}] {account.MaskedPhone} -> {account.NormalizedPhone} " +
                          $"({account.CenterX:0},{account.CenterY:0}) conf={account.Confidence:0.00} near={account.NearbyText}");
}
foreach (var line in view.Result.OcrLines) Console.WriteLine($"  OCR: {line}");
return view.Result.Success ? 0 : 1;
