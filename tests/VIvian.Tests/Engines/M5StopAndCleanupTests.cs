using System.Threading.Channels;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Engines;
using VIvian.Infrastructure.Engines.Cli;
using Xunit;

namespace VIvian.Tests.Engines;

/// <summary>Immediate stop through March7th's control file, and exit verification from its own run records.</summary>
public sealed class M5StopAndCleanupTests : IDisposable
{
    private const string RunId = "20261107-101500-abcd";
    private const double Created = 1794030000.5;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vivian-m7a-stop-" + Guid.NewGuid().ToString("N"));
    private readonly string _runs;
    private readonly Func<int, (bool, double?)> _originalObserve = March7thUpstreamActivity.Observe;

    public M5StopAndCleanupTests()
    {
        _runs = Directory.CreateDirectory(Path.Combine(_root, "logs", "runs")).FullName;
    }

    public void Dispose()
    {
        March7thUpstreamActivity.Observe = _originalObserve;
        March7thRun.WorkerExitWait = TimeSpan.FromSeconds(65);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private void Record(string id, string status, int pid = 4242, string? controlFile = null, string task = "routine")
    {
        var control = (controlFile ?? Path.Combine(_runs, id + ".control")).Replace("\\", "\\\\");
        File.WriteAllText(Path.Combine(_runs, id + ".json"),
            $"{{\"run_id\":\"{id}\",\"status\":\"{status}\",\"task\":\"{task}\",\"pid\":{pid},\"pid_create_time\":{Created}," +
            $"\"started_at\":\"{DateTimeOffset.Now:O}\",\"control_file\":\"{control}\"}}");
    }

    private void Active(string id, int pid = 4242) => File.WriteAllText(Path.Combine(_runs, "active.json"),
        $"{{\"run_id\":\"{id}\",\"pid\":{pid},\"pid_create_time\":{Created},\"task\":\"routine\",\"started_at\":\"{DateTimeOffset.Now:O}\"}}");

    [Fact]
    public void ActivityFollowsExactPidAndCreationTime()
    {
        Assert.Equal(UpstreamActivityState.Idle, March7thUpstreamActivity.Inspect(Path.Combine(_root, "missing")).State);
        Record(RunId, "running");
        Active(RunId);
        March7thUpstreamActivity.Observe = _ => (true, Created);
        var busy = March7thUpstreamActivity.Inspect(_root);
        Assert.Equal(UpstreamActivityState.Busy, busy.State);
        Assert.Equal(RunId, busy.RunId);
        // Same PID, different start time: reused by another program, so the worker is gone.
        March7thUpstreamActivity.Observe = _ => (true, Created + 600);
        Assert.Equal(UpstreamActivityState.Idle, March7thUpstreamActivity.Inspect(_root).State);
        // Worker vanished without finishing its record (window closed): gone is gone.
        March7thUpstreamActivity.Observe = _ => (false, null);
        Assert.Equal(UpstreamActivityState.Idle, March7thUpstreamActivity.Inspect(_root).State);
        Assert.Equal(WorkerExitState.Exited, March7thUpstreamActivity.WorkerState(_root, RunId));
        // A process that cannot be inspected might be the worker: never guessed as idle.
        March7thUpstreamActivity.Observe = _ => (true, null);
        Assert.Equal(UpstreamActivityState.Unknown, March7thUpstreamActivity.Inspect(_root).State);
        Assert.Equal(WorkerExitState.Unknown, March7thUpstreamActivity.WorkerState(_root, "../../evil"));
    }

    [Fact]
    public void FinishedRecordsAreNotChecked()
    {
        Record(RunId, "success");
        var observed = 0;
        March7thUpstreamActivity.Observe = _ => { observed++; return (true, Created); };
        Assert.Equal(UpstreamActivityState.Idle, March7thUpstreamActivity.Inspect(_root).State);
        Assert.Equal(0, observed);
    }

    [Fact]
    public void PauseAndResumeUseOnlyExactRunControlFile()
    {
        Record(RunId, "running");
        var control = March7thUpstreamActivity.TrySendControl(_root, RunId, "pause");
        Assert.Equal(Path.Combine(_runs, RunId + ".control"), control);
        Assert.Equal("{\"command\": \"pause\"}", File.ReadAllText(control!));
        Assert.Equal(control, March7thUpstreamActivity.TrySendControl(_root, RunId, "resume"));
        Assert.Equal("{\"command\": \"resume\"}", File.ReadAllText(control!));
        Assert.Null(March7thUpstreamActivity.TrySendControl(_root, "current", "pause"));
        Assert.Null(March7thUpstreamActivity.TrySendControl(_root, RunId, "launch"));
    }

    [Fact]
    public void StopIsWrittenOnlyIntoThisInstallationsRunsFolder()
    {
        Record(RunId, "running", controlFile: Path.Combine(Path.GetTempPath(), "elsewhere.control"));
        var control = March7thUpstreamActivity.TrySendStop(_root, RunId);
        Assert.Equal(Path.Combine(_runs, RunId + ".control"), control);
        Assert.Equal("{\"command\": \"stop\"}", File.ReadAllText(control!));
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "elsewhere.control")));
        Assert.Empty(Directory.GetFiles(_runs, "*.tmp"));
        Assert.Null(March7thUpstreamActivity.ReadControlState(control!));
        File.WriteAllText(control + ".state", "{\"state\": \"stopping\"}");
        Assert.Equal("stopping", March7thUpstreamActivity.ReadControlState(control!));
        Assert.Null(March7thUpstreamActivity.TrySendStop(_root, "current"));
    }

    [Fact]
    public void ActiveRunIsAdoptedOnlyForThisTaskAndStartedAfterUs()
    {
        Active(RunId);
        Assert.Equal(RunId, March7thUpstreamActivity.ActiveRunStartedSince(_root, "routine", DateTimeOffset.Now.AddMinutes(-1)));
        Assert.Null(March7thUpstreamActivity.ActiveRunStartedSince(_root, "routine", DateTimeOffset.Now.AddMinutes(1)));
        Assert.Null(March7thUpstreamActivity.ActiveRunStartedSince(_root, "daily", DateTimeOffset.Now.AddMinutes(-1)));
    }

    [Fact]
    public async Task EmergencyStopUsesControlFileAndConfirmsWhenWorkerIsGone()
    {
        var group = new Group();
        var supervisor = new CountingSupervisor();
        var run = new March7thRun(Guid.NewGuid(), group, supervisor, Path.Combine(_root, "m7a.exe"), _root, "routine");
        Record(RunId, "running");
        Active(RunId);
        March7thUpstreamActivity.Observe = _ => (true, Created);
        // The worker: acknowledges as soon as the stop command appears, as upstream's pause controller does.
        var worker = Task.Run(async () =>
        {
            var control = Path.Combine(_runs, RunId + ".control");
            for (var i = 0; i < 200; i++)
            {
                if (File.Exists(control) && File.ReadAllText(control).Contains("stop"))
                {
                    File.WriteAllText(control + ".state", "{\"state\": \"stopping\"}");
                    return;
                }
                await Task.Delay(20);
            }
        });
        var started = DateTimeOffset.UtcNow;
        var stop = await run.RequestStopAsync(StopMode.Emergency, CancellationToken.None);
        await worker;
        Assert.Equal(CleanupState.Pending, stop.Cleanup);
        Assert.Equal(ReasonCodes.UserStop, stop.ReasonCode);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(3));
        Assert.Equal(0, supervisor.Starts); // no control process had to start first
        // Worker exits at its next checkpoint; the entry follows it. No final record was printed.
        March7thUpstreamActivity.Observe = _ => (false, null);
        group.Exit();
        Assert.Equal(CleanupState.Confirmed, await run.ReconcileAsync(CancellationToken.None));
        await run.DisposeAsync();
    }

    [Fact]
    public async Task PauseAndResumeWaitForWorkerAcknowledgement()
    {
        var group = new Group();
        var run = new March7thRun(Guid.NewGuid(), group, new CountingSupervisor(),
            Path.Combine(_root, "m7a.exe"), _root, "routine");
        Record(RunId, "running"); Active(RunId);
        March7thUpstreamActivity.Observe = _ => (true, Created);
        var control = Path.Combine(_runs, RunId + ".control");
        var worker = Task.Run(async () =>
        {
            var last = "";
            for (var i = 0; i < 200; i++)
            {
                var command = File.Exists(control) ? File.ReadAllText(control) : "";
                if (command != last && command.Contains("pause"))
                { File.WriteAllText(control + ".state", "{\"state\":\"paused\"}"); last = command; }
                else if (command != last && command.Contains("resume"))
                { File.WriteAllText(control + ".state", "{\"state\":\"running\"}"); return; }
                await Task.Delay(20);
            }
        });
        Assert.True(await run.SetPausedAsync(true, CancellationToken.None));
        Assert.True(run.IsPaused);
        Assert.True(await run.SetPausedAsync(false, CancellationToken.None));
        Assert.False(run.IsPaused);
        await worker;
        group.Exit();
        await run.DisposeAsync();
    }

    [Fact]
    public async Task ResolutionErrorsTripOnlyThisRunsStopControl()
    {
        var group = new Group();
        var run = new March7thRun(Guid.NewGuid(), group, new CountingSupervisor(),
            Path.Combine(_root, "m7a.exe"), _root, "routine", circuitOptions: new(2, TimeSpan.FromSeconds(30)));
        Record(RunId, "running"); Active(RunId);
        March7thUpstreamActivity.Observe = _ => (true, Created);
        var log = Path.Combine(_runs, RunId + ".log");
        File.WriteAllText(log, "游戏分辨率获取失败\n游戏分辨率获取失败\n");
        var control = Path.Combine(_runs, RunId + ".control");
        var stopped = false;
        for (var i = 0; i < 100; i++)
        {
            if (File.Exists(control) && File.ReadAllText(control).Contains("stop")) { stopped = true; break; }
            await Task.Delay(50);
        }
        Assert.True(stopped);
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), RunId + ".control")));
        group.Exit();
        await run.DisposeAsync();
    }

    [Fact]
    public async Task WorkerStillAliveKeepsCleanupUnconfirmed()
    {
        March7thRun.WorkerExitWait = TimeSpan.FromMilliseconds(300);
        var group = new Group();
        var run = new March7thRun(Guid.NewGuid(), group, new CountingSupervisor(), Path.Combine(_root, "m7a.exe"), _root, "routine");
        Record(RunId, "running");
        Active(RunId);
        March7thUpstreamActivity.Observe = _ => (true, Created);
        group.Exit();
        Assert.Equal(CleanupState.Unconfirmed, await run.ReconcileAsync(CancellationToken.None));
        await run.DisposeAsync();
    }

    private sealed class Group : ISupervisedProcessGroup
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Exit() { _lines.Writer.TryComplete(); _exit.TrySetResult(0); }
        public Guid RunId { get; } = Guid.NewGuid();
        public int RootProcessId => 1;
        public DateTimeOffset RootStartTime => DateTimeOffset.UtcNow;
        public int ActiveProcessCount => 0;
        public bool OwnsProcess(Microsoft.Win32.SafeHandles.SafeProcessHandle process) => false;
        public IAsyncEnumerable<string> OutputLines => _lines.Reader.ReadAllAsync();
        public Task<int> WaitForRootExitAsync(CancellationToken ct) => _exit.Task.WaitAsync(ct);
        public Task<bool> SendInputLineAsync(string line, CancellationToken ct) => Task.FromResult(false);
        public Task<CleanupState> TerminateAllAsync(TimeSpan timeout, CancellationToken ct) { Exit(); return Task.FromResult(CleanupState.Confirmed); }
        public ValueTask DisposeAsync() { Exit(); return ValueTask.CompletedTask; }
    }

    private sealed class CountingSupervisor : IProcessSupervisor
    {
        public int Starts;
        public Task<ISupervisedProcessGroup> StartAsync(ProcessLaunchSpec spec, Guid runId, CancellationToken ct)
        {
            Interlocked.Increment(ref Starts);
            throw new InvalidOperationException("no process may be started in this test");
        }
    }
}
