using VIvian.Core.Execution;
using Xunit;

namespace VIvian.Tests.Execution;

public sealed class StateModelTests
{
    private static readonly Guid Run = Guid.NewGuid();
    private static Evidence E(EvidenceKind kind, Guid? run = null) => new(run ?? Run, kind, "t", DateTimeOffset.UtcNow);

    [Fact]
    public void OnlyExplicitCompletionEvidenceIsSuccess()
    {
        Assert.Equal(RunOutcome.Success, VerdictPolicy.Decide(Run, [E(EvidenceKind.CompletionReported)]).Outcome);
        Assert.Equal(RunOutcome.Indeterminate, VerdictPolicy.Decide(Run, []).Outcome);
        Assert.Equal(RunOutcome.Indeterminate, VerdictPolicy.Decide(Run, [E(EvidenceKind.Note)]).Outcome);
    }

    [Fact]
    public void ConflictingEvidenceIsIndeterminateRatherThanOneWordVeto()
    {
        var verdict = VerdictPolicy.Decide(Run, [E(EvidenceKind.CompletionReported), E(EvidenceKind.FailureReported)]);
        Assert.Equal(RunOutcome.Indeterminate, verdict.Outcome);
        Assert.Equal(ReasonCodes.ConflictingEvidence, verdict.ReasonCode);
    }

    [Fact]
    public void EvidenceFromOtherRunsIsIgnored()
    {
        var verdict = VerdictPolicy.Decide(Run, [E(EvidenceKind.FailureReported, Guid.NewGuid()), E(EvidenceKind.CompletionReported)]);
        Assert.Equal(RunOutcome.Success, verdict.Outcome);
        Assert.All(verdict.Evidence, e => Assert.Equal(Run, e.RunId));
    }

    [Fact]
    public void FailureAndCancelReportsMapToTheirOutcomes()
    {
        Assert.Equal(RunOutcome.Failed, VerdictPolicy.Decide(Run, [E(EvidenceKind.FailureReported)]).Outcome);
        Assert.Equal(RunOutcome.Cancelled, VerdictPolicy.Decide(Run, [E(EvidenceKind.CancelReported)]).Outcome);
    }

    public static TheoryData<RunPhase, RunPhase> LegalTransitions() => new()
    {
        { RunPhase.Pending, RunPhase.Preflight }, { RunPhase.Pending, RunPhase.Finished },
        { RunPhase.Preflight, RunPhase.Starting }, { RunPhase.Preflight, RunPhase.Blocked },
        { RunPhase.Preflight, RunPhase.Finished }, { RunPhase.Starting, RunPhase.Running },
        { RunPhase.Starting, RunPhase.Stopping }, { RunPhase.Starting, RunPhase.Reconciling },
        { RunPhase.Running, RunPhase.Stopping }, { RunPhase.Running, RunPhase.Reconciling },
        { RunPhase.Stopping, RunPhase.Reconciling }, { RunPhase.Reconciling, RunPhase.Finished },
        { RunPhase.Reconciling, RunPhase.Blocked },
    };

    [Theory]
    [MemberData(nameof(LegalTransitions))]
    public void LegalTransitionsAreAllowed(RunPhase from, RunPhase to) => Assert.True(RunStateMachine.CanTransition(from, to));

    [Fact]
    public void EveryOtherTransitionIsRejected()
    {
        var legal = LegalTransitions().Select(row => ((RunPhase)row[0], (RunPhase)row[1])).ToHashSet();
        foreach (var from in Enum.GetValues<RunPhase>())
        foreach (var to in Enum.GetValues<RunPhase>())
            Assert.Equal(legal.Contains((from, to)), RunStateMachine.CanTransition(from, to));
    }

    [Fact]
    public void RunCannotSkipFromRunningToFinished()
    {
        var sm = Advance(RunPhase.Preflight, RunPhase.Starting, RunPhase.Running);
        Assert.Throws<InvalidOperationException>(() => sm.MoveTo(RunPhase.Finished));
    }

    [Fact]
    public void FinishingRequiresOutcomeAndConfirmedCleanup()
    {
        var sm = Advance(RunPhase.Preflight, RunPhase.Starting, RunPhase.Running, RunPhase.Reconciling);
        Assert.Throws<InvalidOperationException>(() => sm.MoveTo(RunPhase.Finished));
        sm.SetOutcome(RunOutcome.Success, ReasonCodes.Completed);
        Assert.Equal(CleanupState.Pending, sm.Cleanup);
        Assert.Throws<InvalidOperationException>(() => sm.MoveTo(RunPhase.Finished));
        sm.SetCleanup(CleanupState.Unconfirmed);
        Assert.Throws<InvalidOperationException>(() => sm.MoveTo(RunPhase.Finished));
        sm.SetCleanup(CleanupState.Confirmed);
        sm.MoveTo(RunPhase.Finished);
        Assert.True(sm.AllowsNextTask);
    }

    [Fact]
    public void OutcomeIsDecidedOnlyOnce()
    {
        var sm = Advance(RunPhase.Preflight, RunPhase.Starting);
        sm.SetOutcome(RunOutcome.Failed, ReasonCodes.StartFailed);
        Assert.Throws<InvalidOperationException>(() => sm.SetOutcome(RunOutcome.Success, ReasonCodes.Completed));
    }

    [Fact]
    public void RunThatNeverStartedCanOnlyBeCancelled()
    {
        var sm = new RunStateMachine();
        Assert.Throws<InvalidOperationException>(() => sm.SetOutcome(RunOutcome.Success, ReasonCodes.Completed));
        sm.SetOutcome(RunOutcome.Cancelled, ReasonCodes.UserStop);
        sm.MoveTo(RunPhase.Finished);
        Assert.False(sm.AllowsNextTask);
    }

    [Fact]
    public void TerminalRunRejectsCleanupChanges()
    {
        var sm = new RunStateMachine();
        sm.SetOutcome(RunOutcome.Cancelled, ReasonCodes.UserStop);
        sm.MoveTo(RunPhase.Finished);
        Assert.Throws<InvalidOperationException>(() => sm.SetCleanup(CleanupState.Confirmed));
    }

    private static RunStateMachine Advance(params RunPhase[] phases)
    {
        var sm = new RunStateMachine();
        foreach (var phase in phases) sm.MoveTo(phase);
        return sm;
    }
}
