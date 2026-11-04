namespace VIvian.Core.Execution;

/// <summary>
/// Serializes every run-state write of one scheduler. A write is handed to the journal only after the previous one
/// has finished (successfully or not), so a write that completes late can never land after a newer state of the
/// same run: the stored order is always the order in which the scheduler decided the states. The SQLite store also
/// refuses to change a row that is already terminal, so even a foreign late write cannot overwrite a final state.
/// <para>
/// The writer itself never waits: callers bound their own wait. A write whose wait gave up stays queued and is
/// tracked as <em>overdue</em>; while any write is overdue, the journal is treated as stalled and later callers do not
/// wait at all (their writes queue behind it, in order).
/// </para>
/// </summary>
internal sealed class JournalWriter(IRunJournal journal)
{
    private readonly Lock _gate = new();
    private Task _tail = Task.CompletedTask;
    private int _overdue;
    private int _inFlight;

    /// <summary>Writes queued or running (diagnostics and deterministic tests).</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>True while a write whose bounded wait gave up has not completed yet.</summary>
    public bool IsStalled => Volatile.Read(ref _overdue) > 0;

    /// <summary>Queues a write after every earlier one. The journal call never runs on the caller's thread.</summary>
    public Task Enqueue(RunUpdate update)
    {
        lock (_gate)
        {
            Interlocked.Increment(ref _inFlight);
            // Not ExecuteSynchronously: the journal (which may block) never runs inline or under this lock.
            var write = _tail.ContinueWith(_ => journal.PublishAsync(update, CancellationToken.None),
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            // The chain itself never faults, so one failed write does not stop later ones.
            _tail = write.ContinueWith(_ => Interlocked.Decrement(ref _inFlight), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return write;
        }
    }

    public void BeginOverdue() => Interlocked.Increment(ref _overdue);

    public void EndOverdue() => Interlocked.Decrement(ref _overdue);
}
