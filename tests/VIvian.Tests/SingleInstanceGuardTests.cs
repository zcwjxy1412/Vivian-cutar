using VIvian.Infrastructure;
using Xunit;

namespace VIvian.Tests;

public sealed class SingleInstanceGuardTests
{
    private static string NewDataDirectory() => Path.Combine(Path.GetTempPath(), "vivian-lock", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SecondInstanceForSameDataDirectoryIsRefusedUntilFirstExits()
    {
        var dir = NewDataDirectory();
        using (var first = SingleInstanceGuard.Acquire(dir))
        {
            Assert.Equal(InstanceLockResult.Acquired, first.Result);
            using var second = SingleInstanceGuard.Acquire(dir);
            Assert.Equal(InstanceLockResult.AlreadyRunning, second.Result);
            Assert.False(second.IsOwner);
        }
        using var third = SingleInstanceGuard.Acquire(dir);
        Assert.Equal(InstanceLockResult.Acquired, third.Result);
    }

    [Fact]
    public void DifferentDataDirectoriesAreIndependent()
    {
        using var a = SingleInstanceGuard.Acquire(NewDataDirectory());
        using var b = SingleInstanceGuard.Acquire(NewDataDirectory());
        Assert.True(a.IsOwner);
        Assert.True(b.IsOwner);
    }

    [Fact]
    public void NameIgnoresCaseAndTrailingSeparator()
    {
        var dir = NewDataDirectory();
        Assert.Equal(SingleInstanceGuard.NameFor(dir), SingleInstanceGuard.NameFor(dir.ToUpperInvariant() + Path.DirectorySeparatorChar));
        Assert.StartsWith(@"Local\VIvian-", SingleInstanceGuard.NameFor(dir));
    }

    [Fact]
    public void LockAbandonedByACrashedOwnerIsReportedSoTheAppReconciles()
    {
        var dir = NewDataDirectory();
        var name = SingleInstanceGuard.NameFor(dir);
        // Simulates a crashed instance: its thread takes the mutex and ends without releasing it.
        using var mutex = new Mutex(false, name);
        var crashed = new Thread(() => mutex.WaitOne());
        crashed.Start();
        crashed.Join();

        using var guard = SingleInstanceGuard.Acquire(dir);
        Assert.Equal(InstanceLockResult.AcquiredAfterAbandoned, guard.Result);
        Assert.True(guard.IsOwner);
        using var other = SingleInstanceGuard.Acquire(dir);
        Assert.Equal(InstanceLockResult.AlreadyRunning, other.Result);
    }

    [Fact]
    public void AccessDeniedIsNeverTreatedAsNobodyRunning()
    {
        using var guard = SingleInstanceGuard.Acquire(NewDataDirectory(), _ => throw new UnauthorizedAccessException());
        Assert.Equal(InstanceLockResult.AccessDenied, guard.Result);
        Assert.False(guard.IsOwner);
    }

    [Fact]
    public async Task ConcurrentStartsProduceExactlyOneOwner()
    {
        var dir = NewDataDirectory();
        using var start = new Barrier(8);
        var guards = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            start.SignalAndWait();
            return SingleInstanceGuard.Acquire(dir);
        })));
        try
        {
            Assert.Equal(1, guards.Count(g => g.IsOwner));
            Assert.Equal(7, guards.Count(g => g.Result == InstanceLockResult.AlreadyRunning));
        }
        finally
        {
            foreach (var g in guards) g.Dispose();
        }
    }

    [Fact]
    public async Task ReleaseWorksFromAnyThread()
    {
        var dir = NewDataDirectory();
        var guard = SingleInstanceGuard.Acquire(dir);
        await Task.Run(guard.Dispose); // a different thread than the one that holds the mutex
        using var next = SingleInstanceGuard.Acquire(dir);
        Assert.True(next.IsOwner);
    }
}
