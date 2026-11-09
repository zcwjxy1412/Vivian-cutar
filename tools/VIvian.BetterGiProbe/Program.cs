using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using VIvian.Infrastructure.Engines.BetterGi;

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Length != 0 && args[0] is ("render-account" or "run-account" or "run-daily" or "recover-daily"))
{
    try
    {
        return args switch
        {
            ["render-account", var id, var settingsPath, var client] when Guid.TryParse(id, out var accountId) =>
                PreparedAccountPlan.Render(accountId, settingsPath, client),
            ["run-account", var accountStage, var id, "--manual-account-confirmed"] when Guid.TryParse(id, out var accountId) =>
                await DailyAcceptance.RunAsync(accountStage, accountId),
            ["run-daily", var dailyStage] => await DailyAcceptance.RunAsync(dailyStage),
            ["recover-daily", var recoveryStage] => await DailyAcceptance.RecoverAsync(recoveryStage),
            _ => throw new ArgumentException("验收命令缺少隔离目录、账号档案编号或设置文件。"),
        };
    }
    catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException
        or InvalidDataException or JsonException or ArgumentException or System.ComponentModel.Win32Exception
        or System.Security.SecurityException)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
}

if (args is not ["inspect", _] and not ["inspect", _, _])
{
    Console.Error.WriteLine("用法：VIvian.BetterGiProbe inspect <BetterGI 安装目录> [一条龙配置名]；也支持 render-account <账号编号> <设置文件> <客户端>、run-account <隔离目录> <账号编号> --manual-account-confirmed、run-daily / recover-daily <隔离目录>");
    return 2;
}

var root = args[1];
var installation = await BetterGiInstallationInspector.InspectAsync(root);
BetterGiOneDragonPlan? plan = null;
BetterGiDomainResinPlan? domain = null;
string? configError = null;
try
{
    plan = await BetterGiConfiguration.InspectOneDragonAsync(root, args.Length == 3 ? args[2] : null);
    domain = await BetterGiConfiguration.InspectDomainResinAsync(root);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
    or InvalidOperationException or ArgumentException or JsonException or TimeoutException)
{
    configError = ex is InvalidDataException ? ex.Message : ex.GetType().Name;
}
var instanceBusy = installation.ProgramPath is { } path && new BetterGiProcessProbe().IsBusy(path);
Console.WriteLine(JsonSerializer.Serialize(new
{
    installation.DetectedVersion,
    installation.Compatibility,
    installation.Checks,
    Plan = plan,
    DomainResin = domain,
    ConfigError = configError,
    InstanceBusy = instanceBusy,
    CanRun = BetterGiAdapter.ExecutionGateOpen,
    CanWrite = false,
    CanPause = false,
    CanResume = false,
    BetterGiTaskCatalog.Tasks,
    BetterGiTaskCatalog.Parameters,
}, new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    Converters = { new JsonStringEnumConverter() },
}));
return configError is null ? 0 : 1;
