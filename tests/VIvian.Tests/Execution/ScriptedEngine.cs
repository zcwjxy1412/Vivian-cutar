using System.Collections.Concurrent;
using System.Threading.Channels;
using VIvian.Core;
using VIvian.Core.Execution;

namespace VIvian.Tests.Execution;

/// <summary>How a scripted run reacts to stop requests.</summary>
public enum StopReaction { Honour, IgnoreGraceful, IgnoreAll }

/// <summary>In-memory engine used to drive the scheduler deterministically (no processes).</summary>
public sealed class ScriptedAdapter(GameId game) : IGameAdapter
{
    public GameId Game { get; } = game;
    public string RuleVersion => "test-1";
    public bool IsSimulation => true;

    public PreflightResult Preflight { get; set; } = PreflightResult.Ok();
    public Exception? StartException { get; set; }
    public TaskCompletionSource? StartGate { get; set; }
    /// <summary>Completed once StartAsync has been entered; <see cref="StartToken"/> is the token it received.</summary>
    public TaskCompletionSource StartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CancellationToken StartToken { get; private set; }
    /// <summary>A token linked to <see cref="StartToken"/>, the way a caller adding its own time limit would link it.</summary>
    public CancellationToken LinkedStartToken { get; private set; }
    public Func<ScriptedRun, Task>? Script { get; set; }
    public StopReaction StopReaction { get; set; } = StopReaction.Honour;
    public CleanupState ReconcileResult { get; set; } = CleanupState.Confirmed;
    public InMemoryJournal? Journal { get; set; }

    public int StartCount;
    public RunPhase? JournalPhaseAtStart;
    public ScriptedRun? LastRun;

    public Task<PreflightResult> PreflightAsync(RunContext context, CancellationToken ct) => Task.FromResult(Preflight);

    public async Task<IEngineRun> StartAsync(RunContext context, IProcessSupervisor supervisor, CancellationToken ct)
    {
        Interlocked.Increment(ref StartCount);
        JournalPhaseAtStart = Journal?.LastFor(context.RunId)?.Phase;
        StartToken = ct;
        LinkedStartToken = CancellationTokenSource.CreateLinkedTokenSource(ct).Token;
        StartEntered.TrySetResult();
        if (StartGate is { } gate)
        {
            await gate.Task.ConfigureAwait(false);
            if (ct.IsCancellationRequested)
                throw new EngineStartException(ReasonCodes.UserStop, nothingStarted: true, "Cancelled before process creation.");
        }
        if (StartException is not null) throw StartException;
        var run = new ScriptedRun(context.RunId, StopReaction, ReconcileResult);
        LastRun = run;
        if (Script is not null) _ = Task.Run(() => Script(run));
        return run;
    }
}

public sealed class ScriptedRun(Guid runId, StopReaction reaction, CleanupState reconcile) : IEngineRun
{
    private readonly Channel<RunEvent> _events = Channel.CreateUnbounded<RunEvent>();
    private readonly TaskCompletionSource<RunVerdict> _verdict = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentQueue<(StopMode Mode, DateTimeOffset At)> StopRequests { get; } = new();
    public Guid RunId { get; } = runId;
    public IAsyncEnumerable<RunEvent> Events => _events.Reader.ReadAllAsync();
    public bool Disposed { get; private set; }

    public void Emit(string message, Guid? runId = null) =>
        _events.Writer.TryWrite(new RunEvent(runId ?? RunId, DateTimeOffset.UtcNow, RunEventKind.Output, message));

    public void End(params EvidenceKind[] evidence) => EndWith(VerdictPolicy.Decide(RunId,
        evidence.Select(k => new Evidence(RunId, k, "test", DateTimeOffset.UtcNow))));

    public void EndWith(RunVerdict verdict)
    {
        _events.Writer.TryComplete();
        _verdict.TrySetResult(verdict);
    }

    public Task<RunVerdict> WaitForVerdictAsync(CancellationToken ct) => _verdict.Task.WaitAsync(ct);

    public Task<StopResult> RequestStopAsync(StopMode mode, CancellationToken ct)
    {
        StopRequests.Enqueue((mode, DateTimeOffset.UtcNow));
        var honour = reaction switch
        {
            StopReaction.Honour => true,
            StopReaction.IgnoreGraceful => mode == StopMode.Emergency,
            _ => false,
        };
        if (honour) End(EvidenceKind.CancelReported);
        return Task.FromResult(new StopResult(CleanupState.Pending, "requested"));
    }

    public Task<CleanupState> ReconcileAsync(CancellationToken ct) => Task.FromResult(reconcile);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

public sealed class InMemoryJournal : IRunJournal
{
    private readonly object _gate = new();
    public List<RunUpdate> Updates { get; } = [];
    public List<RunEvent> Events { get; } = [];
    /// <summary>When set, publishing an update in this phase throws.</summary>
    public RunPhase? FailOnPhase { get; set; }
    /// <summary>When set, publishing an update in this phase is slow (simulates disk latency).</summary>
    public (RunPhase Phase, TimeSpan Delay)? SlowPhase { get; set; }

    public async Task PublishAsync(RunUpdate update, CancellationToken ct)
    {
        if (update.Phase == FailOnPhase) throw new IOException("journal unavailable");
        if (SlowPhase is { } slow && slow.Phase == update.Phase) await Task.Delay(slow.Delay, ct);
        lock (_gate) Updates.Add(update);
    }

    /// <summary>When set, each event batch takes this long to store (simulates slow disks).</summary>
    public TimeSpan EventDelay { get; set; }

    public async Task AppendEventsAsync(IReadOnlyList<RunEvent> events, CancellationToken ct)
    {
        if (EventDelay > TimeSpan.Zero) await Task.Delay(EventDelay, ct);
        lock (_gate) Events.AddRange(events);
    }

    public int EventCount
    {
        get { lock (_gate) return Events.Count; }
    }

    public RunEvent[] EventsSnapshot()
    {
        lock (_gate) return [.. Events];
    }

    public bool AnyInPhase(RunPhase phase)
    {
        lock (_gate) return Updates.Any(u => u.Phase == phase);
    }

    public RunPhase? PhaseOf(Guid runId)
    {
        lock (_gate) return Updates.LastOrDefault(u => u.RunId == runId)?.Phase;
    }

    public int Unresolved { get; set; }
    public bool FailCount { get; set; }

    public Task<int> CountUnresolvedAsync(CancellationToken ct) =>
        FailCount ? throw new IOException("journal unavailable") : Task.FromResult(Unresolved);

    public RunUpdate? LastFor(Guid runId)
    {
        lock (_gate) return Updates.LastOrDefault(u => u.RunId == runId);
    }
}

public sealed class NoProcessSupervisor : IProcessSupervisor
{
    public Task<ISupervisedProcessGroup> StartAsync(ProcessLaunchSpec spec, Guid runId, CancellationToken ct) =>
        throw new NotSupportedException("Scripted tests never start processes.");
}
