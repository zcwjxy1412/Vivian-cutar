using Microsoft.Extensions.Time.Testing;
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
        public volatile bool Terminated;
        public volatile bool Disposed;
        public Func<Task<CleanupState>>? Terminate;
        public Task<int> WaitForRootExitAsync(CancellationToken ct) => Task.FromResult(0);
        public Task<bool> SendInputLineAsync(string line, CancellationToken ct) => Task.FromResult(false);
        public Task<CleanupState> TerminateAllAsync(TimeSpan timeout, CancellationToken ct)
        {
            Terminated = true;
            return Terminate?.Invoke() ?? Task.FromResult(CleanupState.Confirmed);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class ScriptedSupervisor(Func<Guid, Task<ISupervisedProcessGroup>> start) : IProcessSupervisor
    {
        public int Calls;
        public Task<ISupervisedProcessGroup> StartAsync(ProcessLaunchSpec spec, Guid runId, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return start(runId);
        }
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
            var revoke = Task.Run(() => scope.RevokeAsync(TimeSpan.FromSeconds(5)));
            Assert.True(SpinWait.SpinUntil(() => scope.IsRevoked, TimeSpan.FromSeconds(5)));
            release.Set();
            // The launch completed after revocation: the adapter never receives the group, and is told that
            // something may have started.
            var ex = await Assert.ThrowsAsync<EngineStartException>(() => started.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(ex.NothingStarted);
            Assert.True(await revoke.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(inner.Started!.Terminated);
            Assert.True(inner.Started.Disposed);
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

    // H-30-1: an emergency-stop callback revokes synchronously; it must not wait behind a slow launch.
    [Fact]
    public async Task RevokeLaunchesDoesNotWaitForALaunchInProgress()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var inner = new ContendedSupervisor(entered, release);
        var id = Guid.NewGuid();
        var scope = new RunProcessScope(inner, id);
        var started = Task.Run(() => scope.StartAsync(Spec, id, CancellationToken.None));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var revoke = Task.Run(scope.RevokeLaunches);
            await revoke.WaitAsync(TimeSpan.FromSeconds(5)); // would deadlock if revoke waited for the launch
            Assert.True(scope.IsRevoked);
            Assert.False(started.IsCompleted);
        }
        finally { release.Set(); }
        var ex = await Assert.ThrowsAsync<EngineStartException>(() => started.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(ex.NothingStarted);
        Assert.False(inner.Started!.Terminated); // not yet: RevokeAsync owns termination
        Assert.True(await scope.RevokeAsync(TimeSpan.FromSeconds(5)));
        Assert.True(inner.Started.Terminated);
    }

    // H-30-1: termination racing registration. A launch still running when the bounded wait ends is never
    // reported as cleaned up.
    [Fact]
    public async Task RevokeAsyncDuringAStuckLaunchIsUnconfirmedAndBounded()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var inner = new ContendedSupervisor(entered, release);
        var time = new FakeTimeProvider();
        var id = Guid.NewGuid();
        var scope = new RunProcessScope(inner, id, time);
        var started = Task.Run(() => scope.StartAsync(Spec, id, default));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var revoke = scope.RevokeAsync(TimeSpan.FromSeconds(10));
            Assert.False(revoke.IsCompleted);
            time.Advance(TimeSpan.FromSeconds(11));
            Assert.False(await revoke.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { release.Set(); }
        await Assert.ThrowsAsync<EngineStartException>(() => started.WaitAsync(TimeSpan.FromSeconds(5)));
        // The late group was still registered, so a later revoke terminates it.
        Assert.True(await scope.RevokeAsync(TimeSpan.FromSeconds(5)));
        Assert.True(inner.Started!.Terminated);
    }

    // H-30-1: Job assignment failed and the suspended process could not be confirmed gone.
    [Fact]
    public async Task SupervisorFailureThatMayHaveStartedSomethingLatchesUnsafe()
    {
        var id = Guid.NewGuid();
        var scope = new RunProcessScope(new ScriptedSupervisor(_ =>
            throw new EngineStartException(ReasonCodes.StartFailed, nothingStarted: false, "job assignment failed")), id);
        var ex = await Assert.ThrowsAsync<EngineStartException>(() => scope.StartAsync(Spec, id, default));
        Assert.False(ex.NothingStarted);
        Assert.True(scope.HasUnsafeLaunch);
        Assert.False(await scope.RevokeAsync(TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedSupervisorErrorsAreNeverNothingStarted(bool faultedTask)
    {
        var id = Guid.NewGuid();
        var scope = new RunProcessScope(new ScriptedSupervisor(_ => faultedTask
            ? Task.FromException<ISupervisedProcessGroup>(new InvalidOperationException("win32"))
            : throw new InvalidOperationException("win32")), id);
        var ex = await Assert.ThrowsAsync<EngineStartException>(() => scope.StartAsync(Spec, id, default));
        Assert.False(ex.NothingStarted);
        Assert.True(scope.HasUnsafeLaunch);
        Assert.False(await scope.RevokeAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task ProvenNothingStartedFromAFaultedTaskStaysSafe()
    {
        var id = Guid.NewGuid();
        var scope = new RunProcessScope(new ScriptedSupervisor(_ => Task.FromException<ISupervisedProcessGroup>(
            new EngineStartException(ReasonCodes.StartFailed, nothingStarted: true, "missing exe"))), id);
        var ex = await Assert.ThrowsAsync<EngineStartException>(() => scope.StartAsync(Spec, id, default));
        Assert.True(ex.NothingStarted);
        Assert.False(scope.HasUnsafeLaunch);
        Assert.True(await scope.RevokeAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task ForeignRunIdGroupIsQuarantinedAndUnsafe()
    {
        var id = Guid.NewGuid();
        var foreign = new Group(Guid.NewGuid());
        var scope = new RunProcessScope(new ScriptedSupervisor(_ => Task.FromResult<ISupervisedProcessGroup>(foreign)), id);
        await Assert.ThrowsAsync<EngineStartException>(() => scope.StartAsync(Spec, id, default));
        Assert.True(scope.HasUnsafeLaunch);
        Assert.False(await scope.RevokeAsync(TimeSpan.FromSeconds(5)));
        Assert.True(foreign.Terminated);
        Assert.True(foreign.Disposed);
    }

    [Fact]
    public async Task AsynchronousLaunchIsRejectedAndTheLateGroupIsTerminated()
    {
        var id = Guid.NewGuid();
        var late = new TaskCompletionSource<ISupervisedProcessGroup>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scope = new RunProcessScope(new ScriptedSupervisor(_ => late.Task), id);
        var ex = await Assert.ThrowsAsync<EngineStartException>(() => scope.StartAsync(Spec, id, default));
        Assert.False(ex.NothingStarted);
        Assert.True(scope.HasUnsafeLaunch);
        var group = new Group(id);
        late.SetResult(group);
        Assert.False(await scope.RevokeAsync(TimeSpan.FromSeconds(5))); // unsafe stays latched
        Assert.True(group.Terminated);
        Assert.True(group.Disposed);
    }

    // H-30-2: cleanup that hangs, reports Unconfirmed or throws is bounded and never Confirmed.
    [Theory]
    [InlineData("hang")]
    [InlineData("unconfirmed")]
    [InlineData("throws")]
    public async Task UnprovenTerminationIsNeverConfirmed(string mode)
    {
        var id = Guid.NewGuid();
        var time = new FakeTimeProvider();
        var group = new Group(id)
        {
            Terminate = mode switch
            {
                "hang" => () => new TaskCompletionSource<CleanupState>().Task,
                "unconfirmed" => () => Task.FromResult(CleanupState.Unconfirmed),
                _ => () => throw new InvalidOperationException("query failed"),
            },
        };
        var scope = new RunProcessScope(new ScriptedSupervisor(_ => Task.FromResult<ISupervisedProcessGroup>(group)), id, time);
        await scope.StartAsync(Spec, id, default);
        var revoke = scope.RevokeAsync(TimeSpan.FromSeconds(10));
        if (mode == "hang")
        {
            Assert.False(revoke.IsCompleted);
            time.Advance(TimeSpan.FromSeconds(11));
        }
        Assert.False(await revoke.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(group.Disposed); // the Job handle is still released (kill-on-close backup)
        await Assert.ThrowsAsync<EngineStartException>(() => scope.StartAsync(Spec, id, default));
    }

    // An adapter that ignores cancellation (passes CancellationToken.None) is still bound by revocation.
    [Fact]
    public async Task RevocationAppliesEvenWhenTheAdapterPassesNoToken()
    {
        var id = Guid.NewGuid();
        var inner = new CountingSupervisor();
        var scope = new RunProcessScope(inner, id);
        scope.RevokeLaunches();
        var ex = await Assert.ThrowsAsync<EngineStartException>(() => scope.StartAsync(Spec, id, CancellationToken.None));
        Assert.True(ex.NothingStarted);
        Assert.Equal(0, inner.Calls);
        Assert.True(await scope.RevokeAsync(TimeSpan.FromSeconds(1)));
    }
}
