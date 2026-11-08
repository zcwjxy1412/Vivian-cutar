using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using VIvian.Infrastructure;
using Xunit;

namespace VIvian.Tests;

/// <summary>
/// Phase 0 regression: older and newer VIvian builds share one data directory. A build must refuse a newer database
/// with a typed, explainable error and must not modify it; the current build must open every older version.
/// </summary>
public sealed class StartupCompatibilityTests
{
    [Fact]
    public async Task NewerDatabaseIsRefusedWithVersionsAndLeftByteForByteUnchanged()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vivian-compat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "vivian.db");
        try
        {
            await using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await c.OpenAsync();
                using var cmd = c.CreateCommand();
                cmd.CommandText = $"PRAGMA journal_mode=WAL; CREATE TABLE future(x); PRAGMA user_version={LocalDatabase.CurrentSchemaVersion + 1};";
                await cmd.ExecuteNonQueryAsync();
            }
            SqliteConnection.ClearAllPools();
            var before = SHA256.HashData(await File.ReadAllBytesAsync(path));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new LocalDatabase(path).InitializeAsync());
            Assert.True(DatabaseVersionTooNew.TryRead(ex, out var found, out var supported));
            Assert.Equal(LocalDatabase.CurrentSchemaVersion + 1, found);
            Assert.Equal(LocalDatabase.CurrentSchemaVersion, supported);
            Assert.Contains($"v{found}", ex.Message, StringComparison.Ordinal);
            SqliteConnection.ClearAllPools();
            Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(path)));
            Assert.Empty(Directory.GetFiles(dir, "*.bak"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TestDirectories.DeleteWithRetry(dir);
        }
    }

    [Fact]
    public async Task CurrentBuildOpensItsOwnDatabaseRepeatedly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vivian-compat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "vivian.db");
            await new LocalDatabase(path).InitializeAsync();
            await new LocalDatabase(path).InitializeAsync(); // second launch
            SqliteConnection.ClearAllPools();
            await using var c = new SqliteConnection($"Data Source={path};Pooling=False");
            await c.OpenAsync();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA user_version;";
            Assert.Equal((long)LocalDatabase.CurrentSchemaVersion, (long)(await cmd.ExecuteScalarAsync())!);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TestDirectories.DeleteWithRetry(dir);
        }
    }
}
