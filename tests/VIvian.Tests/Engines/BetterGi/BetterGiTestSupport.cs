using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Engines.BetterGi;

namespace VIvian.Tests.Engines.BetterGi;

/// <summary>A process group that never runs anything. Exit, active count and termination are scripted.</summary>
internal sealed class FakeGroup(Guid runId, int pid = 4242, DateTimeOffset? started = null) : ISupervisedProcessGroup
{
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Guid RunId { get; } = runId;
    public int RootProcessId { get; } = pid;
    public DateTimeOffset RootStartTime { get; } = started ?? DateTimeOffset.UtcNow.AddSeconds(-1);
    public IAsyncEnumerable<string> OutputLines => Empty();
    public int ActiveProcessCount { get; set; } = 1;
    public CleanupState TerminateResult { get; set; } = CleanupState.Confirmed;
    public bool TerminateThrows { get; set; }
    /// <summary>Exit code reported when VIvian terminates the group (a killed process does not exit 0).</summary>
    public int KilledExitCode { get; set; } = 1;
    public int TerminateCalls;
    public bool Disposed;

    public void Exit(int code)
    {
        ActiveProcessCount = 0;
        _exit.TrySetResult(code);
    }

    private static async IAsyncEnumerable<string> Empty([EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public Task<int> WaitForRootExitAsync(CancellationToken ct) => _exit.Task.WaitAsync(ct);
    public Task<bool> SendInputLineAsync(string line, CancellationToken ct) => Task.FromResult(false);
    public bool OwnsProcess(SafeProcessHandle process) => false;

    public Task<CleanupState> TerminateAllAsync(TimeSpan timeout, CancellationToken ct)
    {
        Interlocked.Increment(ref TerminateCalls);
        if (TerminateThrows) throw new InvalidOperationException("synthetic terminate failure");
        if (TerminateResult == CleanupState.Confirmed)
        {
            ActiveProcessCount = 0;
            _exit.TrySetResult(KilledExitCode);
        }
        return Task.FromResult(TerminateResult);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeSupervisor(Func<Guid, FakeGroup> create, Action? onStart = null) : IProcessSupervisor
{
    public ProcessLaunchSpec? LastSpec;
    public int Calls;
    public FakeGroup? Group;

    public Task<ISupervisedProcessGroup> StartAsync(ProcessLaunchSpec spec, Guid runId, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        onStart?.Invoke();
        LastSpec = spec;
        Group = create(runId);
        return Task.FromResult<ISupervisedProcessGroup>(Group);
    }
}

internal sealed class ScriptedLogSource : IBetterGiLogSource
{
    public Channel<string> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<string>();
    public int Opens;
    public bool Throw;

    public IBetterGiLogSubscription Open(string installRoot)
    {
        if (Throw) throw new IOException("synthetic log failure");
        Interlocked.Increment(ref Opens);
        return new Sub(Channel);
    }

    public void Write(IEnumerable<string> lines)
    {
        foreach (var line in lines) Channel.Writer.TryWrite(line);
    }

    private sealed class Sub(Channel<string> channel) : IBetterGiLogSubscription
    {
        public IAsyncEnumerable<string> Lines => channel.Reader.ReadAllAsync();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class FakeProbe(int running = 0) : IBetterGiProcessProbe
{
    public int Running { get; set; } = running;
    public int CountRunning(string programPath) => Running;
}

/// <summary>Synthetic BetterGI log records in the pinned file template. No real log content is used.</summary>
internal static class BgiLog
{
    public static IEnumerable<string> Record(int pid, DateTimeOffset instanceStart, string message, string level = "INF",
        string type = "Primary", string source = "BetterGenshinImpact.ViewModel.Pages.OneDragonFlowViewModel", string? exception = null)
    {
        yield return $"[12:00:00.000] [{level}] [{type}:S1:P{pid}:T{instanceStart.ToUnixTimeMilliseconds()}] {source}";
        yield return message;
        yield return exception ?? "";
        yield return "";
    }

    public static IEnumerable<string> SuccessfulRun(int pid, DateTimeOffset instanceStart, string config = "默认配置") =>
    [
        .. Record(pid, instanceStart, BetterGiMarkers.ActivatedPrefix + config),
        .. Record(pid, instanceStart, "启用任务总数量: 2"),
        .. Record(pid, instanceStart, "一条龙任务执行: 1/2"),
        .. Record(pid, instanceStart, "一条龙任务执行: 2/2"),
        .. Record(pid, instanceStart, BetterGiMarkers.CompletedLine),
    ];
}

internal sealed class BetterGiFixture : IAsyncDisposable
{
    public string Root { get; }
    public ScriptedLogSource Logs { get; } = new();
    public FakeProbe Probe { get; } = new();
    public BetterGiRunRegistry Registry { get; } = new();
    public FakeSupervisor Supervisor { get; }
    public RunContext Context { get; } = new(Guid.NewGuid(), Guid.NewGuid(), GameId.Genshin, DateTimeOffset.UtcNow);
    public bool LogOpenedBeforeLaunch;
    public int Pid { get; } = 4242;

    public BetterGiFixture(bool createInstall = true)
    {
        Root = Path.Combine(Path.GetTempPath(), "vivian-bgi-" + Guid.NewGuid().ToString("N"));
        if (createInstall)
        {
            Directory.CreateDirectory(Root);
            File.WriteAllBytes(Path.Combine(Root, "BetterGI.exe"), [0x4D, 0x5A]);
            Directory.CreateDirectory(Path.Combine(Root, "Assets"));
            Directory.CreateDirectory(Path.Combine(Root, "GameTask"));
        }
        Supervisor = new FakeSupervisor(id => new FakeGroup(id, Pid), () => LogOpenedBeforeLaunch = Logs.Opens > 0);
    }

    public static Func<string?, CancellationToken, Task<EngineReadinessReport>> Report(CompatibilityClass compatibility, string? program = null) =>
        (root, _) => Task.FromResult(new EngineReadinessReport(GameId.Genshin, "BetterGI", "v0.66.0", compatibility, "0.66.0",
            program ?? (root is null ? null : Path.Combine(root, "BetterGI.exe")), null,
            compatibility is CompatibilityClass.PendingValidation
                ? []
                : [new ReadinessCheck("version", "版本", CheckState.Failed, EngineErrorCategory.UnsupportedVersion, "synthetic")],
            DateTimeOffset.UtcNow));

    public BetterGiAdapter Adapter(BetterGiTaskRequest? request = null, bool? contractVerified = null,
        Func<string?, CancellationToken, Task<EngineReadinessReport>>? inspect = null) =>
        new(() => Root, request ?? BetterGiArguments.OneDragonSelected().Request!, Logs, Probe, Registry,
            new BetterGiRunOptions
            {
                LogQuietPeriod = TimeSpan.FromMilliseconds(100),
                LogDrainLimit = TimeSpan.FromSeconds(2),
                TerminateTimeout = TimeSpan.FromSeconds(1),
                CompletionContractVerifiedOverride = contractVerified,
            },
            TimeProvider.System, inspect);

    public async Task<(BetterGiRun Run, FakeGroup Group)> LaunchAsync(BetterGiAdapter adapter)
    {
        var run = (BetterGiRun)await adapter.LaunchAsync(Context, Supervisor, CancellationToken.None);
        return (run, Supervisor.Group!);
    }

    public ValueTask DisposeAsync()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        return ValueTask.CompletedTask;
    }
}
