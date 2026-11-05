using VIvian.Core.Execution;

namespace VIvian.Core.Engines;

/// <summary>Raw signals an engine adapter can observe. None of them decides the outcome on its own.</summary>
public enum SignalKind
{
    /// <summary>The root process exited with <see cref="ObservedSignal.ExitCode"/>.</summary>
    ProcessExit,
    /// <summary>The engine's own completion line for the planned task (from output or a new log line).</summary>
    CompletionMarker,
    /// <summary>The engine's own failure line.</summary>
    FailureMarker,
    /// <summary>The engine reported cancellation.</summary>
    CancelMarker,
    /// <summary>An unhandled exception, traceback or fatal line.</summary>
    FatalError,
    /// <summary>The engine reported that all tasks were skipped (nothing was done).</summary>
    NothingDone,
}

/// <summary>A signal tagged with the RunId it was attributed to. Signals of other runs are ignored.</summary>
public sealed record ObservedSignal(Guid RunId, SignalKind Kind, string Source, DateTimeOffset At, int? ExitCode = null);

/// <summary>
/// Combines raw engine signals into a verdict for one run, following the composite rule from
/// docs/research/M3_ADAPTER_IMPLEMENTATION_REQUIREMENTS.md §2.5: success needs the engine's completion signal, exit code 0
/// and no failure or fatal signal, all for this RunId. An exit code alone, or a contract whose signals have not been
/// verified on the real release, never yields success.
/// </summary>
public static class CompletionEvidenceEvaluator
{
    public const string UnverifiedContract = ReasonCodes.RealAdapterUnverified;

    public static RunVerdict Evaluate(Guid runId, bool contractVerified, IEnumerable<ObservedSignal> signals)
    {
        ArgumentNullException.ThrowIfNull(signals);
        var own = signals.Where(s => s.RunId == runId).ToList();
        var evidence = own.Select(s => new Evidence(runId, Map(s), Describe(s), s.At)).ToList();
        if (!contractVerified)
        {
            // Signals are kept as notes: nothing decides an outcome before the release's signals are verified.
            var notes = evidence.Select(e => e with { Kind = EvidenceKind.Note }).ToList();
            return new(RunOutcome.Indeterminate, UnverifiedContract, notes);
        }
        var verdict = VerdictPolicy.Decide(runId, evidence);
        if (verdict.Outcome == RunOutcome.Success)
        {
            var exits = own.Where(s => s.Kind == SignalKind.ProcessExit).ToList();
            if (exits.Count == 0 || exits.Any(s => s.ExitCode != 0))
                return new(RunOutcome.Indeterminate, ReasonCodes.ConflictingEvidence, verdict.Evidence);
            if (own.Any(s => s.Kind == SignalKind.NothingDone))
                return new(RunOutcome.Indeterminate, ReasonCodes.NoCompletionEvidence, verdict.Evidence);
        }
        return verdict;
    }

    private static EvidenceKind Map(ObservedSignal signal) => signal.Kind switch
    {
        SignalKind.CompletionMarker => EvidenceKind.CompletionReported,
        SignalKind.FailureMarker or SignalKind.FatalError => EvidenceKind.FailureReported,
        SignalKind.CancelMarker => EvidenceKind.CancelReported,
        SignalKind.ProcessExit when signal.ExitCode is { } code && code != 0 => EvidenceKind.FailureReported,
        _ => EvidenceKind.Note,
    };

    private static string Describe(ObservedSignal signal) =>
        signal.Kind == SignalKind.ProcessExit ? $"{signal.Source}: exit {signal.ExitCode?.ToString() ?? "?"}" : $"{signal.Source}: {signal.Kind}";
}
