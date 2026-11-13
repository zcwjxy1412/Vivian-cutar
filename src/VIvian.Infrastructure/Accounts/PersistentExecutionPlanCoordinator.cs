using VIvian.Core;
using VIvian.Core.Execution;

namespace VIvian.Infrastructure.Accounts;

public delegate Task<MultiAccountOutcome> ExecutionPlanBatchRunner(ExecutionPlanItemReceipt item,
    Func<Guid, CancellationToken, Task> beforeRun, CancellationToken cancellationToken);

/// <summary>Owns accepted work independently of chat lifetime; game delegates retain their existing desktop leases.</summary>
public sealed class PersistentExecutionPlanCoordinator(IExecutionPlanStore store,
    ExecutionPlanBatchRunner executeBatch, Action? stopActive = null, CancellationToken hostStopping = default)
{
    private readonly SemaphoreSlim _submission = new(1, 1);
    private readonly object _gate = new();
    private readonly HashSet<CancellationTokenSource> _accepted = [];
    private Task _idle = Task.CompletedTask;
    private bool _blocked;
    private long _stopVersion;

    public async Task<ExecutionPlanSubmission> SubmitAsync(PreparedExecutionPlan plan, CancellationToken ct = default)
    {
        long stopVersion;
        lock (_gate) stopVersion = _stopVersion;
        await _submission.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The store owns canonical hashing, exact-ID deduplication and durable child IDs.
            var result = await store.AcceptAsync(plan, ct).ConfigureAwait(false);
            if (result.Disposition != ExecutionPlanDisposition.Accepted) return result;
            var receipt = result.Receipt ?? throw new InvalidDataException("Accepted plan has no receipt.");
            // The public receipt may expose a mutable array; queued work owns its accepted item snapshot.
            receipt = receipt with { Items = receipt.Items.ToArray() };
            lock (_gate)
            {
                var stop = CancellationTokenSource.CreateLinkedTokenSource(hostStopping);
                if (stopVersion != _stopVersion) stop.Cancel();
                _accepted.Add(stop);
                var previous = _idle;
                _idle = Task.Run(() => ExecuteAfterAsync(previous, receipt, stop));
            }
            return result;
        }
        finally { _submission.Release(); }
    }

    public void StopAll()
    {
        CancellationTokenSource[] pending;
        lock (_gate) { _stopVersion++; pending = _accepted.ToArray(); }
        List<Exception> failures = [];
        foreach (var stop in pending)
            try { stop.Cancel(); }
            catch (ObjectDisposedException) { /* Work finished after the snapshot. */ }
            catch (Exception ex) { failures.Add(ex); }
        try { stopActive?.Invoke(); }
        catch (Exception ex) { failures.Add(ex); }
        if (failures.Count != 0) throw new AggregateException("本地停止回调发生错误；所有计划均已收到取消请求。", failures);
    }

    public async Task WhenIdleAsync(CancellationToken ct = default)
    {
        // Include acceptance already in progress, not just the previously scheduled worker.
        await _submission.WaitAsync(ct).ConfigureAwait(false);
        Task idle;
        try { lock (_gate) idle = _idle; }
        finally { _submission.Release(); }
        await idle.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task ExecuteAfterAsync(Task previous, ExecutionPlanReceipt receipt, CancellationTokenSource stop)
    {
        var id = receipt.Plan.PlanId;
        try
        {
            await previous.ConfigureAwait(false);
            if (!await store.TryStartAsync(id, CancellationToken.None).ConfigureAwait(false)) return;
            foreach (var item in receipt.Items)
            {
                bool blocked;
                lock (_gate) blocked = _blocked;
                if (blocked || stop.IsCancellationRequested)
                {
                    await store.CompleteAsync(id, ExecutionPlanState.Stopped,
                        blocked ? ReasonCodes.CleanupUnconfirmed : ReasonCodes.UserStop, CancellationToken.None).ConfigureAwait(false);
                    return;
                }
                await store.StartItemAsync(id, item.Item.Ordinal, CancellationToken.None).ConfigureAwait(false);
                Exception? registrationFailure = null;
                ExecutionPlanItemResult result;
                try
                {
                    stop.Token.ThrowIfCancellationRequested();
                    var batch = await executeBatch(item, async (runId, token) =>
                    {
                        try { await store.RegisterRunAsync(id, item.Item.Ordinal, runId, token).ConfigureAwait(false); }
                        catch (Exception ex) { registrationFailure = ex; throw; }
                    }, stop.Token).ConfigureAwait(false);
                    var clean = batch.EngineCleanupConfirmed && batch.GameCleanupConfirmed && batch.HostCleanupConfirmed;
                    var success = batch.BatchId == item.ChildBatchId && batch.Completed && batch.Failed == 0 &&
                        batch.Succeeded == 1 && batch.Outcome == RunOutcome.Success;
                    result = new(success ? RunOutcome.Success : batch.Outcome is RunOutcome.Success or null
                            ? RunOutcome.Indeterminate : batch.Outcome.Value,
                        clean ? CleanupState.Confirmed : CleanupState.Unconfirmed, batch.ConfigurationRestored,
                        !clean ? ReasonCodes.CleanupUnconfirmed : success ? ReasonCodes.Completed : ReasonCodes.NoCompletionEvidence);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                { result = new(RunOutcome.Cancelled, CleanupState.Unconfirmed, false, ReasonCodes.UserStop); }
                catch (Exception)
                { result = new(RunOutcome.Indeterminate, CleanupState.Unconfirmed, false, ReasonCodes.AdapterError); }
                if (registrationFailure is not null)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(registrationFailure).Throw();
                if (stop.IsCancellationRequested)
                    result = result with { Outcome = RunOutcome.Cancelled, ReasonCode = ReasonCodes.UserStop };
                await store.CompleteItemAsync(id, item.Item.Ordinal, result, CancellationToken.None).ConfigureAwait(false);
                if (!result.AllowsNext)
                {
                    if (result.Cleanup == CleanupState.Unconfirmed || !result.ConfigurationRestored)
                        lock (_gate) _blocked = true;
                    await store.CompleteAsync(id, ExecutionPlanState.Stopped, result.ReasonCode, CancellationToken.None).ConfigureAwait(false);
                    return;
                }
            }
            await store.CompleteAsync(id, ExecutionPlanState.Succeeded, null, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate) _blocked = true;
            // Keep incomplete durable state for startup interruption; never turn a write failure into success.
            throw;
        }
        finally
        {
            lock (_gate) { _accepted.Remove(stop); stop.Dispose(); }
        }
    }
}
