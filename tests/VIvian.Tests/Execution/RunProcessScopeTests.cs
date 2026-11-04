using VIvian.Core;
using VIvian.Core.Execution;
using Xunit;

namespace VIvian.Tests.Execution;

public sealed class RunProcessScopeTests
{
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
