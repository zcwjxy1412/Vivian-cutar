// Test helper that stands in for the VIvian process during the crash scenario: it starts a queue with a
// long-running FakeEngine through the real supervisor and SQLite journal, reports the engine PID, and then
// waits to be killed by the test. It never stops anything itself.
using VIvian.Core;
using VIvian.Core.Execution;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Processes;
using VIvian.Infrastructure.Simulation;

if (!OperatingSystem.IsWindows()) return 2;
if (args.Length != 2) { Console.Error.WriteLine("usage: VIvian.CrashHost <database> <fake-engine.exe>"); return 64; }

var database = new LocalDatabase(args[0]);
await database.InitializeAsync();
var scheduler = new QueueScheduler(database, new WindowsProcessSupervisor());
scheduler.EventReceived += e =>
{
    if (e.Message.StartsWith("模拟引擎已启动：PID ", StringComparison.Ordinal))
        Console.WriteLine("ENGINE-PID " + e.Message.Split(' ', '，')[1]);
};
var adapter = new FakeEngineAdapter(GameId.ZenlessZoneZero, args[1], "long-running");
await scheduler.RunAsync([new QueueItem(GameId.ZenlessZoneZero, adapter)]);
return 0;
