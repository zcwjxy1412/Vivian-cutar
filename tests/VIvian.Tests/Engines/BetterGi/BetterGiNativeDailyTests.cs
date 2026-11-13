using VIvian.Infrastructure.Engines.BetterGi;
using Xunit;

namespace VIvian.Tests.Engines.BetterGi;

public sealed class BetterGiNativeDailyTests
{
    private static BetterGiRunObservation Complete => new(42, DateTimeOffset.UtcNow, true, true, true,
        false, false, 0, false, false) { CraftAction = BetterGiCraftAction.Attempt };

    [Fact]
    public void DelegatedCompletionDoesNotClaimInventoryVerification()
    {
        Assert.True(BetterGiNativeDaily.ReportedComplete(Complete));
        Assert.False(Complete.PlannedTasksVerified);
        Assert.Null(Complete.CraftedCount);
    }

    [Fact]
    public void IncompleteConflictingOrForeignResultsCannotAdvanceAccounts()
    {
        Assert.False(BetterGiNativeDaily.ReportedComplete(Complete with { FlowEnded = false }));
        Assert.True(BetterGiNativeDaily.ReportedComplete(Complete with { DailyRewardVerified = false }));
        Assert.False(BetterGiNativeDaily.ReportedComplete(Complete with { ForeignActivitySeen = true }));
        Assert.False(BetterGiNativeDaily.ReportedComplete(Complete with { AdverseSignal = true }));
        Assert.False(BetterGiNativeDaily.ReportedComplete(Complete with { ExitCode = 1 }));
        Assert.True(BetterGiNativeDaily.ReportedComplete(Complete with { CraftAction = BetterGiCraftAction.Conflict }));
        Assert.True(BetterGiNativeDaily.ReportedComplete(Complete with { CraftAction = BetterGiCraftAction.None }));
        Assert.True(BetterGiNativeDaily.ReportedComplete(Complete with { CraftAction = BetterGiCraftAction.Skipped }));
        Assert.True(BetterGiNativeDaily.ReportedComplete(Complete with { CraftAction = BetterGiCraftAction.None }));
    }
}
