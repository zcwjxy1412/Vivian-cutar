// A deliberately scriptable stand-in for an automation engine. It never touches games, input devices or the
// network; it only prints lines and creates/ends processes so VIvian's supervision can be tested end to end.
//
// Protocol: structured lines are "VIVIAN-FAKE|<runId>|<kind>|<text>", kind = progress | evidence | info.
// Evidence text is completion | failure | cancel. Any other line is free-form engine output.
// A line "stop" on standard input is a graceful stop request.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Principal;

Console.OutputEncoding = new UTF8Encoding(false);
var arguments = ParseArguments(args);
var runId = arguments.GetValueOrDefault("--run-id") ?? Guid.Empty.ToString();
var scenario = arguments.GetValueOrDefault("--scenario") ?? "success";
var stopRequested = new ManualResetEventSlim(false);

new Thread(() =>
{
    try
    {
        while (Console.In.ReadLine() is { } line)
            if (line.Trim() == "stop") stopRequested.Set();
    }
    catch (IOException) { }
}) { IsBackground = true }.Start();

void Emit(string kind, string text, string? forRun = null) => Console.WriteLine($"VIVIAN-FAKE|{forRun ?? runId}|{kind}|{text}");

// Waits the given time; returns true if a graceful stop arrived and this scenario honours it.
bool Pause(int milliseconds, bool honourStop = true) => stopRequested.Wait(milliseconds) && honourStop;

int HonouredStop()
{
    Emit("evidence", "cancel");
    return 3;
}

Emit("info", $"pid={Environment.ProcessId}");
switch (scenario)
{
    case "success":
        for (var i = 1; i <= 3; i++) { Emit("progress", $"step {i}/3"); if (Pause(100)) return HonouredStop(); }
        Emit("evidence", "completion");
        return 0;

    case "no-evidence":
        Emit("progress", "step 1/1");
        return 0;

    case "failure":
        Emit("progress", "step 1/2");
        Emit("evidence", "failure");
        return 1;

    case "noisy-success":
        Console.WriteLine("ERROR: 网络波动，已自动重试");
        Console.WriteLine("Traceback (most recent call last): recovered");
        Console.WriteLine("任务失败？不，已恢复。成功！");
        Emit("progress", "step 1/1");
        Emit("evidence", "completion");
        return 0;

    case "stale-evidence":
        // Leftovers of an earlier run, e.g. an old log tail. They must never count for this run.
        Emit("evidence", "completion", forRun: Guid.NewGuid().ToString());
        Emit("progress", "step 1/1");
        return 0;

    case "orphan-child":
    {
        var self = Environment.ProcessPath!;
        var child = Process.Start(new ProcessStartInfo(self) { ArgumentList = { "--scenario", "sleep-forever", "--run-id", runId }, UseShellExecute = false })!;
        Emit("info", $"child-pid={child.Id}");
        Emit("evidence", "completion");
        return 0; // exits while the child keeps running
    }

    case "hang-silent":
        Emit("progress", "starting");
        while (!Pause(60_000)) { }
        return HonouredStop();

    case "ignore-stop":
        while (true) { Emit("progress", "busy"); Pause(200, honourStop: false); }

    case "long-running":
        while (true) { Emit("progress", "working"); if (Pause(200)) return HonouredStop(); }

    case "echo-args":
        // Prints every argument it received, so tests can verify quoting survives the Windows command line.
        foreach (var argument in args) Emit("info", "arg=" + argument);
        Emit("evidence", "completion");
        return 0;

    case "probe-handle":
        // The supplied numeric value is a handle in the parent process. It must be invalid here.
        var candidate = new IntPtr(long.Parse(arguments["--probe-handle"]));
        Emit("info", "unrelated-handle-inherited=" + HandleProbe.GetHandleInformation(candidate, out _));
        Emit("evidence", "completion");
        return 0;

    case "probe-identity":
        Emit("info", "user-sid=" + WindowsIdentity.GetCurrent().User?.Value);
        Emit("info", "in-job=" + JobProbe.IsProcessInJob(IntPtr.Zero, IntPtr.Zero, out var insideJob));
        Emit("info", "job-member=" + insideJob);
        return 0;

    case "sleep-forever":
        Thread.Sleep(Timeout.Infinite);
        return 0;

    default:
        Console.Error.WriteLine($"unknown scenario {scenario}");
        return 64;
}

static Dictionary<string, string> ParseArguments(string[] args)
{
    var result = new Dictionary<string, string>();
    for (var i = 0; i + 1 < args.Length; i += 2) result[args[i]] = args[i + 1];
    return result;
}

internal static class HandleProbe
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetHandleInformation(IntPtr handle, out uint flags);
}

internal static class JobProbe
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsProcessInJob(IntPtr processHandle, IntPtr jobHandle,
        [MarshalAs(UnmanagedType.Bool)] out bool result);
}
