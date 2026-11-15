using VIvian.Core;
using VIvian.Core.Execution;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Accounts;
using Xunit;

namespace VIvian.Tests.Accounts;

public sealed class ExecutionPlanPreflightTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("furina-preflight-").FullName;
    private static PreparedExecutionPlan Plan() => new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5),
        Enumerable.Range(0, 3).Select(i => new ExecutionPlanItem(i, GameId.StarRail, Guid.NewGuid(),
            "synthetic", "routine", "revision", 1, "saved_account_settings", "synthetic")).ToArray());
    private static MultiAccountOutcome Success(Guid id) => new(id, true, 1, 0, "synthetic",
        GameCleanupConfirmed: true, EngineCleanupConfirmed: true, ConfigurationRestored: true, Outcome: RunOutcome.Success);

    [Fact]
    public async Task KnownPreflightRejectionStopsPlanWithoutPoisoningNextConfirmedPlan()
    {
        var db = new LocalDatabase(Path.Combine(_root, "test.db")); await db.InitializeAsync();
        var calls = 0; var reject = true;
        var coordinator = new PersistentExecutionPlanCoordinator(db, async (item, register, ct) =>
        { calls++; await register(Guid.NewGuid(), ct); return Success(item.ChildBatchId); },
            validateBeforeExecution: (item, _) => reject && item.Item.Ordinal == 1
                ? Task.FromException(new ExecutionPlanPreflightException("Configuration changed")) : Task.CompletedTask);
        var plan = Plan();
        await coordinator.SubmitAsync(plan); await coordinator.WhenIdleAsync();
        var receipt = (await db.ReadAsync(plan.PlanId))!;
        Assert.Equal(1, calls);
        Assert.Equal(ExecutionPlanState.Stopped, receipt.State);
        Assert.Equal(ReasonCodes.PreflightError, receipt.ReasonCode);
        Assert.Equal(ExecutionPlanItemState.Succeeded, receipt.Items[0].State);
        Assert.Empty(receipt.Items[1].RunIds);
        Assert.Equal(new ExecutionPlanItemResult(RunOutcome.Failed, CleanupState.NotRequired, true, ReasonCodes.PreflightError), receipt.Items[1].Result);
        Assert.Equal(ExecutionPlanItemState.Skipped, receipt.Items[2].State);
        Assert.Empty(await db.ReadUnresolvedAsync());
        var message = VIvian.App.MainViewModel.PersistentPlanCompletionText(plan.PlanId, receipt);
        Assert.Contains("尚未启动", message);
        Assert.Contains("重新确认", message);
        Assert.DoesNotContain("清理", message);
        Assert.Equal(ExecutionPlanDisposition.Existing, (await coordinator.SubmitAsync(plan)).Disposition);
        await coordinator.WhenIdleAsync(); Assert.Equal(1, calls);
        reject = false;
        var next = Plan(); await coordinator.SubmitAsync(next); await coordinator.WhenIdleAsync();
        Assert.Equal(4, calls);
        Assert.Equal(ExecutionPlanState.Succeeded, (await db.ReadAsync(next.PlanId))!.State);
    }

    [Theory]
    [InlineData("unknown_preflight")]
    [InlineData("adapter_before_registration")]
    [InlineData("adapter_after_registration")]
    public async Task UnknownPreflightAndAdapterExceptionsRemainUnconfirmed(string stage)
    {
        var db = new LocalDatabase(Path.Combine(_root, "test.db")); await db.InitializeAsync();
        var calls = 0;
        var coordinator = new PersistentExecutionPlanCoordinator(db, async (item, register, ct) =>
        {
            calls++;
            if (stage == "adapter_after_registration") await register(Guid.NewGuid(), ct);
            throw new ExecutionPlanPreflightException("A later adapter validation is not a safe preflight");
        }, validateBeforeExecution: (_, _) => stage == "unknown_preflight"
            ? Task.FromException(new IOException("unknown")) : Task.CompletedTask);
        var plan = Plan(); await coordinator.SubmitAsync(plan); await coordinator.WhenIdleAsync();
        var receipt = (await db.ReadAsync(plan.PlanId))!;
        Assert.Equal(stage == "unknown_preflight" ? 0 : 1, calls);
        Assert.Equal(ReasonCodes.AdapterError, receipt.Items[0].Result!.ReasonCode);
        Assert.Equal(CleanupState.Unconfirmed, receipt.Items[0].Result!.Cleanup);
        Assert.False(receipt.Items[0].Result!.ConfigurationRestored);
        Assert.DoesNotContain("尚未启动", VIvian.App.MainViewModel.PersistentPlanCompletionText(plan.PlanId, receipt));
        Assert.Equal(ExecutionPlanItemState.Skipped, receipt.Items[1].State);
        var next = Plan(); await coordinator.SubmitAsync(next); await coordinator.WhenIdleAsync();
        Assert.Equal(stage == "unknown_preflight" ? 0 : 1, calls);
        Assert.Equal(ReasonCodes.CleanupUnconfirmed, (await db.ReadAsync(next.PlanId))!.ReasonCode);
    }

    public void Dispose() => TestDirectories.DeleteWithRetry(_root);
}
