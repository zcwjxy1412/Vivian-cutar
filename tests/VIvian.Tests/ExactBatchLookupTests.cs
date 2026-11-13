using VIvian.Core;
using VIvian.Infrastructure;
using Xunit;

namespace VIvian.Tests;

public sealed class ExactBatchLookupTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("vivian-batch-lookup-").FullName;

    [Fact]
    public async Task ExactBatchIsFoundOutsideRecentWindowAndUnknownNeverFallsBack()
    {
        var db = new LocalDatabase(Path.Combine(root, "test.db"));
        await db.InitializeAsync();
        var target = await db.BeginBatchAsync(GameId.StarRail, actionId: Guid.NewGuid());
        await db.SaveBatchAsync(target with { StartedAt = DateTimeOffset.UtcNow.AddDays(-1), State = "finished" });
        for (var i = 0; i < 21; i++) await db.BeginBatchAsync(GameId.Genshin);
        Assert.DoesNotContain(await db.LoadRecentBatchesAsync(), b => b.Id == target.Id);
        var found = await db.LoadBatchAsync(target.Id);
        Assert.NotNull(found);
        Assert.Equal(target.Id, found.Id);
        Assert.Equal(GameId.StarRail, found.Game);
        Assert.Equal("finished", found.State);
        Assert.Null(await db.LoadBatchAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentException>(() => db.LoadBatchAsync(Guid.Empty));
    }

    [Fact]
    public async Task ExactBatchSurvivesReopenAndReflectsInterruptedReconciliation()
    {
        var path = Path.Combine(root, "test.db");
        var db = new LocalDatabase(path);
        await db.InitializeAsync();
        var target = await db.BeginBatchAsync(GameId.StarRail, actionId: Guid.NewGuid());
        var reopened = new LocalDatabase(path);
        Assert.Equal("running", (await reopened.LoadBatchAsync(target.Id))!.State);
        await reopened.ReconcileInterruptedBatchesAsync();
        Assert.Equal("interrupted", (await reopened.LoadBatchAsync(target.Id))!.State);
        Assert.Equal(8, LocalDatabase.CurrentSchemaVersion);
    }

    public void Dispose() => Directory.Delete(root, true);
}
