// A separate, harmless UAC topology test. It never starts an engine, game, or input automation.
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using VIvian.Infrastructure.Engines.Cli;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
if (args.Length < 2) throw new ArgumentException("用法：VIvian.UacBoundaryProbe.exe parent <独立报告目录>");
var mode = args[0];
var directory = Path.GetFullPath(args[1]);
Directory.CreateDirectory(directory);
var runId = args.Length >= 3 ? args[2] : Guid.NewGuid().ToString("N");
var stopFile = Path.Combine(directory, runId + ".stop");
var workerFile = Path.Combine(directory, runId + ".worker.json");
var childFile = Path.Combine(directory, runId + ".child.json");
var resultFile = Path.Combine(directory, "结果.json");
var executable = Environment.ProcessPath ?? throw new InvalidOperationException("缺少自身程序路径");

switch (mode)
{
    case "child":
        WriteIdentity(childFile, runId, null);
        await WaitForStopAsync(stopFile);
        break;
    case "worker":
        using (var child = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = directory,
            ArgumentList = { "child", directory, runId },
        }) ?? throw new InvalidOperationException("无害子进程未能启动"))
        {
            WriteIdentity(workerFile, runId, child.Id);
            await WaitForStopAsync(stopFile);
            if (!await WaitForExitAsync(child, TimeSpan.FromSeconds(5)))
            {
                // This is the exact child handle created above, never a process-name search.
                child.Kill();
                await child.WaitForExitAsync();
            }
        }
        break;
    case "parent":
        if (IsElevated()) throw new InvalidOperationException("请从普通权限终端运行探针入口");
        var report = new Dictionary<string, object?>
        {
            ["runId"] = runId, ["kind"] = "harmless_uac_topology",
            ["uac"] = "not_requested", ["worker"] = "unknown", ["child"] = "unknown",
        };
        try
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = directory,
                ArgumentList = { "worker", directory, runId },
            };
            report["uac"] = "requested";
            using var worker = Process.Start(start) ?? throw new InvalidOperationException("UAC 后未取得进程句柄");
            report["uac"] = "accepted";
            report["launcherPidMatches"] = await WaitForFileAsync(workerFile, TimeSpan.FromSeconds(10))
                && ReadIdentity(workerFile)?.Pid == worker.Id;
            report["childReported"] = await WaitForFileAsync(childFile, TimeSpan.FromSeconds(10));
            var workerIdentity = ReadIdentity(workerFile);
            var childIdentity = ReadIdentity(childFile);
            report["workerBeforeStop"] = Check(workerIdentity, runId).ToString();
            report["childBeforeStop"] = Check(childIdentity, runId).ToString();
            // The worker and child poll only this run's marker. Both self-exit after 40 seconds even if it is lost.
            await File.WriteAllTextAsync(stopFile, runId);
            report["stopSent"] = true;
            report["workerWaitEnded"] = await WaitForExitAsync(worker, TimeSpan.FromSeconds(45));
            report["worker"] = Check(workerIdentity, runId).ToString();
            report["child"] = Check(childIdentity, runId).ToString();
            report["cleanupConfirmed"] = (bool)report["launcherPidMatches"]!
                && workerIdentity?.Elevated == true && childIdentity?.Elevated == true
                && workerIdentity.ChildPid == childIdentity.Pid
                && (string)report["workerBeforeStop"]! == nameof(WorkerExitState.Running)
                && (string)report["childBeforeStop"]! == nameof(WorkerExitState.Running)
                && (string)report["worker"]! == nameof(WorkerExitState.Exited)
                && (string)report["child"]! == nameof(WorkerExitState.Exited);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            report["uac"] = "rejected";
            report["cleanupConfirmed"] = false;
        }
        catch (Exception ex)
        {
            report["errorType"] = ex.GetType().Name;
            report["cleanupConfirmed"] = false;
        }
        await File.WriteAllTextAsync(resultFile, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("报告：" + resultFile);
        break;
    default: throw new ArgumentException("未知模式");
}

static bool IsElevated()
{
    using var identity = WindowsIdentity.GetCurrent();
    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
}

static void WriteIdentity(string file, string runId, int? childPid)
{
    using var self = Process.GetCurrentProcess();
    var value = new Identity(runId, self.Id,
        new DateTimeOffset(self.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds() / 1000.0,
        IsElevated(), childPid);
    File.WriteAllText(file, JsonSerializer.Serialize(value));
}

static Identity? ReadIdentity(string file)
{
    try { return JsonSerializer.Deserialize<Identity>(File.ReadAllText(file)); }
    catch (Exception ex) when (ex is IOException or JsonException) { return null; }
}

static WorkerExitState Check(Identity? identity, string runId)
{
    if (identity is null || identity.RunId != runId) return WorkerExitState.Unknown;
    var record = JsonSerializer.Serialize(new
    {
        run_id = runId, task = "daily", ended_at = "probe-only", @lock = "acquired",
        pid = identity.Pid, pid_create_time = identity.StartedAt, status = "stopped",
    });
    return March7thWorkerExit.Check(record, runId, "daily");
}

static async Task WaitForStopAsync(string stopFile)
{
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(40);
    while (DateTime.UtcNow < deadline)
    {
        if (File.Exists(stopFile)) return;
        await Task.Delay(100);
    }
}

static async Task<bool> WaitForFileAsync(string path, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        if (File.Exists(path)) return true;
        await Task.Delay(100);
    }
    return File.Exists(path);
}

static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
{
    try { await process.WaitForExitAsync().WaitAsync(timeout); return true; }
    catch (TimeoutException) { return false; }
}

internal sealed record Identity(string RunId, int Pid, double StartedAt, bool Elevated, int? ChildPid);
