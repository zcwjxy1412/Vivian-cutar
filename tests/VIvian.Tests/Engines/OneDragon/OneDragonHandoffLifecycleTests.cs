using VIvian.Core.Execution;
using VIvian.Infrastructure.Engines.OneDragon;
using VIvian.Tests.Execution;
using Xunit;

namespace VIvian.Tests.Engines.OneDragon;

public sealed class OneDragonHandoffLifecycleTests
{
    [Fact]
    public async Task FailedRunRegistrationPreventsJournalAndEngineStart()
    {
        await using var fixture = new OneDragonFixture();
        var journal = new InMemoryJournal();
        await Assert.ThrowsAsync<IOException>(() => OneDragonAccountBatch.RunAsync(journal,
            fixture.Adapter(OneDragonArguments.OneDragon(instances: [2]).Request), fixture.Supervisor,
            fixture.Root, Guid.NewGuid(), new(456, 2, ["coffee"], "138****11"), null, default,
            beforeRun: (_, _) => throw new IOException("registration failed")));
        Assert.Null(fixture.Supervisor.Group);
        Assert.Empty(journal.Updates);
    }
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    public async Task EventAndTerminalJournalFailuresDoNotHideTheFirstError(bool failEvents, bool failDispose, bool failTerminal)
    {
        await using var fixture = new OneDragonFixture();
        var journal = new FaultJournal(failEvents, failTerminal);
        var batch = OneDragonAccountBatch.RunAsync(journal,
            fixture.Adapter(OneDragonArguments.OneDragon(instances: [2]).Request), fixture.Supervisor,
            fixture.Root, Guid.NewGuid(), new(456, 2, ["coffee"], "138****11"), null, default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (fixture.Supervisor.Group is null) await Task.Delay(10, timeout.Token);
        var group = fixture.Supervisor.Group;
        group.FailDispose = failDispose;
        var prefix = $"VIVIAN-ZZZ|{group.RunId:D}|";
        group.Write(prefix + "start|5151|2", prefix + "entered", prefix + "task_start|coffee",
            prefix + "task|coffee|success", prefix + "end|success");
        group.Exit(0);
        var error = await Record.ExceptionAsync(() => batch.WaitAsync(timeout.Token));
        Assert.Equal(failEvents ? "event journal unavailable" : "Synthetic supervisor disposal failure", error?.Message);
        Assert.DoesNotContain(journal.Store.Updates, update => update.Cleanup == CleanupState.Confirmed);
        Assert.Equal(failTerminal ? RunPhase.Running : RunPhase.Blocked, journal.Store.Updates.Last().Phase);
        // Early journal failure may stop before worker identity is observed, retaining the registry conservatively.
        if (failDispose) Assert.True(fixture.Registry.IsBusy(fixture.Root));
    }

    [Fact]
    public async Task NormalCancellationReturnsOnlyAfterOwnedCleanupAndRelease()
    {
        await using var fixture = new OneDragonFixture();
        var journal = new InMemoryJournal();
        using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childBatchId = Guid.NewGuid();
        Guid? registeredRun = null;
        var batch = OneDragonAccountBatch.RunAsync(journal,
            fixture.Adapter(OneDragonArguments.OneDragon(instances: [2]).Request), fixture.Supervisor,
            fixture.Root, childBatchId, new(456, 2, ["coffee"], "138****11"),
            message => { if (message.Contains("已接管")) entered.TrySetResult(); }, cancel.Token,
            beforeRun: (runId, _) =>
            {
                Assert.Null(fixture.Supervisor.Group);
                Assert.Empty(journal.Updates);
                registeredRun = runId;
                return Task.CompletedTask;
            });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (fixture.Supervisor.Group is null) await Task.Delay(10, timeout.Token);
        var group = fixture.Supervisor.Group;
        group.Write($"VIVIAN-ZZZ|{group.RunId:D}|start|5151|2");
        Assert.Equal(registeredRun, group.RunId);
        await entered.Task.WaitAsync(timeout.Token);
        Assert.True(fixture.Registry.IsBusy(fixture.Root));
        cancel.Cancel();
        var result = await batch.WaitAsync(timeout.Token);
        Assert.Equal(RunOutcome.Cancelled, result.Outcome);
        Assert.True(result.CleanupConfirmed);
        Assert.False(result.Success);
        Assert.True(group.Disposed);
        Assert.False(fixture.Registry.IsBusy(fixture.Root));
        Assert.Equal(CleanupState.Confirmed, journal.Updates.Last().Cleanup);
        Assert.All(journal.Updates, update => Assert.Equal(childBatchId, update.QueueId));
    }

    private sealed class FaultJournal(bool failEvents, bool failTerminal) : IRunJournal
    {
        public InMemoryJournal Store { get; } = new();
        public Task PublishAsync(RunUpdate update, CancellationToken ct) =>
            failTerminal && update.Phase == RunPhase.Blocked
                ? Task.FromException(new InvalidOperationException("terminal journal unavailable"))
                : Store.PublishAsync(update, ct);
        public Task AppendEventsAsync(IReadOnlyList<RunEvent> events, CancellationToken ct) =>
            failEvents ? Task.FromException(new InvalidDataException("event journal unavailable"))
                : Store.AppendEventsAsync(events, ct);
        public Task<int> CountUnresolvedAsync(CancellationToken ct) => Store.CountUnresolvedAsync(ct);
    }

    [Fact]
    public async Task EarlierFailureSurvivesDisposalFailureAndRecordsUnconfirmedCleanup()
    {
        await using var fixture = new OneDragonFixture();
        var journal = new InMemoryJournal { FailOnPhase = RunPhase.Running };
        var supervisor = new FailingDisposalSupervisor();
        var batch = OneDragonAccountBatch.RunAsync(journal,
            fixture.Adapter(OneDragonArguments.OneDragon(instances: [2]).Request), supervisor,
            fixture.Root, Guid.NewGuid(), new(456, 2, ["coffee"], "138****11"), null, default);
        var error = await Assert.ThrowsAsync<IOException>(() => batch.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("journal unavailable", error.Message);
        var last = journal.Updates.Last();
        Assert.Equal(RunPhase.Blocked, last.Phase);
        Assert.Equal(CleanupState.Unconfirmed, last.Cleanup);
        Assert.Equal(RunOutcome.Indeterminate, last.Outcome);
        Assert.True(fixture.Registry.IsBusy(fixture.Root));
    }

    private sealed class FailingDisposalSupervisor : IProcessSupervisor
    {
        public Task<ISupervisedProcessGroup> StartAsync(ProcessLaunchSpec spec, Guid runId, CancellationToken ct)
        {
            var group = new FakeLauncherGroup(runId) { FailDispose = true };
            group.Exit(1);
            return Task.FromResult<ISupervisedProcessGroup>(group);
        }
    }

    [Theory]
    [InlineData("owned", RunOutcome.Success, true)]
    [InlineData("foreign_run", RunOutcome.Indeterminate, false)]
    [InlineData("foreign_pid", RunOutcome.Indeterminate, false)]
    [InlineData("foreign_instance", RunOutcome.Indeterminate, false)]
    [InlineData("missing_task", RunOutcome.Indeterminate, true)]
    [InlineData("duplicate_task", RunOutcome.Indeterminate, true)]
    [InlineData("cleanup_failed", RunOutcome.Success, false)]
    public async Task BatchKeepsTaskEvidenceIdentityAndCleanupSeparate(string scenario, RunOutcome outcome, bool clean)
    {
        await using var fixture = new OneDragonFixture();
        var journal = new InMemoryJournal();
        var batchId = Guid.NewGuid();
        var progress = new List<string>();
        var batch = OneDragonAccountBatch.RunAsync(journal,
            fixture.Adapter(OneDragonArguments.OneDragon(instances: [2]).Request), fixture.Supervisor,
            fixture.Root, batchId, new(456, 2, ["coffee", "email"], "138****11"), progress.Add, default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (fixture.Supervisor.Group is null) await Task.Delay(10, timeout.Token);
        var group = fixture.Supervisor.Group;
        var prefix = $"VIVIAN-ZZZ|{(scenario == "foreign_run" ? Guid.NewGuid() : group.RunId):D}|";
        var pid = scenario == "foreign_pid" ? 999 : 5151;
        var instance = scenario == "foreign_instance" ? 1 : 2;
        group.Write(prefix + $"start|{pid}|{instance}", prefix + "entered", prefix + "task_start|coffee",
            prefix + "task|coffee|success");
        if (scenario != "missing_task") group.Write(prefix + "task|email|already_done");
        if (scenario == "duplicate_task") group.Write(prefix + "task|email|already_done");
        group.Write(prefix + "end|success");
        group.TerminateResult = CleanupState.Unconfirmed;
        group.Exit(0, workerStillRunning: scenario == "cleanup_failed");

        var result = await batch.WaitAsync(timeout.Token);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(outcome == RunOutcome.Success, result.Success);
        Assert.Equal(clean, result.CleanupConfirmed);
        Assert.Equal(group.RunId, result.RunId);
        var last = journal.LastFor(group.RunId)!;
        Assert.Equal(batchId, last.QueueId);
        Assert.Equal(clean ? RunPhase.Finished : RunPhase.Blocked, last.Phase);
        Assert.Equal(clean ? CleanupState.Confirmed : CleanupState.Unconfirmed, last.Cleanup);
        Assert.Equal(!clean, fixture.Registry.IsBusy(fixture.Root));
        Assert.All(journal.EventsSnapshot(), entry => Assert.Equal(group.RunId, entry.RunId));
        if (scenario == "owned")
        {
            Assert.Contains(progress, message => message.Contains("本轮完成"));
            Assert.Contains(progress, message => message.Contains("本轮跳过"));
            Assert.Contains(progress, message => message.Contains("执行完成 1，已完成跳过 1，失败 0，未确认 0"));
        }
    }

    [Fact]
    public async Task DisposalFailureCannotReturnACleanBatchResultOrPersistConfirmedCleanup()
    {
        await using var fixture = new OneDragonFixture();
        var journal = new InMemoryJournal();
        var adapter = fixture.Adapter(OneDragonArguments.OneDragon(instances: [2]).Request);
        var batch = OneDragonAccountBatch.RunAsync(journal, adapter, fixture.Supervisor,
            fixture.Root, Guid.NewGuid(), new(456, 2, ["coffee"], "138****11"), null, default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (fixture.Supervisor.Group is null) await Task.Delay(10, timeout.Token);
        var group = fixture.Supervisor.Group;
        group.FailDispose = true;
        var prefix = $"VIVIAN-ZZZ|{group.RunId:D}|";
        group.Write(prefix + "start|5151|2", prefix + "entered", prefix + "task_start|coffee",
            prefix + "task|coffee|success", prefix + "end|success");
        group.Exit(0);

        await Assert.ThrowsAsync<IOException>(() => batch.WaitAsync(timeout.Token));
        var last = journal.LastFor(group.RunId)!;
        Assert.Equal(RunOutcome.Success, last.Outcome); // Task result survives; cleanup is separate.
        Assert.Equal(RunPhase.Blocked, last.Phase);
        Assert.Equal(CleanupState.Unconfirmed, last.Cleanup);
        Assert.DoesNotContain(journal.Updates, update => update.Cleanup == CleanupState.Confirmed);
        Assert.True(fixture.Registry.IsBusy(fixture.Root));
    }
}
