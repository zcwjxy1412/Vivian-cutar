using System.Text.Json;
using VIvian.Core;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Accounts;
using VIvian.Infrastructure;
using Xunit;

namespace VIvian.Tests.Accounts;

public sealed class PersistentExecutionPlanCoordinatorTests
{
    private static PreparedExecutionPlan Plan() => new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1),
        Enum.GetValues<GameId>().Take(3).Select((game, i) => new ExecutionPlanItem(i, game, Guid.NewGuid(),
            "synthetic", "routine", "revision", 1, "saved_account_settings", "synthetic")).ToArray());
    private static MultiAccountOutcome Success(Guid batch) => new(batch, true, 1, 0, "done",
        HostCleanupConfirmed: true, GameCleanupConfirmed: true, Outcome: RunOutcome.Success,
        EngineCleanupConfirmed: true, ConfigurationRestored: true);

    [Fact]
    public async Task RealStoreReopensExactReceiptWithoutReplayingCompletedWork()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vivian-plan-coordinator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "test.db");
            var store = new LocalDatabase(path);
            await store.InitializeAsync();
            var calls = new List<Guid>();
            var plan = Plan();
            var coordinator = new PersistentExecutionPlanCoordinator(store, async (item, register, ct) =>
            {
                var runId = Guid.NewGuid();
                await register(runId, ct);
                var durable = await new LocalDatabase(path).ReadAsync(plan.PlanId, ct);
                Assert.Contains(runId, durable!.Items[item.Item.Ordinal].RunIds);
                calls.Add(item.ChildBatchId);
                return Success(item.ChildBatchId);
            });
            await coordinator.SubmitAsync(plan);
            await coordinator.WhenIdleAsync();
            var reopened = new LocalDatabase(path);
            var receipt = await reopened.ReadAsync(plan.PlanId);
            Assert.Equal(ExecutionPlanState.Succeeded, receipt!.State);
            Assert.Equal(receipt.Items.Select(i => i.ChildBatchId), calls);
            Assert.Null(await reopened.ReadAsync(Guid.NewGuid()));
            var nextHost = new PersistentExecutionPlanCoordinator(reopened, (_, _, _) => throw new Exception("replay"));
            Assert.Equal(ExecutionPlanDisposition.Existing, (await nextHost.SubmitAsync(plan)).Disposition);
            await nextHost.WhenIdleAsync();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TestDirectories.DeleteWithRetry(directory);
        }
    }

    [Fact]
    public async Task ThrowingCancellationCallbackDoesNotLeaveQueuedPlansUncancelled()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vivian-plan-stop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new LocalDatabase(Path.Combine(directory, "test.db"));
            await store.InitializeAsync();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var localStops = 0;
            var coordinator = new PersistentExecutionPlanCoordinator(store, async (item, register, ct) =>
            {
                calls++;
                await register(Guid.NewGuid(), ct);
                using var callback = ct.Register(() => throw new IOException("cancel callback failed"));
                entered.TrySetResult();
                await release.Task;
                return Success(item.ChildBatchId);
            }, () => localStops++);
            await coordinator.SubmitAsync(Plan());
            await entered.Task;
            var next = Plan();
            await coordinator.SubmitAsync(next);
            Assert.Throws<AggregateException>(() => coordinator.StopAll());
            release.SetResult();
            await coordinator.WhenIdleAsync();
            Assert.Equal(1, calls);
            Assert.Equal(1, localStops);
            Assert.Equal(ExecutionPlanState.Stopped, (await store.ReadAsync(next.PlanId))!.State);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TestDirectories.DeleteWithRetry(directory);
        }
    }

    [Fact]
    public async Task ConcurrentDuplicateSubmissionsExecuteOnceInPersistedOrderAndRegisterBeforeWork()
    {
        var store = new Store();
        var calls = new List<int>();
        var coordinator = new PersistentExecutionPlanCoordinator(store, async (item, register, ct) =>
        {
            Assert.Equal(ExecutionPlanItemState.Running, store.Receipt!.Items[item.Item.Ordinal].State);
            var runId = Guid.NewGuid();
            await register(runId, ct);
            Assert.Contains(runId, store.Receipt.Items[item.Item.Ordinal].RunIds);
            calls.Add(item.Item.Ordinal);
            return Success(item.ChildBatchId);
        });
        var plan = Plan();
        var submissions = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => coordinator.SubmitAsync(plan)));
        await coordinator.WhenIdleAsync();
        Assert.Single(submissions, s => s.Disposition == ExecutionPlanDisposition.Accepted);
        Assert.Equal(new[] { 0, 1, 2 }, calls);
        Assert.Equal(ExecutionPlanState.Succeeded, store.Receipt!.State);
        var conflict = await coordinator.SubmitAsync(plan with { ExpiresAt = plan.ExpiresAt.AddMinutes(1) });
        Assert.Equal(ExecutionPlanDisposition.Conflict, conflict.Disposition);
        Assert.Equal(3, calls.Count);
    }

    [Fact]
    public async Task AcceptedWorkSurvivesChatCancellationButLocalStopCancelsIt()
    {
        var store = new Store();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopping = 0;
        using var chat = new CancellationTokenSource();
        var coordinator = new PersistentExecutionPlanCoordinator(store, async (_, _, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException();
        }, () => stopping++);
        await coordinator.SubmitAsync(Plan(), chat.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        chat.Cancel();
        Assert.Equal(ExecutionPlanState.Running, store.Receipt!.State);
        coordinator.StopAll();
        await coordinator.WhenIdleAsync();
        Assert.Equal(1, stopping);
        Assert.Equal(ExecutionPlanState.Stopped, store.Receipt.State);
        Assert.Equal(RunOutcome.Cancelled, store.Receipt.Items[0].Result!.Outcome);
        Assert.All(store.Receipt.Items.Skip(1), item => Assert.Equal(ExecutionPlanItemState.Skipped, item.State));
    }

    [Theory]
    [InlineData("accept")]
    [InlineData("start")]
    [InlineData("item")]
    [InlineData("register")]
    [InlineData("completeItem")]
    [InlineData("complete")]
    public async Task StorageFailureNeverStartsLaterWork(string failure)
    {
        var store = new Store { Failure = failure };
        var sideEffects = 0;
        var coordinator = new PersistentExecutionPlanCoordinator(store, async (item, register, ct) =>
        {
            await register(Guid.NewGuid(), ct);
            sideEffects++;
            return Success(item.ChildBatchId);
        });
        if (failure == "accept") await Assert.ThrowsAsync<IOException>(() => coordinator.SubmitAsync(Plan()));
        else
        {
            await coordinator.SubmitAsync(Plan());
            await Assert.ThrowsAsync<IOException>(() => coordinator.WhenIdleAsync());
        }
        Assert.Equal(failure == "completeItem" ? 1 : failure == "complete" ? 3 : 0, sideEffects);
    }

    [Theory]
    [InlineData(RunOutcome.Failed, true, true)]
    [InlineData(RunOutcome.Cancelled, true, true)]
    [InlineData(RunOutcome.Indeterminate, true, true)]
    [InlineData(RunOutcome.Success, false, true)]
    [InlineData(RunOutcome.Success, true, false)]
    public async Task NonSuccessfulOrUncleanItemStopsRemainingItems(RunOutcome outcome, bool clean, bool restored)
    {
        var store = new Store();
        var calls = 0;
        var coordinator = new PersistentExecutionPlanCoordinator(store, (item, _, _) =>
        {
            calls++;
            return Task.FromResult(Success(item.ChildBatchId) with
            { Outcome = outcome, EngineCleanupConfirmed = clean, ConfigurationRestored = restored });
        });
        await coordinator.SubmitAsync(Plan());
        await coordinator.WhenIdleAsync();
        Assert.Equal(1, calls);
        Assert.Equal(ExecutionPlanState.Stopped, store.Receipt!.State);
    }

    [Fact]
    public async Task ExistingInterruptedPlanIsNeverReplayed()
    {
        var store = new Store();
        var plan = Plan();
        await store.AcceptAsync(plan);
        await store.InterruptUnfinishedAsync();
        var coordinator = new PersistentExecutionPlanCoordinator(store, (_, _, _) => throw new Exception("replay"));
        Assert.Equal(ExecutionPlanDisposition.Existing, (await coordinator.SubmitAsync(plan)).Disposition);
        await coordinator.WhenIdleAsync();
        Assert.Equal(ExecutionPlanState.Interrupted, store.Receipt!.State);
    }

    [Fact]
    public async Task StopDuringDurableAcceptancePreventsAnyBatchStart()
    {
        var store = new Store { AcceptGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var calls = 0;
        var coordinator = new PersistentExecutionPlanCoordinator(store, (item, _, _) =>
        { calls++; return Task.FromResult(Success(item.ChildBatchId)); });
        var submit = coordinator.SubmitAsync(Plan());
        await store.AcceptEntered.Task;
        coordinator.StopAll();
        var idle = coordinator.WhenIdleAsync();
        Assert.False(idle.IsCompleted);
        store.AcceptGate.SetResult();
        await submit;
        await idle;
        Assert.Equal(0, calls);
        Assert.Equal(ExecutionPlanState.Stopped, store.Receipt!.State);
    }

    [Fact]
    public async Task CancellationWinsEvenWhenFinalDelegateReturnsSuccess()
    {
        var store = new Store();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new PersistentExecutionPlanCoordinator(store, async (item, _, _) =>
        { entered.SetResult(); await release.Task; return Success(item.ChildBatchId); });
        var plan = Plan();
        await coordinator.SubmitAsync(plan with { Items = plan.Items.Take(1).ToArray() });
        await entered.Task;
        coordinator.StopAll();
        release.SetResult();
        await coordinator.WhenIdleAsync();
        Assert.Equal(ExecutionPlanState.Stopped, store.Receipt!.State);
        Assert.Equal(RunOutcome.Cancelled, store.Receipt.Items[0].Result!.Outcome);
    }

    [Fact]
    public async Task CallerCannotMutateAcceptedReceiptToChangeQueuedAccounts()
    {
        var store = new Store();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accounts = new List<Guid>();
        var coordinator = new PersistentExecutionPlanCoordinator(store, async (item, register, ct) =>
        {
            if (item.Item.Ordinal == 0) { entered.SetResult(); await release.Task; }
            accounts.Add(item.Item.AccountId);
            await register(Guid.NewGuid(), ct);
            return Success(item.ChildBatchId);
        });
        var plan = Plan();
        var submission = await coordinator.SubmitAsync(plan);
        await entered.Task;
        var mutable = (ExecutionPlanItemReceipt[])submission.Receipt!.Items;
        mutable[1] = mutable[1] with { Item = mutable[1].Item with { AccountId = Guid.NewGuid() } };
        release.SetResult();
        await coordinator.WhenIdleAsync();
        Assert.Equal(plan.Items.Select(i => i.AccountId), accounts);
    }

    private sealed class Store : IExecutionPlanStore
    {
        public ExecutionPlanReceipt? Receipt;
        public string? Failure;
        public TaskCompletionSource? AcceptGate;
        public TaskCompletionSource AcceptEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private void Check(string operation) { if (Failure == operation) throw new IOException(operation); }
        public async Task<ExecutionPlanSubmission> AcceptAsync(PreparedExecutionPlan plan, CancellationToken ct = default)
        {
            Check("accept"); ct.ThrowIfCancellationRequested();
            AcceptEntered.TrySetResult();
            if (AcceptGate is not null) await AcceptGate.Task;
            var hash = JsonSerializer.Serialize(plan);
            if (Receipt is not null) return new ExecutionPlanSubmission(
                Receipt.PayloadHash == hash ? ExecutionPlanDisposition.Existing : ExecutionPlanDisposition.Conflict, Receipt);
            Receipt = new(plan, hash, ExecutionPlanState.Accepted, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                plan.Items.Select(item => new ExecutionPlanItemReceipt(item, Guid.NewGuid(), ExecutionPlanItemState.Pending, [], null)).ToArray());
            return new ExecutionPlanSubmission(ExecutionPlanDisposition.Accepted, Receipt);
        }
        public Task<ExecutionPlanReceipt?> ReadAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Receipt);
        public Task<bool> TryStartAsync(Guid id, CancellationToken ct = default)
        { Check("start"); Receipt = Receipt! with { State = ExecutionPlanState.Running }; return Task.FromResult(true); }
        public Task StartItemAsync(Guid id, int ordinal, CancellationToken ct = default)
        { Check("item"); Change(ordinal, i => i with { State = ExecutionPlanItemState.Running }); return Task.CompletedTask; }
        public Task RegisterRunAsync(Guid id, int ordinal, Guid runId, CancellationToken ct = default)
        { Check("register"); Change(ordinal, i => i with { RunIds = [.. i.RunIds, runId] }); return Task.CompletedTask; }
        public Task CompleteItemAsync(Guid id, int ordinal, ExecutionPlanItemResult result, CancellationToken ct = default)
        { Check("completeItem"); Change(ordinal, i => i with { Result = result, State = result.AllowsNext ? ExecutionPlanItemState.Succeeded : ExecutionPlanItemState.Failed }); return Task.CompletedTask; }
        public Task CompleteAsync(Guid id, ExecutionPlanState state, string? reason, CancellationToken ct = default)
        {
            Check("complete"); Receipt = Receipt! with { State = state, Items = Receipt!.Items.Select(i =>
                i.State == ExecutionPlanItemState.Pending ? i with { State = ExecutionPlanItemState.Skipped } : i).ToArray() };
            return Task.CompletedTask;
        }
        public Task<int> InterruptUnfinishedAsync(CancellationToken ct = default)
        { Receipt = Receipt! with { State = ExecutionPlanState.Interrupted }; return Task.FromResult(1); }
        private void Change(int ordinal, Func<ExecutionPlanItemReceipt, ExecutionPlanItemReceipt> update) =>
            Receipt = Receipt! with { Items = Receipt!.Items.Select(i => i.Item.Ordinal == ordinal ? update(i) : i).ToArray() };
    }
}
