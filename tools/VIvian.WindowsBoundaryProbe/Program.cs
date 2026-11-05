// Harmless desktop validation. Only the executables beside this probe are launched.
using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using VIvian.Core;
using VIvian.Core.Execution;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Processes;
using VIvian.Infrastructure.Simulation;

Console.OutputEncoding = new UTF8Encoding(false);
if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("仅支持 Windows。"); return 2; }
var engine = Path.Combine(AppContext.BaseDirectory, "VIvian.FakeEngine.exe");
var crashHost = Path.Combine(AppContext.BaseDirectory, "VIvian.CrashHost.exe");
if (!File.Exists(engine) || !File.Exists(crashHost)) { Console.Error.WriteLine("测试工具包不完整：缺少 FakeEngine 或 CrashHost。"); return 2; }
var root = Path.Combine(Path.GetTempPath(), "VIvian-DesktopProbe", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var results = new List<CaseResult>();
var revision = typeof(WindowsProcessSupervisor).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "未知";
using var identity = WindowsIdentity.GetCurrent();
var report = new ProbeReport("阶段 B：无害进程测试", Environment.OSVersion.VersionString,
    revision, "0.4.0-dev（用户安装包由用户核对）", DateTimeOffset.UtcNow, results);

async Task Check(string name, Func<Task<CaseResult>> action)
{
    try { results.Add(await action().WaitAsync(TimeSpan.FromSeconds(80))); }
    catch (TimeoutException ex) { results.Add(new(name, "INCONCLUSIVE", null, null, null, null, null, null, "测试超时；不能确认清理状态：" + ex.GetType().Name)); }
    catch (Exception ex) { results.Add(new(name, "FAIL", null, null, null, null, null, null, ex.GetType().Name + "：" + SafeMessage(ex))); }
    Console.WriteLine($"{results[^1].Status} {name}");
}

// The control process is created and held by this probe; no PID/name-based termination occurs.
await Check("B1 Job 归属及同名外部进程隔离", async () =>
{
    using var control = Process.Start(new ProcessStartInfo(engine) {
        ArgumentList = { "--scenario", "probe-identity-wait", "--run-id", Guid.NewGuid().ToString("D") },
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
    try {
        await ReadUntilAsync(control.StandardOutput, "|info|waiting");
        var id = Guid.NewGuid();
        await using var group = await Start(id, "probe-identity-wait");
        await ReadGroupUntilAsync(group.OutputLines, "|info|waiting");
        using var child = Process.GetProcessById(group.RootProcessId);
        using var self = Process.GetCurrentProcess();
        var owned = group.RunId == id && group.OwnsProcess(child.SafeHandle) &&
            !group.OwnsProcess(control.SafeHandle) && !group.OwnsProcess(self.SafeHandle);
        var before = group.ActiveProcessCount;
        var sw = Stopwatch.StartNew();
        var cleanup = await group.TerminateAllAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        var gone = await ProcessExited(child, TimeSpan.FromSeconds(5));
        return new("B1 Job 归属及同名外部进程隔离",
            owned && before >= 1 && cleanup == CleanupState.Confirmed && gone && !control.HasExited ? "PASS" : "FAIL",
            id, child.Id, group.RootStartTime, sw.ElapsedMilliseconds, cleanup.ToString(), null,
            $"自有 Job 归属={owned}；停止前 Job 活跃数={before}；对照进程未受影响={!control.HasExited}");
    }
    finally { if (!control.HasExited) control.Kill(); await control.WaitForExitAsync(); }
});

await Check("B2 子进程及 Job 清理", async () =>
{
    var id = Guid.NewGuid();
    await using var group = await Start(id, "orphan-child");
    var childLine = await ReadGroupUntilAsync(group.OutputLines, "|info|child-pid=");
    var childPid = int.Parse(childLine[(childLine.LastIndexOf('=') + 1)..]);
    await group.WaitForRootExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
    // The child is only queried by handle; never killed independently.
    using var child = Process.GetProcessById(childPid);
    var owned = group.OwnsProcess(child.SafeHandle);
    var sw = Stopwatch.StartNew();
    var cleanup = await group.TerminateAllAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
    return new("B2 子进程及 Job 清理", owned && cleanup == CleanupState.Confirmed && await ProcessExited(child, TimeSpan.FromSeconds(5)) ? "PASS" : "FAIL",
        id, group.RootProcessId, group.RootStartTime, sw.ElapsedMilliseconds, cleanup.ToString(), 0,
        $"子进程 PID={childPid}；属于本次 Job={owned}；清理后 Job 活跃数={group.ActiveProcessCount}");
});

await Check("B3 队列紧急停止及持久化", async () =>
{
    var db = new LocalDatabase(Path.Combine(root, "stop.db"));
    await db.InitializeAsync();
    var scheduler = new QueueScheduler(db, new WindowsProcessSupervisor());
    var first = new FakeEngineAdapter(GameId.Genshin, engine, "long-running");
    var second = new FakeEngineAdapter(GameId.StarRail, engine, "success");
    var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    scheduler.EventReceived += e => { if (e.Message.StartsWith("模拟引擎已启动：PID ", StringComparison.Ordinal))
        { var number = e.Message["模拟引擎已启动：PID ".Length..].Split('，')[0]; if (int.TryParse(number, out var pid)) started.TrySetResult(pid); } };
    var queue = scheduler.RunAsync([new QueueItem(GameId.Genshin, first), new QueueItem(GameId.StarRail, second)]);
    var pid = await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
    using var child = Process.GetProcessById(pid);
    var startTime = child.StartTime.ToUniversalTime();
    var sw = Stopwatch.StartNew();
    scheduler.EmergencyStop();
    var result = await queue.WaitAsync(TimeSpan.FromSeconds(25));
    var gone = await ProcessExited(child, TimeSpan.FromSeconds(5));
    var stored = (await db.ReadRecentRunsAsync(10)).SingleOrDefault(r => r.RunId == result.Runs.Single().RunId);
    var run = result.Runs.Single();
    return new("B3 队列紧急停止及持久化",
        gone && result.Status == QueueStatus.Cancelled && run.Outcome == RunOutcome.Cancelled &&
        run.Cleanup == CleanupState.Confirmed && result.NotStarted.Contains(GameId.StarRail) &&
        stored?.Outcome == RunOutcome.Cancelled && stored.Cleanup == CleanupState.Confirmed ? "PASS" : "FAIL",
        run.RunId, pid, new DateTimeOffset(startTime, TimeSpan.Zero), sw.ElapsedMilliseconds, run.Cleanup.ToString(), null,
        $"队列={result.Status}；下一任务未启动={result.NotStarted.Contains(GameId.StarRail)}；数据库结果={stored?.Outcome}；进程已退出={gone}");
});

await Check("B4 退出码不等于完成证据", async () =>
{
    var db = new LocalDatabase(Path.Combine(root, "evidence.db")); await db.InitializeAsync();
    var result = await new QueueScheduler(db, new WindowsProcessSupervisor()).RunAsync([
        new QueueItem(GameId.ZenlessZoneZero, new FakeEngineAdapter(GameId.ZenlessZoneZero, engine, "no-evidence"))
    ]).WaitAsync(TimeSpan.FromSeconds(20));
    var run = result.Runs.Single();
    return new("B4 退出码不等于完成证据",
        run.Outcome == RunOutcome.Indeterminate && run.Cleanup == CleanupState.Confirmed ? "PASS" : "FAIL",
        run.RunId, null, null, null, run.Cleanup.ToString(), 0, $"结果={run.Outcome}；原因={run.ReasonCode}");
});

await Check("B4a 引擎静默卡死后安全停止", async () =>
{
    var db = new LocalDatabase(Path.Combine(root, "stall.db")); await db.InitializeAsync();
    var options = new SchedulerOptions(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(12), new RunLimits(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2)));
    var sw = Stopwatch.StartNew();
    var result = await new QueueScheduler(db, new WindowsProcessSupervisor(), options).RunAsync([
        new QueueItem(GameId.Genshin, new FakeEngineAdapter(GameId.Genshin, engine, "hang-silent"))
    ]).WaitAsync(TimeSpan.FromSeconds(25));
    var run = result.Runs.Single();
    return new("B4a 引擎静默卡死后安全停止",
        run.Outcome == RunOutcome.Indeterminate && run.ReasonCode == ReasonCodes.Stalled &&
        run.Cleanup == CleanupState.Confirmed ? "PASS" : "FAIL",
        run.RunId, null, null, sw.ElapsedMilliseconds, run.Cleanup.ToString(), null,
        $"结果={run.Outcome}；原因={run.ReasonCode}；队列={result.Status}");
});

await Check("B4b 拒绝优雅停止后 Job 清理", async () =>
{
    var id = Guid.NewGuid();
    await using var group = await Start(id, "ignore-stop");
    using var child = Process.GetProcessById(group.RootProcessId);
    var sw = Stopwatch.StartNew();
    var sent = await group.SendInputLineAsync("stop", CancellationToken.None);
    var stillRunning = !await ProcessExited(child, TimeSpan.FromMilliseconds(400));
    var cleanup = await group.TerminateAllAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
    var gone = await ProcessExited(child, TimeSpan.FromSeconds(5));
    return new("B4b 拒绝优雅停止后 Job 清理",
        sent && stillRunning && cleanup == CleanupState.Confirmed && gone ? "PASS" : "FAIL",
        id, group.RootProcessId, group.RootStartTime, sw.ElapsedMilliseconds, cleanup.ToString(), null,
        $"优雅停止已发出={sent}；仍在运行={stillRunning}；强制清理后退出={gone}");
});

await Check("B4c 启动失败不产生进程", async () =>
{
    var id = Guid.NewGuid();
    var missing = Path.Combine(root, "missing.exe");
    try {
        await new WindowsProcessSupervisor().StartAsync(new ProcessLaunchSpec(missing, [], root), id, CancellationToken.None);
        return new("B4c 启动失败不产生进程", "FAIL", id, null, null, null, null, null, "不存在的文件却被启动");
    }
    catch (EngineStartException ex) {
        return new("B4c 启动失败不产生进程", ex.NothingStarted ? "PASS" : "FAIL", id, null, null, null,
            CleanupState.NotRequired.ToString(), null, $"NothingStarted={ex.NothingStarted}");
    }
});

await Check("B5 独立宿主崩溃及重启对账", async () =>
{
    var database = Path.Combine(root, "crash.db");
    using var host = Process.Start(new ProcessStartInfo(crashHost) {
        ArgumentList = { database, engine }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
    int pid;
    try { pid = int.Parse((await ReadUntilAsync(host.StandardOutput, "ENGINE-PID "))["ENGINE-PID ".Length..]); }
    finally { if (!host.HasExited) host.Kill(); await host.WaitForExitAsync(); }
    var gone = await PidExited(pid, TimeSpan.FromSeconds(10));
    var db = new LocalDatabase(database); await db.InitializeAsync();
    var recovered = (await db.ReconcileInterruptedRunsAsync(DateTimeOffset.UtcNow)).SingleOrDefault();
    var blocked = await new QueueScheduler(db, new WindowsProcessSupervisor()).RunAsync([
        new QueueItem(GameId.Genshin, new FakeEngineAdapter(GameId.Genshin, engine, "success"))
    ]).WaitAsync(TimeSpan.FromSeconds(15));
    return new("B5 独立宿主崩溃及重启对账",
        gone && recovered?.Outcome == RunOutcome.Indeterminate && recovered?.Cleanup == CleanupState.Unconfirmed &&
        blocked.Status == QueueStatus.Blocked && blocked.BlockReason == ReasonCodes.UnresolvedRuns ? "PASS" : "FAIL",
        recovered?.RunId, pid, null, null, recovered?.Cleanup.ToString(), null,
        $"测试宿主已退出；自有引擎已退出={gone}；重启结果={recovered?.Outcome}；队列={blocked.Status}；原因={blocked.BlockReason}");
});

results.Add(new("C 桌面 UAC 同意/拒绝", "NOT RUN", null, null, null, null, null, null, "需要用户单独授权及亲自操作"));
results.Add(new("D 官方发行包 CLI", "NOT RUN", null, null, null, null, null, null, "需要用户单独授权；真实命令未经验证"));
results.Add(new("B 迟到启动、未知清理和数据库写入故障注入", "NOT RUN", null, null, null, null, null, null, "现有 Windows CI 有定向单测；桌面探针尚未覆盖，阶段 B 完整验收须补测"));
var reportPath = Path.Combine(root, "报告.json");
await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
var summary = new StringBuilder($"VIvian 阶段 B 本机无害进程报告\n系统：{report.WindowsVersion}\n工具版本：{report.ToolVersion}\n用户安装包：{report.InstalledVersion}\n开始时间：{report.StartedAt:O}\n");
foreach (var item in results) summary.AppendLine($"{item.Status} {item.Name} | RunId={item.RunId} | PID={item.Pid} | 清理={item.Cleanup} | 耗时={item.ElapsedMs}ms | {item.Detail}");
summary.AppendLine("本工具没有操作用户现有 VIvian 程序或正式数据库。INCONCLUSIVE 和 FAIL 均不表示安全通过。");
await File.WriteAllTextAsync(Path.Combine(root, "摘要.txt"), summary.ToString(), new UTF8Encoding(false));
Console.WriteLine(summary);
Console.WriteLine("报告目录：" + root);
return results.Any(x => x.Status == "FAIL" || x.Status == "INCONCLUSIVE") ? 1 : 0;

Task<ISupervisedProcessGroup> Start(Guid id, string scenario) => new WindowsProcessSupervisor().StartAsync(
    new ProcessLaunchSpec(engine, ["--run-id", id.ToString("D"), "--scenario", scenario], AppContext.BaseDirectory), id, CancellationToken.None);

static async Task<string> ReadUntilAsync(StreamReader reader, string suffix)
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    while (await reader.ReadLineAsync(cts.Token) is { } line) if (line.Contains(suffix, StringComparison.Ordinal)) return line;
    throw new IOException("测试进程在就绪前退出。");
}
static async Task<string> ReadGroupUntilAsync(IAsyncEnumerable<string> lines, string suffix)
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    await foreach (var line in lines.WithCancellation(cts.Token)) if (line.Contains(suffix, StringComparison.Ordinal)) return line;
    throw new IOException("受监管进程在就绪前退出。");
}
static async Task<bool> ProcessExited(Process process, TimeSpan limit)
{
    try { await process.WaitForExitAsync(new CancellationTokenSource(limit).Token); return true; }
    catch (OperationCanceledException) { return false; }
}
static async Task<bool> PidExited(int pid, TimeSpan limit)
{
    try { using var process = Process.GetProcessById(pid); return await ProcessExited(process, limit); }
    catch (ArgumentException) { return true; }
}
static string SafeMessage(Exception ex) => ex is System.ComponentModel.Win32Exception w ? $"Win32 错误 {w.NativeErrorCode}" :
    ex is OperationCanceledException ? "操作超时" : "查看测试场景与退出状态；原始异常信息可能包含本机路径，已隐藏";

internal sealed record CaseResult(string Name, string Status, Guid? RunId, int? Pid, DateTimeOffset? CreatedAt,
    long? ElapsedMs, string? Cleanup, int? ExitCode, string Detail);
internal sealed record ProbeReport(string Name, string WindowsVersion, string ToolVersion, string InstalledVersion,
    DateTimeOffset StartedAt, List<CaseResult> Cases);
