using Microsoft.Data.Sqlite;
using VIvian.Core;
using VIvian.Infrastructure;
using Xunit;
namespace VIvian.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vivian-tests", Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_root, "test.db");
    public PersistenceTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private async Task<LocalDatabase> CreateAsync()
    { var db = new LocalDatabase(DbPath); await db.InitializeAsync(); return db; }

    [Fact] public async Task InitializeIsIdempotentAndPreservesSettings()
    {
        var db = await CreateAsync();
        var initial = await db.LoadSettingsAsync();
        var saved = initial.Select(x => x with { InstallPath = @"E:\游戏 目录\" + x.Game }).ToList();
        await db.SaveSettingsAsync(saved);
        await db.InitializeAsync();
        var reloaded = await new LocalDatabase(DbPath).LoadSettingsAsync();
        Assert.Equal(saved.Select(x => x.InstallPath), reloaded.Select(x => x.InstallPath));
        Assert.All(reloaded, x => Assert.Equal(1L, x.Revision));
    }
    [Fact] public async Task ConflictRollsBackWholeBatchIncludingEarlierUpdates()
    {
        var db = await CreateAsync();
        var initial = await db.LoadSettingsAsync();
        var invalid = initial.Select(x => x with { InstallPath = "changed" }).ToList();
        invalid[2] = invalid[2] with { Revision = 55 };
        await Assert.ThrowsAsync<SettingsConflictException>(() => db.SaveSettingsAsync(invalid));
        Assert.Equal(initial, await db.LoadSettingsAsync());
    }
    [Fact] public async Task TwoWritersDoNotSilentlyOverwrite()
    {
        var db = await CreateAsync();
        var other = new LocalDatabase(DbPath);
        var a = await db.LoadSettingsAsync(); var b = await other.LoadSettingsAsync();
        await db.SaveSettingsAsync(a.Select(x => x with { InstallPath = "first" }).ToList());
        await Assert.ThrowsAsync<SettingsConflictException>(() => other.SaveSettingsAsync(b));
        Assert.All(await db.LoadSettingsAsync(), x => Assert.Equal("first", x.InstallPath));
    }
    [Fact] public async Task CancelledWriteDoesNotChangeDatabase()
    {
        var db = await CreateAsync(); var initial = await db.LoadSettingsAsync();
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.SaveSettingsAsync(initial, cts.Token));
        Assert.Equal(initial, await db.LoadSettingsAsync());
    }
    [Theory] [InlineData("name\nextra")] [InlineData("name\u0000extra")]
    public async Task InvalidSettingsAreRejectedBeforeWrite(string path)
    {
        var db = await CreateAsync(); var initial = await db.LoadSettingsAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => db.SaveSettingsAsync(initial.Select(x => x with { InstallPath=path }).ToList()));
        Assert.Equal(initial, await db.LoadSettingsAsync());
    }
    [Fact] public async Task DuplicateGameSetIsRejected()
    {
        var db = await CreateAsync(); var item = (await db.LoadSettingsAsync())[0];
        await Assert.ThrowsAsync<ArgumentException>(() => db.SaveSettingsAsync([item,item,item]));
    }
    [Fact] public async Task SqlLikePathIsStoredAsData()
    {
        var db = await CreateAsync(); var values = await db.LoadSettingsAsync();
        const string path = @"E:\'; DROP TABLE engine_settings; --";
        await db.SaveSettingsAsync(values.Select(x => x with { InstallPath = path }).ToList());
        Assert.All(await db.LoadSettingsAsync(), x => Assert.Equal(path, x.InstallPath));
    }
    [Fact] public async Task DiagnosticsSurviveReopenAndRespectLimit()
    {
        var db = await CreateAsync(); var at = DateTimeOffset.UtcNow;
        for (var i = 0; i < 4; i++) await db.AppendAsync(new(i.ToString(), at.AddMinutes(i), "基础自检，不是游戏任务"));
        var result = await new LocalDatabase(DbPath).ReadRecentAsync(2);
        Assert.Equal(new[] { "3", "2" }, result.Select(x => x.Id));
    }
    [Fact] public async Task DuplicateDiagnosticDoesNotOverwriteExistingEvidence()
    {
        var db = await CreateAsync();
        var record = new DiagnosticRecord("same", DateTimeOffset.UtcNow, "original");
        await db.AppendAsync(record);
        await Assert.ThrowsAsync<SqliteException>(() => db.AppendAsync(record with { Summary = "replacement" }));
        Assert.Equal("original", (await db.ReadRecentAsync(1))[0].Summary);
    }
    [Theory] [InlineData(0)] [InlineData(201)]
    public async Task InvalidHistoryLimitIsRejected(int limit)
    { var db = await CreateAsync(); await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => db.ReadRecentAsync(limit)); }
    [Fact] public async Task NewerSchemaIsNotReset()
    {
        await CreateAsync();
        using (var c = new SqliteConnection("Pooling=False;Data Source=" + DbPath))
        { await c.OpenAsync(); using var command = c.CreateCommand(); command.CommandText="PRAGMA user_version=99;"; await command.ExecuteNonQueryAsync(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => new LocalDatabase(DbPath).InitializeAsync());
        Assert.Equal(3, (await new LocalDatabase(DbPath).LoadSettingsAsync()).Count);
    }
    [Fact] public async Task CorruptDatabaseIsNotSilentlyRecreated()
    {
        var bytes = new byte[1024]; Array.Fill<byte>(bytes, 0x55);
        await File.WriteAllBytesAsync(DbPath, bytes);
        await Assert.ThrowsAsync<SqliteException>(() => new LocalDatabase(DbPath).InitializeAsync());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(DbPath));
    }
}
