using Microsoft.Data.Sqlite;
using VIvian.Core;
using VIvian.Core.Execution;
using VIvian.Infrastructure;
using Xunit;

namespace VIvian.Tests.Execution;

public sealed class PersistentExecutionPlanTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("furina-plans-").FullName;
    private string PathName => Path.Combine(_root, "plans.db");
    private async Task<LocalDatabase> OpenAsync()
    {
        var db = new LocalDatabase(PathName);
        await db.InitializeAsync();
        return db;
    }
    private static PreparedExecutionPlan Plan() => new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(10),
        [new(0, GameId.StarRail, Guid.NewGuid(), "星铁测试", "routine", "rev-1", 1, "saved_account_settings", "已保存任务"),
         new(1, GameId.Genshin, Guid.NewGuid(), "原神测试", "routine", "rev-2", 2, "saved_account_settings", "已保存任务")]);
    private static ExecutionPlanItemResult Success => new(RunOutcome.Success, CleanupState.Confirmed, true, "completed");

    [Fact]
    public async Task ConcurrentAcceptIsIdempotentAndDifferentPayloadConflicts()
    {
        var db = await OpenAsync();
        var plan = Plan();
        var submissions = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            new LocalDatabase(PathName).AcceptAsync(plan))));
        Assert.Single(submissions, x => x.Disposition == ExecutionPlanDisposition.Accepted);
        Assert.Equal(7, submissions.Count(x => x.Disposition == ExecutionPlanDisposition.Existing));
        Assert.Single(submissions.Select(x => x.Receipt!.Items[0].ChildBatchId).Distinct());
        Assert.Equal(ExecutionPlanDisposition.Conflict,
            (await db.AcceptAsync(plan with { Items = plan.Items.Reverse().Select((x, i) => x with { Ordinal = i }).ToArray() })).Disposition);
        Assert.Null(await db.ReadAsync(Guid.NewGuid()));
        Assert.Single(await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() => db.TryStartAsync(plan.PlanId)))), x => x);
    }

    [Fact]
    public async Task ExactRunLinksSurviveReopenAndCannotCrossItems()
    {
        var db = await OpenAsync(); var plan = Plan(); var runId = Guid.NewGuid();
        await db.AcceptAsync(plan); await db.TryStartAsync(plan.PlanId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.StartItemAsync(plan.PlanId, 1));
        await db.StartItemAsync(plan.PlanId, 0);
        await db.RegisterRunAsync(plan.PlanId, 0, runId);
        await db.RegisterRunAsync(plan.PlanId, 0, runId);
        Assert.Equal(runId, Assert.Single((await new LocalDatabase(PathName).ReadAsync(plan.PlanId))!.Items[0].RunIds));
        await db.CompleteItemAsync(plan.PlanId, 0, Success);
        await db.StartItemAsync(plan.PlanId, 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.RegisterRunAsync(plan.PlanId, 1, runId));
        await db.RegisterRunAsync(plan.PlanId, 1, Guid.NewGuid());
        await db.CompleteItemAsync(plan.PlanId, 1, Success);
        await db.CompleteAsync(plan.PlanId, ExecutionPlanState.Succeeded, null);
        Assert.Equal(ExecutionPlanState.Succeeded, (await db.ReadAsync(plan.PlanId))!.State);
    }

    [Theory]
    [InlineData(RunOutcome.Failed, CleanupState.Confirmed, true)]
    [InlineData(RunOutcome.Cancelled, CleanupState.Confirmed, true)]
    [InlineData(RunOutcome.Success, CleanupState.Unconfirmed, true)]
    [InlineData(RunOutcome.Success, CleanupState.Confirmed, false)]
    public async Task UnsafeResultNeverAllowsNext(RunOutcome outcome, CleanupState cleanup, bool restored)
    {
        var db = await OpenAsync(); var plan = Plan();
        await db.AcceptAsync(plan); await db.TryStartAsync(plan.PlanId); await db.StartItemAsync(plan.PlanId, 0);
        await db.CompleteItemAsync(plan.PlanId, 0, new(outcome, cleanup, restored, "fixture"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.StartItemAsync(plan.PlanId, 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.CompleteAsync(plan.PlanId, ExecutionPlanState.Succeeded, null));
        await db.CompleteAsync(plan.PlanId, ExecutionPlanState.Stopped, "fixture");
        Assert.Equal(ExecutionPlanItemState.Skipped, (await db.ReadAsync(plan.PlanId))!.Items[1].State);
    }

    [Fact]
    public async Task RestartInterruptsAcceptedAndRunningWithoutReplay()
    {
        var db = await OpenAsync(); var accepted = Plan(); var running = Plan();
        await db.AcceptAsync(accepted); await db.AcceptAsync(running);
        await db.TryStartAsync(running.PlanId); await db.StartItemAsync(running.PlanId, 0);
        Assert.Equal(2, await new LocalDatabase(PathName).InterruptUnfinishedAsync());
        Assert.Equal(0, await db.InterruptUnfinishedAsync());
        foreach (var plan in new[] { accepted, running })
        {
            Assert.Equal(ExecutionPlanDisposition.Existing, (await db.AcceptAsync(plan)).Disposition);
            Assert.False(await db.TryStartAsync(plan.PlanId));
            Assert.Equal(ExecutionPlanState.Interrupted, (await db.ReadAsync(plan.PlanId))!.State);
        }
        Assert.Equal(ExecutionPlanItemState.Interrupted, (await db.ReadAsync(running.PlanId))!.Items[0].State);
    }

    [Fact]
    public async Task ExpiredOrInvalidPlanDoesNotPersist()
    {
        var db = await OpenAsync(); var expired = Plan() with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) };
        Assert.Equal(ExecutionPlanDisposition.Rejected, (await db.AcceptAsync(expired)).Disposition);
        Assert.Null(await db.ReadAsync(expired.PlanId));
        var invalid = Plan() with { Items = [] };
        await Assert.ThrowsAsync<ArgumentException>(() => db.AcceptAsync(invalid));
    }

    [Fact]
    public async Task PreparedVersionSevenIsUpgradedOnlyInIsolatedSnapshot()
    {
        var source = Path.Combine(_root, "source"); Directory.CreateDirectory(source);
        var path = Path.Combine(source, "vivian.db");
        await new LocalDatabase(path).InitializeAsync();
        await using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await c.OpenAsync(); using var cmd = c.CreateCommand();
            cmd.CommandText = "DROP TABLE execution_plan_runs; DROP TABLE execution_plans; PRAGMA user_version=7; PRAGMA application_id=1447646798;";
            await cmd.ExecuteNonQueryAsync();
        }
        var destination = Path.Combine(_root, "snapshot");
        await UnifiedProfilePreparation.CopyPreparedProfileAsync(source, destination, CancellationToken.None);
        UnifiedProfilePreparation.VerifyPrepared(destination);
        await using var original = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await original.OpenAsync(); using var check = original.CreateCommand(); check.CommandText = "PRAGMA user_version;";
        Assert.Equal(7L, await check.ExecuteScalarAsync());
        Assert.True(File.Exists(Path.Combine(destination, "vivian.db.pre-v8.bak")));
        await new LocalDatabase(Path.Combine(destination, "vivian.db")).AcceptAsync(Plan());
        await Assert.ThrowsAsync<InvalidOperationException>(() => UnifiedProfilePreparation.CopyPreparedProfileAsync(
            destination, Path.Combine(_root, "must-not-publish"), CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(_root, "must-not-publish")));
    }

    [Fact]
    public async Task CorruptReceiptFailsClosedOnReadAndStart()
    {
        var db = await OpenAsync(); var plan = Plan(); await db.AcceptAsync(plan);
        await using var c = new SqliteConnection($"Data Source={PathName};Pooling=False"); await c.OpenAsync();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE execution_plans SET receipt_json=json_set(receipt_json,'$.Items',NULL);";
        await cmd.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => db.ReadAsync(plan.PlanId));
        await Assert.ThrowsAsync<InvalidDataException>(() => db.TryStartAsync(plan.PlanId));
    }

    [Fact]
    public async Task ConcurrentStartItemHasExactlyOneWinner()
    {
        var db = await OpenAsync(); var plan = Plan();
        await db.AcceptAsync(plan); await db.TryStartAsync(plan.PlanId);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            try { await new LocalDatabase(PathName).StartItemAsync(plan.PlanId, 0); return true; }
            catch (InvalidOperationException) { return false; }
        })));
        Assert.Single(results, x => x);
    }

    [Fact]
    public async Task VersionSevenMigratesWithReadableBackupAndUnchangedSettings()
    {
        var db = await OpenAsync();
        var account = await db.AddAccountAsync(GameId.StarRail, "迁移测试", "测试", null);
        await using (var c = new SqliteConnection($"Data Source={PathName};Pooling=False"))
        {
            await c.OpenAsync(); using var cmd = c.CreateCommand();
            cmd.CommandText = "DROP TABLE execution_plan_runs; DROP TABLE execution_plans; PRAGMA user_version=7;";
            await cmd.ExecuteNonQueryAsync();
        }
        await db.InitializeAsync();
        Assert.Equal(account, (await db.LoadAccountsAsync(GameId.StarRail)).Single(x => x.Id == account.Id));
        await using var backup = new SqliteConnection($"Data Source={PathName}.pre-v8.bak;Mode=ReadOnly;Pooling=False");
        await backup.OpenAsync(); using var check = backup.CreateCommand(); check.CommandText = "PRAGMA user_version;";
        Assert.Equal(7L, await check.ExecuteScalarAsync());
        Assert.Equal(ExecutionPlanDisposition.Accepted, (await db.AcceptAsync(Plan())).Disposition);
    }

    public void Dispose() => TestDirectories.DeleteWithRetry(_root);
}
