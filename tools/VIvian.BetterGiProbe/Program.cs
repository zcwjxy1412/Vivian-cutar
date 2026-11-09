using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using VIvian.Infrastructure.Engines.BetterGi;

if (args is not ["inspect", _] and not ["inspect", _, _])
{
    Console.Error.WriteLine("用法：VIvian.BetterGiProbe inspect <BetterGI 安装目录> [一条龙配置名]");
    return 2;
}

Console.OutputEncoding = System.Text.Encoding.UTF8;
var root = args[1];
var installation = await BetterGiInstallationInspector.InspectAsync(root);
BetterGiOneDragonPlan? plan = null;
string? configError = null;
try
{
    plan = await BetterGiConfiguration.InspectOneDragonAsync(root, args.Length == 3 ? args[2] : null);
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
