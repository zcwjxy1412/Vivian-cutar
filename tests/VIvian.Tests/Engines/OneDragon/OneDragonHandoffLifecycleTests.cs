using VIvian.Core.Execution;
using VIvian.Infrastructure.Engines.OneDragon;
using VIvian.Tests.Execution;
using Xunit;

namespace VIvian.Tests.Engines.OneDragon;

public sealed class OneDragonHandoffLifecycleTests
{
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
