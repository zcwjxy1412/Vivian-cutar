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
        Assert.Contains("synthetic", message);
        Assert.Contains("星铁", message);
        Assert.Contains("1 项", message);
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

    [Fact]
    public async Task SpecificPreflightCauseSurvivesDatabaseRoundTripWithoutRawExceptionText()
    {
        var db = new LocalDatabase(Path.Combine(_root, "detail.db")); await db.InitializeAsync();
        var plan = Plan();
        var coordinator = new PersistentExecutionPlanCoordinator(db, (_, _, _) => throw new Exception("must not execute"),
            validateBeforeExecution: (_, _) => throw new ExecutionPlanPreflightException("raw internal detail must not persist",
                ExecutionPlanPreflightIssue.ResourcesChanged));
        await coordinator.SubmitAsync(plan); await coordinator.WhenIdleAsync();
        var receipt = (await db.ReadAsync(plan.PlanId))!;
        Assert.Equal(ExecutionPlanPreflightIssue.ResourcesChanged, receipt.Items[0].Result!.PreflightIssue);
        var message = VIvian.App.MainViewModel.PersistentPlanCompletionText(plan.PlanId, receipt);
        Assert.Contains("资源使用规则", message); Assert.Contains("后续 2 项", message);
        Assert.DoesNotContain("raw internal", System.Text.Json.JsonSerializer.Serialize(receipt));
        Assert.Empty(await db.ReadUnresolvedAsync());
        // Simulate an actual pre-upgrade stored receipt: the additive field was absent, not null.
        var legacyJson = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(receipt))!;
        foreach (var row in legacyJson["Items"]!.AsArray())
            if (row?["Result"] is System.Text.Json.Nodes.JsonObject result) result.Remove("PreflightIssue");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_root, "detail.db")}"))
        {
            await connection.OpenAsync(); using var command = connection.CreateCommand();
            command.CommandText = "UPDATE execution_plans SET receipt_json=$json WHERE plan_id=$id";
            command.Parameters.AddWithValue("$json", legacyJson.ToJsonString());
            command.Parameters.AddWithValue("$id", plan.PlanId.ToString("D"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        var legacyReceipt = (await db.ReadAsync(plan.PlanId))!;
        Assert.Equal(ExecutionPlanPreflightIssue.Unspecified, legacyReceipt.Items[0].Result!.PreflightIssue);
        Assert.Contains("旧记录没有保存具体", VIvian.App.MainViewModel.PersistentPlanCompletionText(plan.PlanId, legacyReceipt));
    }

    [Fact]
    public void ChangedFieldsAreClassifiedWithoutGuessingFromExceptionText()
    {
        var item = Plan().Items[0];
        Assert.Equal(ExecutionPlanPreflightIssue.AccountChanged, VIvian.App.MainViewModel.ChangedPlanIssue(item, item with { AccountName = "renamed" }));
        Assert.Equal(ExecutionPlanPreflightIssue.TasksChanged, VIvian.App.MainViewModel.ChangedPlanIssue(item, item with { TaskPlanRevision = 2 }));
        Assert.Equal(ExecutionPlanPreflightIssue.ResourcesChanged, VIvian.App.MainViewModel.ChangedPlanIssue(item, item with { ResourceSummary = "changed" }));
        Assert.Equal(ExecutionPlanPreflightIssue.ConfigurationVersionChanged, VIvian.App.MainViewModel.ChangedPlanIssue(item, item with { ConfigurationRevision = "new" }));
        var legacy = System.Text.Json.JsonSerializer.Deserialize<ExecutionPlanItemResult>("{\"Outcome\":1,\"Cleanup\":0,\"ConfigurationRestored\":true,\"ReasonCode\":\"preflight_error\"}")!;
        Assert.Equal(ExecutionPlanPreflightIssue.Unspecified, legacy.PreflightIssue);
    }

    public void Dispose() => TestDirectories.DeleteWithRetry(_root);
}
