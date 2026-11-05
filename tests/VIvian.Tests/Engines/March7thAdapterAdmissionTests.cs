using VIvian.Core;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Engines;
using VIvian.Infrastructure.Simulation;
using Xunit;

namespace VIvian.Tests.Engines;

public class March7thAdapterAdmissionTests
{
    [Fact]
    public async Task RealTaskCannotStartBeforeUacOwnershipIsVerified()
    {
        var evidence = new March7thReleaseEvidence("v2026.10.31", "March7thAssistant_full.zip", "archive", "cli");
        var adapter = new March7thAdapter("C:\\missing", evidence, "daily", () => true);
        var context = new RunContext(Guid.NewGuid(), Guid.NewGuid(), GameId.StarRail, DateTimeOffset.UtcNow);
        var error = await Assert.ThrowsAsync<EngineStartException>(() =>
            adapter.StartAsync(context, new RefusingProcessSupervisor(), CancellationToken.None));
        Assert.True(error.NothingStarted);
        Assert.Equal(ReasonCodes.RealAdapterUnverified, error.ReasonCode);
    }
}
