using VIvian.Core;
using VIvian.Core.Execution;
using Xunit;

namespace VIvian.Tests.Execution;

public sealed class RunProcessScopeTests
{
    private sealed class Group(Guid id) : ISupervisedProcessGroup
    {
        public Guid RunId => id;
        public int RootProcessId => 1;
        public DateTimeOffset RootStartTime => DateTimeOffset.UtcNow;
        public IAsyncEnumerable<string> OutputLines => Empty();
        private static async IAsyncEnumerable<string> Empty() { await Task.CompletedTask; yield break; }
        public int ActiveProcessCount => Terminated ? 0 : 1;
        public bool Terminated;
        public Task<int> WaitForRootExitAsync(CancellationToken ct) => Task.FromResult(0);
        public Task<bool> SendInputLineAsync(string line, CancellationToken ct) => Task.FromResult(false);
        public Task<CleanupState> TerminateAllAsync(TimeSpan timeout, CancellationToken ct)
        {
            Terminated = true;
            return Task.FromResult(CleanupState.Confirmed);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ContendedSupervisor(ManualResetEventSlim entered, ManualResetEventSlim release) : IProcessSupervisor
    {
        public Group? Started;
        public Task<ISupervisedProcessGroup> StartAsync(ProcessLaunchSpec spec, Guid runId, CancellationToken ct)
        {
            entered.Set();
            release.Wait();
            Started = new Group(runId);
            return Task.FromResult<ISupervisedProcessGroup>(Started);
        }
    }

    [Fact]
    public async Task RevokeConcurrentWithLaunchTerminatesRegisteredGroupAndRejectsLateRetry()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var inner = new ContendedSupervisor(entered, release);
        var id = Guid.NewGuid();
        var scope = new RunProcessScope(inner, id);
        var started = Task.Run(() => scope.StartAsync(Spec, id, default));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var revoke = Task.Run(() => scope.RevokeAsync(TimeSpan.FromSeconds(1)));
            release.Set();
            await started.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await revoke.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(inner.Started!.Terminated);
            await Assert.ThrowsAsync<EngineStartException>(() => scope.StartAsync(Spec, id, default));
        }
        finally { release.Set(); }
    }
    private sealed class UnreviewedRealAdapter : IGameAdapter
    {
        public GameId Game => GameId.Genshin;
        public string RuleVersion => "unreviewed";
        public bool IsSimulation => false;
        public int Starts;
        public Task<PreflightResult> PreflightAsync(RunContext context, CancellationToken ct) =>
            Task.FromResult(PreflightResult.Ok());
        public Task<IEngineRun> StartAsync(RunContext context, IProcessSupervisor supervisor, CancellationToken ct)
        {
            Interlocked.Increment(ref Starts);
            throw new InvalidOperationException("real adapter must never be entered");
        }
    }

    [Fact]
    public async Task UnreviewedRealAdapterIsBlockedBeforePreflightAndLaunch()
    {
        var adapter = new UnreviewedRealAdapter();
        var result = await new QueueScheduler(new InMemoryJournal(), new NoProcessSupervisor())
            .RunAsync([new QueueItem(GameId.Genshin, adapter)]);
        Assert.Equal(QueueStatus.Blocked, result.Status);
        Assert.Equal(ReasonCodes.RealAdapterUnverified, result.BlockReason);
        Assert.Equal(0, adapter.Starts);
    }
    private sealed class CountingSupervisor : IProcessSupervisor
    {
        public int Calls;
        public Task<ISupervisedProcessGroup> StartAsync(ProcessLaunchSpec spec, Guid runId, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            throw new EngineStartException(ReasonCodes.StartFailed, nothingStarted: true, "synthetic launch rejected");
        }
    }

    private static readonly ProcessLaunchSpec Spec = new("unused.exe", [], ".");

    [Fact]
    public async Task ARunIdCanAttemptLaunchOnlyOnce()
    {
        var id = Guid.NewGuid();
        var inner = new CountingSupervisor();
        var scope = new RunProcessScope(inner, id);
        await Assert.ThrowsAsync<EngineStartException>(() => scope.StartAsync(Spec, id, default));
        await Assert.ThrowsAsync<EngineStartException>(() => scope.StartAsync(Spec, id, default));
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task RevokedAndMismatchedRunIdsNeverReachSupervisor()
    {
        var id = Guid.NewGuid();
        var inner = new CountingSupervisor();
        var scope = new RunProcessScope(inner, id);
        await Assert.ThrowsAsync<EngineStartException>(() => scope.StartAsync(Spec, Guid.NewGuid(), default));
        scope.RevokeLaunches();
        await Assert.ThrowsAsync<EngineStartException>(() => scope.StartAsync(Spec, id, CancellationToken.None));
        Assert.Equal(0, inner.Calls);
    }
}
