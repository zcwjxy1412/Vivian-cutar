using VIvian.Core;
using VIvian.Core.Execution;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Accounts;
using Xunit;

namespace VIvian.Tests.Execution;

public sealed class PersistentPlanIntegrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("furina-plan-integration-").FullName;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealStoreAndCoordinatorPersistBeforeDelegateAndStopAfterUnsafeResult(bool failSecond)
    {
        var path = Path.Combine(_root, "data.db"); var db = new LocalDatabase(path); await db.InitializeAsync();
        var plan = new PreparedExecutionPlan(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5),
            new[] { GameId.StarRail, GameId.ZenlessZoneZero, GameId.Genshin }.Select((game, index) =>
                new ExecutionPlanItem(index, game, Guid.NewGuid(), "合成账号", "routine", "rev", 1,
                    "saved_account_settings", "合成资源策略")).ToArray());
        var calls = new List<int>();
        var coordinator = new PersistentExecutionPlanCoordinator(db, async (item, beforeRun, ct) =>
        {
            var reopened = new LocalDatabase(path);
            var before = (await reopened.ReadAsync(plan.PlanId, ct))!;
            Assert.Equal(ExecutionPlanState.Running, before.State);
            Assert.Equal(ExecutionPlanItemState.Running, before.Items[item.Item.Ordinal].State);
            Assert.Equal(item.ChildBatchId, before.Items[item.Item.Ordinal].ChildBatchId);
            var run = Guid.NewGuid(); await beforeRun(run, ct);
            Assert.Equal(run, Assert.Single((await reopened.ReadAsync(plan.PlanId, ct))!.Items[item.Item.Ordinal].RunIds));
            calls.Add(item.Item.Ordinal);
            var success = !failSecond || item.Item.Ordinal != 1;
            return new(item.ChildBatchId, success, success ? 1 : 0, success ? 0 : 1, "synthetic",
                HostCleanupConfirmed: true, GameCleanupConfirmed: true,
                Outcome: success ? RunOutcome.Success : RunOutcome.Failed,
                EngineCleanupConfirmed: true, ConfigurationRestored: true);
        });
        Assert.Equal(ExecutionPlanDisposition.Accepted, (await coordinator.SubmitAsync(plan)).Disposition);
        Assert.Equal(ExecutionPlanDisposition.Existing, (await coordinator.SubmitAsync(plan)).Disposition);
        await coordinator.WhenIdleAsync();
        var receipt = (await new LocalDatabase(path).ReadAsync(plan.PlanId))!;
        Assert.Equal(failSecond ? ExecutionPlanState.Stopped : ExecutionPlanState.Succeeded, receipt.State);
        Assert.Equal(failSecond ? new[] { 0, 1 } : [0, 1, 2], calls);
        if (failSecond) Assert.Equal(ExecutionPlanItemState.Skipped, receipt.Items[2].State);
        Assert.Equal(0, await db.InterruptUnfinishedAsync());
        var reopenedCoordinator = new PersistentExecutionPlanCoordinator(new LocalDatabase(path), (_, _, _) =>
            throw new InvalidOperationException("A completed plan must never execute again."));
        Assert.Equal(ExecutionPlanDisposition.Existing, (await reopenedCoordinator.SubmitAsync(plan)).Disposition);
        await reopenedCoordinator.WhenIdleAsync();
        Assert.Equal(receipt.State, (await db.ReadAsync(plan.PlanId))!.State);
    }

    public void Dispose() => TestDirectories.DeleteWithRetry(_root);
}
