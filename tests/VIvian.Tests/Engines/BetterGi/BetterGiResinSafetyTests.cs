using Microsoft.Extensions.Time.Testing;
using VIvian.Infrastructure.Engines.BetterGi;
using Xunit;

namespace VIvian.Tests.Engines.BetterGi;

public sealed class BetterGiResinSafetyTests
{
    private static readonly BetterGiResinSession Session = new(new("5aa694b5-4303-4ea9-babe-cbc2d161e01a"), 4242,
        new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));

    [Theory]
    [InlineData(61, 4, 60, true)]
    [InlineData(61, 2, 60, false)]
    [InlineData(62, 4, 60, false)]
    [InlineData(61, 4, 62, false)]
    [InlineData(61, null, 60, false)]
    [InlineData(1, 5, 0, false)]
    public void CraftNeedsMatchingInventoryChangesAndMustKeepTheRequestedReserve(int originalAfter, int? condensedAfter,
        int keep, bool expected)
    {
        var before = new BetterGiResinInventory(new(Session, 1, 10), 181, 2);
        var after = new BetterGiResinInventory(new(Session, 3, 30), originalAfter, condensedAfter);

        Assert.Equal(expected, BetterGiResinSafety.CraftVerified(Session, before, new(Session, 2, 20), after, 2, keep));
    }

    [Fact]
    public void CraftRejectsAnotherRunAndReusedProcessIdentityOrAnOlderCapture()
    {
        var before = new BetterGiResinInventory(new(Session, 1, 10), 120, 0);
        var submission = new BetterGiResinEvent(Session, 2, 20);
        var after = new BetterGiResinInventory(new(Session, 3, 30), 60, 1);

        Assert.False(BetterGiResinSafety.CraftVerified(Session, before, submission,
            after with { Event = new(Session with { RunId = Guid.NewGuid() }, 3, 30) }, 1, 0));
        Assert.False(BetterGiResinSafety.CraftVerified(Session, before, submission,
            after with { Event = new(Session with { ProcessStartedAt = Session.ProcessStartedAt.AddSeconds(1) }, 3, 30) }, 1, 0));
        Assert.False(BetterGiResinSafety.CraftVerified(Session, before, submission,
            after with { Event = new(Session, 3, 19) }, 1, 0));
    }

    [Fact]
    public void DomainChecksExactFreshOfferBeforeDispatchAndNeverExceedsTheTypeBudget()
    {
        var time = new FakeTimeProvider();
        var budget = Budget(time, original20: 1, original40: 1);
        var calls = 0;
        bool Click() { calls++; return true; }

        Assert.False(budget.TryDispatch(new(new(Session, 1, time.GetTimestamp()), BetterGiResinKind.Unknown, 20), Click));
        Assert.False(budget.TryDispatch(new(new(Session, 2, time.GetTimestamp()), BetterGiResinKind.Original20, 40), Click));
        var valid = new BetterGiResinOffer(new(Session, 3, time.GetTimestamp()), BetterGiResinKind.Original20, 20);
        Assert.True(budget.TryDispatch(valid, Click));
        Assert.False(budget.TryDispatch(valid with { Kind = BetterGiResinKind.Original40, Amount = 40 }, Click));
        Assert.False(budget.TryDispatch(valid with { Event = new(Session, 4, time.GetTimestamp()) }, Click));
        Assert.True(budget.TryDispatch(new(new(Session, 5, time.GetTimestamp()), BetterGiResinKind.Original40, 40), Click));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void DomainRejectsStaleAndForeignOffersAndStopsAfterUncertainDispatch()
    {
        var time = new FakeTimeProvider();
        var budget = Budget(time, condensed: 2);
        var stale = time.GetTimestamp();
        time.Advance(TimeSpan.FromSeconds(10));
        var calls = 0;
        bool UnknownOutcome() { calls++; return false; }

        Assert.False(budget.TryDispatch(new(new(Session, 1, stale), BetterGiResinKind.Condensed, 1), UnknownOutcome));
        Assert.False(budget.TryDispatch(new(new(Session with { GameProcessId = 4040 }, 2, time.GetTimestamp()),
            BetterGiResinKind.Condensed, 1), UnknownOutcome));
        Assert.False(budget.TryDispatch(new(new(Session, 3, time.GetTimestamp()), BetterGiResinKind.Condensed, 1), UnknownOutcome));
        Assert.False(budget.TryDispatch(new(new(Session, 4, time.GetTimestamp()), BetterGiResinKind.Condensed, 1), UnknownOutcome));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ConcurrentClaimAttemptsCanDispatchAtMostTheRemainingBudget()
    {
        var time = new FakeTimeProvider();
        var budget = Budget(time, condensed: 1);
        var calls = 0;
        Parallel.For(1, 17, i => budget.TryDispatch(new(new(Session, i, time.GetTimestamp()), BetterGiResinKind.Condensed, 1),
            () => { Interlocked.Increment(ref calls); return true; }));

        Assert.Equal(1, calls);
    }

    private static BetterGiDomainSpendBudget Budget(TimeProvider time, int original20 = 0, int original40 = 0, int condensed = 0) =>
        new(Session, new(true, [], 0, original20, original40, condensed, 0, 0, false), time);
}
