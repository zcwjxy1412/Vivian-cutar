using Microsoft.Extensions.Time.Testing;
using VIvian.Infrastructure.Engines.BetterGi;
using Xunit;

namespace VIvian.Tests.Engines.BetterGi;

public sealed class GenshinResinReadinessTests
{
    [Fact]
    public void OuterVerificationBudgetAllowsMapLoadingAndWorldRechecks()
    {
        Assert.True(GenshinResinReadiness.VerificationTimeout >= GenshinResinReadiness.Timeout
            + GenshinResinReadiness.ReturnToWorldTimeout + TimeSpan.FromSeconds(30));
    }
    [Fact]
    public void SlowMapCanLoadBeforeTwoConsecutiveCountsAgree()
    {
        var time = new FakeTimeProvider();
        var wait = new GenshinResinReadiness(time);
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.False(wait.Expired);
        Assert.False(wait.Observe(null));
        Assert.False(wait.Observe((143, 0)));
        Assert.False(wait.Observe(null));
        Assert.False(wait.Observe((143, 0)));
        Assert.False(wait.Observe((144, 0)));
        Assert.True(wait.Observe((144, 0)));
    }

    [Fact]
    public void LoadingCannotWaitForeverOrAcceptLateSamples()
    {
        var time = new FakeTimeProvider();
        var wait = new GenshinResinReadiness(time);
        Assert.False(wait.Observe((143, 0)));
        time.Advance(TimeSpan.FromSeconds(60));
        Assert.True(wait.Expired);
        Assert.False(wait.Observe((143, 0)));
    }
}
