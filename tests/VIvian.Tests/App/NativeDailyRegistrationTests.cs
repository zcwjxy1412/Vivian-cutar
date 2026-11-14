using Microsoft.Extensions.DependencyInjection;
using VIvian.Core.Engines;
using VIvian.Infrastructure.Engines.BetterGi;
using Xunit;

namespace VIvian.Tests.App;

public sealed class NativeDailyRegistrationTests
{
    [Fact]
    public void UnifiedDailyUsesNativeSavedPlanAndRecoveryWithoutOpeningLegacyCliGate()
    {
        var services = new ServiceCollection();
        VIvian.App.App.RegisterGenshinDailyServices(services, rewardAcceptance: false);
        using var provider = services.BuildServiceProvider();
        var enabled = RealExecutionPolicy.IsM5DailyTest && RealExecutionPolicy.IsUnifiedInstall;
        Assert.Equal(enabled, provider.GetService<VIvian.App.GenshinDailyBatch>() is not null);
        Assert.Equal(enabled, provider.GetService<VIvian.App.GenshinDailyRecovery>() is not null);
        Assert.False(BetterGiContract.LaunchContractVerified);
        Assert.False(BetterGiContract.CompletionSignalsVerified);
        if (enabled)
            Assert.Equal(typeof(BetterGiAccountBatch), provider.GetRequiredService<VIvian.App.GenshinDailyBatch>().Method.DeclaringType);
    }

    [Fact]
    public void DedicatedRewardAcceptanceKeepsItsSeparateVerifier()
    {
        var services = new ServiceCollection();
        VIvian.App.App.RegisterGenshinDailyServices(services, rewardAcceptance: true);
        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<VIvian.App.GenshinDailyBatch>());
        Assert.Equal(RealExecutionPolicy.IsM5DailyTest && RealExecutionPolicy.IsUnifiedInstall,
            provider.GetService<VIvian.App.GenshinDailyRecovery>() is not null);
    }
}
