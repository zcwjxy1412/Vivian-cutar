using VIvian.Core.Engines;
using Xunit;

namespace VIvian.Tests.Engines;

/// <summary>M4-F1: per-run admission on the real-test page. Re-evaluated before every single run.</summary>
public class ManualTestAdmissionTests
{
    private static ManualTestInputs Ready => new("screen_test", UserConfirmedThisRun: true,
        SelectionUnchangedSinceConfirmation: true, ReleaseArchiveVerified: true, InstallIdentityVerified: true,
        QueueIdle: true, QueueRunning: false, HasUnresolvedCleanup: false, OneShotConsumed: false);

#if M4_MANUAL_TEST
    [Fact]
    public void ReusableBuildAdmitsRepeatedRunsOnlyWithFreshConfirmationAndConfirmedCleanup()
    {
        Assert.True(RealExecutionPolicy.IsReusableManualTest);
        Assert.False(RealExecutionPolicy.IsOneShotScreenTest);
        // First run.
        Assert.Equal(ManualTestState.Ready, ManualTestPolicy.Evaluate(Ready).State);
        // While it runs nothing else can start.
        Assert.Equal(ManualTestState.Running, ManualTestPolicy.Evaluate(Ready with { QueueRunning = true, QueueIdle = false }).State);
        // It ended with cleanup unconfirmed: the next run is blocked even if the user confirms again.
        var blocked = ManualTestPolicy.Evaluate(Ready with { HasUnresolvedCleanup = true });
        Assert.False(blocked.Allowed);
        Assert.Equal(ManualTestState.CleanupUnconfirmed, blocked.State);
        Assert.StartsWith("清理未确认", blocked.Text);
        // After reconciliation the confirmation was reset: not admitted until the user confirms this run.
        var reset = ManualTestPolicy.Evaluate(Ready with { UserConfirmedThisRun = false });
        Assert.Equal(ManualTestState.NeedsConfirmation, reset.State);
        Assert.False(reset.Allowed);
        // Fresh confirmation: a second run is admitted without a new build (no package-wide marker in this build).
        Assert.True(ManualTestPolicy.Evaluate(Ready with { OneShotConsumed = true }).Allowed);
    }

    [Theory]
    [InlineData("daily")]
    [InlineData("power")]
    [InlineData("main")]
    [InlineData("")]
    public void OnlyScreenTestIsEverAdmitted(string task)
        => Assert.Equal(ManualTestState.NotAvailable, ManualTestPolicy.Evaluate(Ready with { TaskId = task }).State);

    [Fact]
    public void ChangedSelectionOrUnverifiedInstallBlocks()
    {
        Assert.Equal(ManualTestState.NeedsConfirmation,
            ManualTestPolicy.Evaluate(Ready with { SelectionUnchangedSinceConfirmation = false }).State);
        var install = ManualTestPolicy.Evaluate(Ready with { InstallIdentityVerified = false });
        Assert.Equal(ManualTestState.InstallNotVerified, install.State);
        Assert.StartsWith("未通过安装核验", install.Text);
        Assert.Equal(ManualTestState.InstallNotVerified,
            ManualTestPolicy.Evaluate(Ready with { ReleaseArchiveVerified = false }).State);
    }
#elif M5_DAILY_TEST
    [Fact]
    public void DailyRequiresFreshConsentAndConfirmedCleanup()
    {
        var daily = Ready with { TaskId = "daily" };
        Assert.True(ManualTestPolicy.Evaluate(daily).Allowed);
        Assert.Equal(ManualTestState.NotAvailable, ManualTestPolicy.Evaluate(Ready).State);
        Assert.Equal(ManualTestState.NeedsConfirmation,
            ManualTestPolicy.Evaluate(daily with { UserConfirmedThisRun = false }).State);
        Assert.Equal(ManualTestState.CleanupUnconfirmed,
            ManualTestPolicy.Evaluate(daily with { HasUnresolvedCleanup = true }).State);
        Assert.Equal(ManualTestState.Running,
            ManualTestPolicy.Evaluate(daily with { QueueRunning = true }).State);
    }
#elif M4_SCREEN_TEST
    [Fact]
    public void OneShotPackageStaysConsumedAfterItsAttempt()
    {
        Assert.True(RealExecutionPolicy.IsOneShotScreenTest);
        Assert.True(ManualTestPolicy.Evaluate(Ready).Allowed);
        Assert.Equal(ManualTestState.Consumed, ManualTestPolicy.Evaluate(Ready with { OneShotConsumed = true }).State);
    }
#else
    [Fact]
    public void DefaultBuildNeverAdmitsARealRun()
    {
        Assert.False(RealExecutionPolicy.BuildPermitsRealExecution);
        Assert.False(RealExecutionPolicy.IsReusableManualTest);
        var decision = ManualTestPolicy.Evaluate(Ready);
        Assert.False(decision.Allowed);
        Assert.Equal(ManualTestState.NotAvailable, decision.State);
    }
#endif
}
