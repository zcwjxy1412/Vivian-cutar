namespace VIvian.Core.Execution;

/// <summary>
/// A run that, in this session, may still occupy the machine. While any hazard exists, no new queue starts.
/// </summary>
/// <param name="RunId">The run.</param>
/// <param name="Game">The run's game.</param>
/// <param name="LastKnown">The last state the scheduler decided for the run (may not have reached the database).</param>
/// <param name="PendingOperations">Adapter calls that were abandoned after their time limit and have not returned yet.</param>
/// <param name="RequiresAcknowledgement">Cleanup ended unconfirmed; the user must confirm before queues resume.</param>
/// <param name="TerminalPersisted">Whether the final state was written to the journal successfully.</param>
public sealed record SessionHazard(
    Guid RunId, GameId Game, RunUpdate? LastKnown, int PendingOperations, bool RequiresAcknowledgement, bool TerminalPersisted);

/// <summary>
/// In-memory, fail-closed record of runs that may still own resources. It is kept independently of persistence,
/// so a failed database write can never reopen the gate (#29), and abandoned adapter calls keep the gate closed
/// until they actually return (#28). Thread-safe.
/// </summary>
internal sealed class SafetyBarrier
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];

    private sealed class Entry(GameId game)
    {
        public GameId Game { get; } = game;
        public RunUpdate? Last { get; set; }
        public int Pending { get; set; }
        public bool RequiresAcknowledgement { get; set; }
        public bool Persisted { get; set; }
    }

    public event Action? Changed;

    public bool IsClear
    {
        get { lock (_gate) return _entries.Count == 0; }
    }

    public bool HasPendingOperations
    {
        get { lock (_gate) return _entries.Values.Any(e => e.Pending > 0); }
    }

    public void BeginOperation(Guid runId, GameId game)
    {
        lock (_gate) Get(runId, game).Pending++;
        Raise();
    }

    public void EndOperation(Guid runId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(runId, out var entry)) return;
            entry.Pending = Math.Max(0, entry.Pending - 1);
            RemoveIfClear(runId, entry);
        }
        Raise();
    }

    /// <summary>Records that the run ended with cleanup unconfirmed. Only an acknowledgement can clear this.</summary>
    public void MarkUnconfirmed(RunUpdate terminal, bool persisted)
    {
        lock (_gate)
        {
            var entry = Get(terminal.RunId, terminal.Game);
            entry.Last = terminal;
            entry.RequiresAcknowledgement = true;
            entry.Persisted = persisted;
        }
        Raise();
    }

    /// <summary>Remembers the last decided state of a run that has abandoned operations (for display and retry).</summary>
    public void Observe(RunUpdate update, bool persisted)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(update.RunId, out var entry)) return;
            entry.Last = update;
            entry.Persisted = persisted;
        }
    }

    public SessionHazard? Find(Guid runId)
    {
        lock (_gate) return _entries.TryGetValue(runId, out var e) ? ToHazard(runId, e) : null;
    }

    /// <summary>Clears the acknowledgement requirement. Refuses while any abandoned call of the run is still running.</summary>
    public bool TryResolve(Guid runId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(runId, out var entry)) return true;
            if (entry.Pending > 0) return false;
            entry.RequiresAcknowledgement = false;
            RemoveIfClear(runId, entry);
        }
        Raise();
        return true;
    }

    public void MarkPersisted(Guid runId)
    {
        lock (_gate)
            if (_entries.TryGetValue(runId, out var entry)) entry.Persisted = true;
    }

    public IReadOnlyList<SessionHazard> Snapshot()
    {
        lock (_gate) return _entries.Select(kv => ToHazard(kv.Key, kv.Value)).ToList();
    }

    private Entry Get(Guid runId, GameId game)
    {
        if (!_entries.TryGetValue(runId, out var entry)) _entries[runId] = entry = new Entry(game);
        return entry;
    }

    private void RemoveIfClear(Guid runId, Entry entry)
    {
        if (entry.Pending == 0 && !entry.RequiresAcknowledgement) _entries.Remove(runId);
    }

    private static SessionHazard ToHazard(Guid runId, Entry e) =>
        new(runId, e.Game, e.Last, e.Pending, e.RequiresAcknowledgement, e.Persisted);

    private void Raise()
    {
        try { Changed?.Invoke(); } catch (Exception) { /* observers never break safety */ }
    }
}
